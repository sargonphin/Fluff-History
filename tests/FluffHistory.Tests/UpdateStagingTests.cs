using System.Net;
using System.Security.Cryptography;
using FluffHistory.Application;
using FluffHistory.Windows;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class UpdateStagingTests
{
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(response()); } }
    private sealed class Verifier : IUpdateFileVerifier
    {
        public int Calls; public UpdateFailure Failure;
        public void Verify(string path, FileStream heldFile, UpdateRelease release)
        {
            Calls++;
            if (heldFile.Length != release.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(heldFile)), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(UpdateFailure.Integrity);
            if (Failure != UpdateFailure.None) throw new UpdateException(Failure);
        }
    }
    private sealed class Progress : IProgress<long> { public long Bytes; public void Report(long bytes) => Bytes = bytes; }
    private sealed class StreamingContent(byte[] bytes, bool interrupt = false) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new InterruptedStream(bytes, interrupt));
    }
    private sealed class InterruptedStream(byte[] bytes, bool interrupt) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            interrupt ? ValueTask.FromException<int>(new IOException("Controlled interrupted download.")) : base.ReadAsync(buffer, cancellationToken);
    }
    private static UpdateRelease Release(byte[] bytes) => UpdateReleaseTests.Parse(UpdateReleaseTests.Release("v1.10.0"))[0] with
    { Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    [WindowsFact]
    public async Task StreamHashReadbackReuseAndRetainedFileRejectsReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); byte[] bytes = [1, 2, 3, 4]; var release = Release(bytes); var progress = new Progress();
        var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var http = new HttpClient(handler); var verifier = new Verifier(); var staging = new WindowsUpdateStaging(http, verifier) { Root = temp.Child("Updates") };
        var staged = await staging.StageAsync(release, progress, default); Assert.Equal(4, progress.Bytes); Assert.Equal(bytes, File.ReadAllBytes(staged.Path));
        Assert.Equal(staged, await staging.StageAsync(release, progress, default)); Assert.Equal(1, handler.Calls);
        using (staging.OpenVerified(staged))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(staged.Path, "replacement"));
            Assert.Throws<IOException>(() => File.Delete(staged.Path));
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(staged.Path)!, temp.Child("renamed")));
        }
        File.WriteAllBytes(staged.Path, [9, 9, 9, 9]);
        Assert.Equal(UpdateFailure.Integrity, Assert.Throws<UpdateException>(() => staging.OpenVerified(staged)).Failure);
        Assert.Throws<UpdateException>(() => staging.OpenVerified(staged with { Path = temp.Child("random.exe") }));
    }
    [WindowsTheory]
    [InlineData(2)][InlineData(5)][InlineData(4)]
    public async Task ShortExtraOrWrongHashBodiesCleanCandidate(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); var release = Release([1, 2, 3, 4]);
        var bytes = Enumerable.Repeat((byte)9, length).ToArray();
        var content = new StreamingContent(bytes);
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = content }));
        var staging = new WindowsUpdateStaging(http, new Verifier()) { Root = temp.Child("Updates") };
        await Assert.ThrowsAsync<UpdateException>(() => staging.StageAsync(release, new Progress(), default));
        Assert.Equal("ownership-v1.json", Path.GetFileName(Assert.Single(Directory.GetFiles(staging.GetDirectory(release)))));
    }
    [WindowsFact]
    public async Task SignatureAndWrongProductFailuresNeverPublishExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); byte[] bytes = [1, 2, 3]; var release = Release(bytes);
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var verifier = new Verifier { Failure = UpdateFailure.Signature }; var staging = new WindowsUpdateStaging(http, verifier) { Root = temp.Child("Updates") };
        foreach (var failure in new[] { UpdateFailure.Signature, UpdateFailure.SignerNotApproved, UpdateFailure.WrongProduct })
        {
            verifier.Failure = failure;
            Assert.Equal(failure, (await Assert.ThrowsAsync<UpdateException>(() => staging.StageAsync(release, new Progress(), default))).Failure);
            Assert.Equal("ownership-v1.json", Path.GetFileName(Assert.Single(Directory.GetFiles(staging.GetDirectory(release)))));
        }
    }
    [WindowsFact]
    public async Task DirectoryJunctionAndHttpRedirectRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); var release = Release([1, 2, 3]);
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.Found) { Headers = { Location = new Uri("http://github.com/installer") } }));
        var staging = new WindowsUpdateStaging(http, new Verifier()) { Root = temp.Child("Updates") };
        await Assert.ThrowsAsync<UpdateException>(() => staging.StageAsync(release, new Progress(), default));
        Directory.CreateDirectory(temp.Child("foreign"));
        Directory.CreateSymbolicLink(temp.Child("linked"), temp.Child("foreign"));
        var linked = new WindowsUpdateStaging(http, new Verifier()) { Root = temp.Child("linked") };
        await Assert.ThrowsAsync<IOException>(() => linked.StageAsync(release, new Progress(), default));
    }
    [WindowsFact]
    public void RealWindowsTrustRejectsUnsignedAndPinsValidatedMicrosoftSigner()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); var unsigned = temp.Child("unsigned.exe"); File.WriteAllText(unsigned, "not a PE");
        using (var file = File.OpenRead(unsigned))
            Assert.Equal(UpdateFailure.Signature, Assert.Throws<UpdateException>(() => WindowsUpdateFileVerifier.VerifySignature(unsigned, file.SafeFileHandle, new ApprovedUpdateSignerPolicy([]))).Failure);
        var signed = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        using var input = File.OpenRead(signed);
        // Valid Windows signature alone must still fail the empty product signer policy.
        Assert.Equal(UpdateFailure.SignerNotApproved, Assert.Throws<UpdateException>(() => WindowsUpdateFileVerifier.VerifySignature(signed, input.SafeFileHandle, new ApprovedUpdateSignerPolicy([]))).Failure);
        var capture = new CaptureSigner(); WindowsUpdateFileVerifier.VerifySignature(signed, input.SafeFileHandle, capture);
        WindowsUpdateFileVerifier.VerifySignature(signed, input.SafeFileHandle, new ApprovedUpdateSignerPolicy([capture.CertificateHash!]));
        var wrongProduct = Release(File.ReadAllBytes(signed));
        Assert.Equal(UpdateFailure.WrongProduct, Assert.Throws<UpdateException>(() =>
            new WindowsUpdateFileVerifier(capture).Verify(signed, input, wrongProduct)).Failure);
    }
    private sealed class CaptureSigner : IUpdateSignerPolicy
    {
        internal string? CertificateHash;
        public bool Accept(ReadOnlySpan<byte> certificateDer)
        { CertificateHash = Convert.ToHexString(SHA256.HashData(certificateDer)); return true; }
    }
    [WindowsFact]
    public async Task InterruptedCandidateIsRemovedAndOwnedRetentionIsBoundedWithoutSweepingUnknownFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); byte[] bytes = [1, 2, 3]; var interrupted = true;
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = new StreamingContent(bytes, interrupted) }));
        var staging = new WindowsUpdateStaging(http, new Verifier()) { Root = temp.Child("Updates") };
        await Assert.ThrowsAsync<IOException>(() => staging.StageAsync(Release(bytes), new Progress(), default));
        Assert.False(File.Exists(Path.Combine(staging.GetDirectory(Release(bytes)), "candidate.tmp")));
        interrupted = false;
        var unknown = Path.Combine(staging.Root, "0.1.0"); Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "personal.txt"), "preserve");
        for (var i = 0; i < 5; i++)
        {
            var tag = "v1.10." + i;
            var release = UpdateReleaseTests.Parse(UpdateReleaseTests.Release(tag))[0] with { Size = bytes.Length, Sha256 = Release(bytes).Sha256 };
            await staging.StageAsync(release, new Progress(), default);
        }
        Assert.True(File.Exists(Path.Combine(unknown, "personal.txt")));
        Assert.InRange(Directory.GetDirectories(staging.Root).Length, 2, 4); // three owned plus unrelated preserved directory
    }
}

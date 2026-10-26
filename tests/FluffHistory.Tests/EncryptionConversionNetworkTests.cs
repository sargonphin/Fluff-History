using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class EncryptionConversionNetworkTests
{
    [SmbFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void RealUncSameDirectoryPayloadAndControlPivotsRoundTrip()
    {
        using var environment = new SmbTestEnvironment(); var path = Path.Combine(environment.Remote, "repository"); string digest;
        using (var repository = Repository.Create(path))
        {
            EncryptionTests.Add(repository, bytes: new byte[262145]); EncryptionTests.Add(repository, "empty.bin", []);
            using var reader = repository.OpenReader(); digest = Repository.SemanticDigest(reader, default);
        }
        var options = new EncryptionConversionOptions(environment.Local, new(MaximumOutputBytes: 32 * 1024 * 1024, MaximumScratchBytes: 16 * 1024 * 1024));
        var encrypted = Repository.ConvertEncryption(path, true, EncryptionTests.Password, options);
        Assert.Equal(digest, encrypted.SemanticDigest);
        using (var repository = Repository.OpenWithWindowsUser(path))
        using (var reader = repository.OpenReader())
            foreach (var version in reader.EnumerateCurrent()) { using var payload = reader.OpenVersionPayload(version.Identity)!; payload.Content.CopyTo(Stream.Null); }
        var plain = Repository.ConvertEncryption(path, false, options: options);
        Assert.Equal(digest, plain.SemanticDigest);
        using var reopened = Repository.Open(path); using var after = reopened.OpenReader();
        Assert.Equal(digest, Repository.SemanticDigest(after, default)); Assert.Equal(2, after.EnumerateCurrent().Count());
    }
}

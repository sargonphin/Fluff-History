using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluffHistory.Storage;
using FluffHistory.Core;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class OneDriveRecoveryTests
{
    private static readonly string Tag = "\"synthetic-etag\"";
    private static AuthorityIdentity Authority(Repository repository)
    { using var reader = repository.OpenReader(); return reader.Authority; }
    private static OneDriveRecoveryReference Enable(Repository repository, OneDriveRecoverySecret secret, Guid? prior = null)
    {
        var reference = secret.Reference("synthetic-item-" + secret.GenerationId.ToString("N"), Tag);
        repository.PublishOneDriveRecovery(secret, reference, Authority(repository), prior); return reference;
    }

    [Fact]
    public void ExactTwoHalvesRecoverWithoutMasterUploadAndRemainIndependentOfPasswordRotation()
    {
        using var temp = new TemporaryDirectory();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        EncryptionTests.Add(repository);
        var baseline = TemporaryDirectory.Snapshot(temp.Child());
        using var secret = OneDriveRecoverySecret.Create(repository.Identity.RepositoryId);
        using var other = OneDriveRecoverySecret.Create(repository.Identity.RepositoryId);
        Assert.Equal(32, secret.Key.Length); Assert.NotEqual(secret.Key.ToArray(), other.Key.ToArray());
        Assert.NotEqual(secret.GenerationId, other.GenerationId);
        var reference = Enable(repository, secret);
        Assert.Equal(reference, Repository.InspectOneDriveRecovery(temp.Child()));
        var sidecar = OneDriveRecoverySidecar.Read(temp.Child(), repository.Identity.RepositoryId);
        var sidecarText = Encoding.UTF8.GetString(sidecar.Bytes());
        Assert.DoesNotContain(Convert.ToBase64String(secret.Key), sidecarText);
        var packetContext = OneDriveRecoverySidecar.Context(RepositoryBootstrap.Read(temp.Child()), reference);
        using var wrapper = new RepositoryCryptography(secret.Key.ToArray(), packetContext);
        var packet = wrapper.Unprotect(Convert.FromBase64String(sidecar.ProtectedKey), "onedrive-appfolder-master-v1");
        var cloud = secret.EncodeCloudObject();
        try
        {
            Assert.Equal(64, packet.Length);
            Assert.DoesNotContain(Convert.ToBase64String(packet.AsSpan(32)), Encoding.UTF8.GetString(cloud));
            var cloudKey = Convert.FromBase64String(JsonNode.Parse(cloud)!["key"]!.GetValue<string>());
            try { Assert.Equal(secret.Key.ToArray(), cloudKey); Assert.False(packet.AsSpan(32).SequenceEqual(cloudKey)); }
            finally { CryptographicOperations.ZeroMemory(cloudKey); }
            using var parsed = OneDriveRecoverySecret.ParseCloudObject(cloud, reference);
            using var recovered = Repository.OpenWithOneDriveRecovery(temp.Child(), parsed);
            Assert.Equal(Authority(repository), Authority(recovered));
        }
        finally { CryptographicOperations.ZeroMemory(packet); CryptographicOperations.ZeroMemory(cloud); }
        var after = TemporaryDirectory.Snapshot(temp.Child()); Assert.True(after.Remove(OneDriveRecoverySidecar.Name)); Assert.Equal(baseline, after);
        var beforeSidecar = sidecar.Bytes();
        repository.ChangeRecoveryPassword("new synthetic portable password");
        Assert.Throws<RecoveryPasswordAuthenticationException>(() => Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password));
        using var changedPassword = Repository.OpenWithPassword(temp.Child(), "new synthetic portable password");
        using var afterRotation = Repository.OpenWithOneDriveRecovery(temp.Child(), secret);
        Assert.Equal(beforeSidecar, OneDriveRecoverySidecar.Read(temp.Child(), repository.Identity.RepositoryId).Bytes());
        Assert.Equal(reference.GenerationId, Repository.InspectOneDriveRecovery(temp.Child())!.GenerationId);
    }

    [Theory]
    [InlineData("wrong-secret")][InlineData("wrong-generation")][InlineData("wrong-repository")]
    [InlineData("wrong-bootstrap")][InlineData("tampered-envelope")][InlineData("changed-item")]
    public void WrongHalfOrBindingCannotRecoverOrMutateHistory(string fault)
    {
        using var temp = new TemporaryDirectory();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        using var secret = OneDriveRecoverySecret.Create(repository.Identity.RepositoryId);
        var reference = Enable(repository, secret);
        var sidecar = OneDriveRecoverySidecar.Read(temp.Child(), repository.Identity.RepositoryId);
        var path = Path.Combine(temp.Child(), OneDriveRecoverySidecar.Name);
        if (fault == "wrong-generation")
        {
            var generation = Guid.NewGuid();
            sidecar = sidecar with { GenerationId = generation, FileName = $"recovery-{reference.RepositoryId:N}-{generation:N}.json" };
        }
        if (fault == "wrong-repository") sidecar = sidecar with { RepositoryId = Guid.NewGuid() };
        if (fault == "changed-item") sidecar = sidecar with { ItemId = "another-item" };
        if (fault == "tampered-envelope")
        { var bytes = Convert.FromBase64String(sidecar.ProtectedKey); bytes[^1] ^= 1; sidecar = sidecar with { ProtectedKey = Convert.ToBase64String(bytes) }; }
        File.WriteAllBytes(path, sidecar.Bytes());
        if (fault == "wrong-bootstrap")
        {
            var bootstrap = RepositoryBootstrap.Read(temp.Child());
            File.WriteAllBytes(Path.Combine(temp.Child(), "repository.json"), (bootstrap with { CreatedUtc = bootstrap.CreatedUtc.AddSeconds(1) }).Bytes());
        }
        var before = TemporaryDirectory.Snapshot(temp.Child());
        if (fault == "wrong-secret")
        {
            using var wrong = OneDriveRecoverySecret.Create(repository.Identity.RepositoryId);
            Assert.Throws<InvalidDataException>(() => Repository.OpenWithOneDriveRecovery(temp.Child(), wrong));
        }
        else Assert.Throws<InvalidDataException>(() => Repository.OpenWithOneDriveRecovery(temp.Child(), secret));
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
        if (fault != "wrong-bootstrap") { using var portable = Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password); }
    }

    [Theory]
    [InlineData("oversize")][InlineData("unknown-schema")][InlineData("wrong-repository")]
    [InlineData("wrong-generation")][InlineData("wrong-key")][InlineData("short-key")]
    [InlineData("unknown-field")][InlineData("duplicate-field")][InlineData("malformed")]
    public void CloudObjectParsingIsBoundedCanonicalAndBoundToExactGeneration(string fault)
    {
        using var secret = OneDriveRecoverySecret.Create(Guid.NewGuid());
        var reference = secret.Reference("synthetic-item", Tag); var original = secret.EncodeCloudObject();
        var node = JsonNode.Parse(original)!;
        if (fault == "unknown-schema") node["schema"] = 2;
        if (fault == "wrong-repository") node["repositoryId"] = Guid.NewGuid();
        if (fault == "wrong-generation") node["generationId"] = Guid.NewGuid();
        if (fault == "wrong-key") node["key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (fault == "short-key") node["key"] = Convert.ToBase64String(new byte[31]);
        if (fault == "unknown-field") node["unexpected"] = true;
        var bytes = fault switch
        {
            "oversize" => new byte[16385], "malformed" => "{"u8.ToArray(),
            "duplicate-field" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal)),
            _ => Encoding.UTF8.GetBytes(node.ToJsonString())
        };
        try { Assert.Throws<InvalidDataException>(() => OneDriveRecoverySecret.ParseCloudObject(bytes, reference)); }
        finally { CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("CandidateFlushed", false)]
    [InlineData("BeforeInstall", false)]
    [InlineData("AfterInstall", true)]
    [InlineData("BeforeConfirmation", true)]
    public void PublicationFaultsPreserveHistoryAndLeaveOnlyClassifiableLocalState(string faultName, bool installed)
    {
        var fault = Enum.Parse<OneDriveRecoveryStage>(faultName);
        using var temp = new TemporaryDirectory();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        using var secret = OneDriveRecoverySecret.Create(repository.Identity.RepositoryId);
        var before = TemporaryDirectory.Snapshot(temp.Child());
        repository.OneDriveRecoveryObserved = stage => { if (stage == fault) throw new IOException("Synthetic publication fault."); };
        if (fault == OneDriveRecoveryStage.AfterInstall) Enable(repository, secret); // Exact reconciliation proves success.
        else if (installed) Assert.Throws<OneDriveRecoveryUncertainException>(() => Enable(repository, secret));
        else Assert.Throws<IOException>(() => Enable(repository, secret));
        var after = TemporaryDirectory.Snapshot(temp.Child());
        if (installed)
        { using var recovered = Repository.OpenWithOneDriveRecovery(temp.Child(), secret); Assert.True(after.Remove(OneDriveRecoverySidecar.Name)); }
        else Assert.Null(Repository.InspectOneDriveRecovery(temp.Child()));
        Assert.Equal(before, after); Assert.Empty(Directory.GetFiles(temp.Child(), "onedrive-recovery-*.tmp"));
    }

    [Fact]
    public void RepairDoesNotRetireOldSlotBeforeReplacementAndDisableDoesNotTouchOtherRepositories()
    {
        using var temp = new TemporaryDirectory();
        var repositories = Enumerable.Range(0, 3).Select(index => Repository.CreateEncrypted(Path.Combine(temp.Child(), index.ToString()), EncryptionTests.Password)).ToArray();
        var secrets = repositories.Select(repository => OneDriveRecoverySecret.Create(repository.Identity.RepositoryId)).ToArray();
        try
        {
            for (int i = 0; i < repositories.Length; i++) Enable(repositories[i], secrets[i]);
            using var replacement = OneDriveRecoverySecret.Create(repositories[0].Identity.RepositoryId);
            repositories[0].OneDriveRecoveryObserved = stage => { if (stage == OneDriveRecoveryStage.BeforeInstall) throw new IOException("Synthetic repair fault."); };
            Assert.Throws<IOException>(() => Enable(repositories[0], replacement, secrets[0].GenerationId));
            using (var old = Repository.OpenWithOneDriveRecovery(repositories[0].DirectoryPath, secrets[0])) { }
            repositories[0].OneDriveRecoveryObserved = null;
            Enable(repositories[0], replacement, secrets[0].GenerationId);
            Assert.Throws<InvalidDataException>(() => Repository.OpenWithOneDriveRecovery(repositories[0].DirectoryPath, secrets[0]));
            repositories[0].RemoveOneDriveRecovery(replacement.GenerationId);
            Assert.Null(Repository.InspectOneDriveRecovery(repositories[0].DirectoryPath));
            for (int i = 1; i < repositories.Length; i++)
            { using var recovered = Repository.OpenWithOneDriveRecovery(repositories[i].DirectoryPath, secrets[i]); }
            using var password = Repository.OpenWithPassword(repositories[0].DirectoryPath, EncryptionTests.Password);
        }
        finally { foreach (var secret in secrets) secret.Dispose(); foreach (var repository in repositories) repository.Dispose(); }
    }

    [Fact]
    public void DecryptionRetiresLocalHalfAndReencryptionNeverUploadsOrReenablesRecovery()
    {
        using var temp = new TemporaryDirectory(); Guid id;
        using (var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password))
        {
            id = repository.Identity.RepositoryId; EncryptionTests.Add(repository);
            using var secret = OneDriveRecoverySecret.Create(id); Enable(repository, secret);
        }
        Repository.ConvertEncryption(temp.Child(), false, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        Assert.Null(Repository.InspectOneDriveRecovery(temp.Child()));
        using (var plain = Repository.Open(temp.Child())) Assert.Equal(id, plain.Identity.RepositoryId);
        Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        Assert.Null(Repository.InspectOneDriveRecovery(temp.Child()));
        using var portable = Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password);
        using var reader = portable.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }
}

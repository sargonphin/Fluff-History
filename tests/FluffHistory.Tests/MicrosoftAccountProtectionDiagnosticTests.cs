using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

/// <summary>Research only: no production slot, credential collection or account-recovery claim.</summary>
public sealed class MicrosoftAccountProtectionDiagnosticTests(ITestOutputHelper output)
{
    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void NativeClientProviderCandidateWithoutAccountCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(0, RoundTrip("LOCAL=user", out var localStep)); Assert.Equal("complete", localStep);
        var status = RoundTrip("WEBCREDENTIALS=FluffHistory-Ownership-synthetic", out var step);
        output.WriteLine($"WEBCREDENTIALS synthetic diagnostic: stage={step}, status=0x{unchecked((uint)status):X8}. Descriptor parsing/same-session results do not prove MSA portability. This probe does not inspect account provisioning; use the live account diagnostic for that evidence.");
    }

    // The official descriptor parser selects the provider. Only synthetic nonrepository data is passed.
    private static int RoundTrip(string descriptor, out string step)
    {
        step = "descriptor"; nint handle = 0, blob = 0, plain = 0, recoveredDescriptor = 0; uint blobSize = 0, plainSize = 0;
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var status = NCryptCreateProtectionDescriptor(descriptor, 0, out handle); if (status != 0) return status;
            step = "protect-silent";
            status = NCryptProtectSecret(handle, 0x40, secret, (uint)secret.Length, 0, 0, out blob, out blobSize); if (status != 0) return status;
            Assert.InRange(blobSize, 1U, 65536U); var encrypted = new byte[blobSize]; Marshal.Copy(blob, encrypted, 0, encrypted.Length);
            step = "unprotect-silent";
            status = NCryptUnprotectSecret(out recoveredDescriptor, 0x40, encrypted, blobSize, 0, 0, out plain, out plainSize); if (status != 0) return status;
            Assert.Equal(32U, plainSize); var actual = new byte[plainSize]; Marshal.Copy(plain, actual, 0, actual.Length);
            try { Assert.Equal(secret, actual); } finally { CryptographicOperations.ZeroMemory(actual); }
            step = "complete"; return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (plain != 0) { if (plainSize <= 65536) Marshal.Copy(new byte[plainSize], 0, plain, (int)plainSize); _ = LocalFree(plain); }
            if (blob != 0) _ = LocalFree(blob);
            if (recoveredDescriptor != 0) _ = NCryptCloseProtectionDescriptor(recoveredDescriptor);
            if (handle != 0) _ = NCryptCloseProtectionDescriptor(handle);
        }
    }
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptCreateProtectionDescriptor(string descriptor, uint flags, out nint handle);
    [DllImport("ncrypt.dll")] private static extern int NCryptCloseProtectionDescriptor(nint handle);
    [DllImport("ncrypt.dll")] private static extern int NCryptProtectSecret(nint handle, uint flags, byte[] data, uint size, nint memory, nint window, out nint blob, out uint blobSize);
    [DllImport("ncrypt.dll")] private static extern int NCryptUnprotectSecret(out nint handle, uint flags, byte[] blob, uint blobSize, nint memory, nint window, out nint plain, out uint plainSize);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluffHistory.Application;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Windows;

public interface IUpdateSignerPolicy { bool Accept(ReadOnlySpan<byte> certificateDer); }
public sealed class ApprovedUpdateSignerPolicy : IUpdateSignerPolicy
{
    private readonly string[] _hashes;
    public ApprovedUpdateSignerPolicy(IEnumerable<string> certificateSha256)
    {
        _hashes = certificateSha256.ToArray();
        if (_hashes.Any(h => h.Length != 64 || !h.All(Uri.IsHexDigit))) throw new ArgumentException("Invalid approved signer identity.");
    }
    public bool Accept(ReadOnlySpan<byte> certificateDer) => _hashes.Contains(Convert.ToHexString(SHA256.HashData(certificateDer)), StringComparer.OrdinalIgnoreCase);
    public bool IsConfigured => _hashes.Length != 0;
    public static ApprovedUpdateSignerPolicy FromAssembly(Assembly assembly) => new(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(a => a.Key == "UpdateSignerCertificateSha256" && !string.IsNullOrEmpty(a.Value)).Select(a => a.Value!));
}

public interface IUpdateFileVerifier
{
    /// <summary>Caller retains the exact no-write/no-delete file handle through execution.</summary>
    void Verify(string path, FileStream heldFile, UpdateRelease release);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateFileVerifier(IUpdateSignerPolicy signer) : IUpdateFileVerifier
{
    public void Verify(string path, FileStream heldFile, UpdateRelease release)
    {
        release.Validate();
        if (!release.CanDownload || heldFile.Length != release.Size) throw new UpdateException(UpdateFailure.Integrity);
        heldFile.Position = 0;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(heldFile)), release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateFailure.Integrity);
        VerifySignature(path, heldFile.SafeFileHandle, signer);
        var info = FileVersionInfo.GetVersionInfo(path);
        // The signed PE text resource binds the public release independently from FILEVERSION.
        if (!InstallerProductIdentity.Matches(info.ProductName, info.CompanyName, info.FileDescription, info.ProductVersion, release.Version))
            throw new UpdateException(UpdateFailure.WrongProduct);
    }
    internal static void VerifySignature(string path, SafeFileHandle heldFile, IUpdateSignerPolicy signer)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path, Handle = heldFile.DangerousGetHandle() };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = pointer,
            StateAction = 1, ProviderFlags = 0x80 | 0x2000 }; // chain excluding root revocation; disable legacy MD2/MD4
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            var trust = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (trust != 0) { System.Diagnostics.Trace.TraceWarning("Update Authenticode rejected ({0:X8}).", trust); throw new UpdateException(UpdateFailure.Signature); }
            var provider = WTHelperProvDataFromStateData(data.State);
            var signerPointer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signerPointer == 0) throw new UpdateException(UpdateFailure.Signature);
            var selected = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
            if (selected.Error != 0 || selected.CertificateCount == 0 || selected.Certificates == 0) throw new UpdateException(UpdateFailure.Signature);
            var certificate = Marshal.PtrToStructure<ProviderCertificate>(selected.Certificates);
            if (certificate.Certificate == 0) throw new UpdateException(UpdateFailure.Signature);
            var context = Marshal.PtrToStructure<CertificateContext>(certificate.Certificate);
            if (context.Length is < 1 or > 65536 || context.Encoded == 0) throw new UpdateException(UpdateFailure.Signature);
            var der = new byte[context.Length]; Marshal.Copy(context.Encoded, der, 0, der.Length);
            using var parsed = X509CertificateLoader.LoadCertificate(der);
            if (!signer.Accept(parsed.RawData)) throw new UpdateException(UpdateFailure.SignerNotApproved);
        }
        finally
        {
            data.StateAction = 2; _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer);
            GC.KeepAlive(heldFile);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { public uint Size; public nint Policy, Sip; public uint Ui, Revocation, Choice; public nint File; public uint StateAction;
        public nint State, Url; public uint ProviderFlags, UiContext; public nint SignatureSettings; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderSigner
    { public uint Size; public System.Runtime.InteropServices.ComTypes.FILETIME VerifyTime; public uint CertificateCount; public nint Certificates; public uint SignerType; public nint Signer; public uint Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public nint Certificate; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { public uint Encoding; public nint Encoded; public int Length; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperGetProvSignerFromChain(nint data, uint index, [MarshalAs(UnmanagedType.Bool)] bool countersigner, uint counterIndex);
}

/// <summary>Pins each existing directory against rename/deletion while paths are validated/opened.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUpdatePathLease : IDisposable
{
    private readonly List<SafeFileHandle> _parents = [];
    internal WindowsUpdatePathLease(string directory)
    {
        try
        {
            var stack = new Stack<string>();
            for (string? path = Path.GetFullPath(directory); path is not null; path = Path.GetDirectoryName(path)) stack.Push(path);
            while (stack.TryPop(out var path))
            {
                var handle = CreateFileW(path, 0x80, 3, 0, 3, 0x02200000, 0); // READ_ATTRIBUTES, READ|WRITE sharing, OPEN_REPARSE_POINT|BACKUP_SEMANTICS
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Update directory cannot be retained."); }
                _parents.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
                    throw new IOException("Update directory is not ordinary.");
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var parent in _parents.AsEnumerable().Reverse()) parent.Dispose(); _parents.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Serial, HighSize, LowSize, Links, HighIndex, LowIndex; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FluffHistory.Storage;

internal static class WindowsKeyProtector
{
    internal static byte[] Protect(byte[] bytes, byte[] entropy) => Transform(bytes, entropy, true);
    internal static byte[] Unprotect(byte[] bytes, byte[] entropy) => Transform(bytes, entropy, false);
    private static byte[] Transform(byte[] bytes, byte[] entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows user DPAPI is unavailable; use password recovery.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var extra = new Blob { Length = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.Copy(entropy, 0, extra.Data, entropy.Length);
            var ok = protect ? CryptProtectData(ref input, null, ref extra, 0, 0, 1, out output) :
                CryptUnprotectData(ref input, 0, ref extra, 0, 0, 1, out output);
            if (!ok) throw new IOException("Windows user protector failed. Independent password recovery remains available.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Wipe(input); Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(extra.Data);
            if (output.Data != 0) { Wipe(output); _ = LocalFree(output.Data); }
        }
    }
    private static void Wipe(Blob blob) { if (blob.Data != 0 && blob.Length > 0) Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length); }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Length; internal nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob result);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob data, nint description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob result);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

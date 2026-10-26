using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FluffHistory.Tests.Diagnostics
{
    // Deliberately .NET Framework-compatible for Windows PowerShell's WinRT PasswordVault projection.
    // Test-only native API adapter. Never accepts a repository key or an account password.
    public static class MicrosoftAccountProbeNative
    {
        public sealed class IdentityMetadata
        {
            public int Status;
            public bool InternetIdentity;
            public string Provider;
            public bool HasPrincipalName;
        }
        public sealed class ProtectedSample
        {
            public string Descriptor;
            public uint Flags;
            public int DescriptorStatus;
            public int ProtectStatus;
            public string ExpectedHash;
            public byte[] Blob;
        }
        public sealed class RecoveryResult
        {
            public int Status;
            public bool Matches;
        }
        public static IdentityMetadata InspectIdentity()
        {
            IntPtr pointer = IntPtr.Zero;
            var result = new IdentityMetadata();
            try
            {
                result.Status = NetUserGetInfo(null, Environment.UserName, 24, out pointer);
                if (result.Status != 0) return result;
                var info = (UserInfo24)Marshal.PtrToStructure(pointer, typeof(UserInfo24));
                result.InternetIdentity = info.InternetIdentity;
                if (info.InternetIdentity)
                {
                    result.Provider = Marshal.PtrToStringUni(info.Provider);
                    result.HasPrincipalName = info.Principal != IntPtr.Zero;
                }
                // Do not retrieve/log the principal name, SID or profile key material.
                return result;
            }
            finally { if (pointer != IntPtr.Zero) NetApiBufferFree(pointer); }
        }
        public static ProtectedSample ProtectSynthetic(string descriptor, uint flags)
        {
            if (descriptor == null || descriptor.Length > 512) throw new ArgumentException("Bounded descriptor required.");
            IntPtr handle = IntPtr.Zero, blob = IntPtr.Zero;
            uint size = 0;
            var secret = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
            var result = new ProtectedSample { Descriptor = descriptor, Flags = flags };
            try
            {
                using (var hash = SHA256.Create()) result.ExpectedHash = BitConverter.ToString(hash.ComputeHash(secret)).Replace("-", "");
                result.DescriptorStatus = NCryptCreateProtectionDescriptor(descriptor, 0, out handle);
                if (result.DescriptorStatus != 0) return result;
                result.ProtectStatus = NCryptProtectSecret(handle, flags, secret, 32, IntPtr.Zero, IntPtr.Zero, out blob, out size);
                if (result.ProtectStatus != 0) return result;
                if (size == 0 || size > 65536) throw new InvalidOperationException("Unexpected synthetic protected-blob size.");
                result.Blob = new byte[size]; Marshal.Copy(blob, result.Blob, 0, (int)size);
                return result;
            }
            finally
            {
                Array.Clear(secret, 0, secret.Length);
                if (blob != IntPtr.Zero) LocalFree(blob);
                if (handle != IntPtr.Zero) NCryptCloseProtectionDescriptor(handle);
            }
        }
        public static RecoveryResult RecoverSynthetic(byte[] blob, string expectedHash, uint flags)
        {
            if (blob == null || blob.Length == 0 || blob.Length > 65536 || expectedHash == null || expectedHash.Length != 64)
                throw new ArgumentException("Bounded synthetic sample required.");
            IntPtr handle = IntPtr.Zero, plain = IntPtr.Zero; uint size = 0;
            var result = new RecoveryResult(); byte[] recovered = null;
            try
            {
                result.Status = NCryptUnprotectSecret(out handle, flags, blob, (uint)blob.Length, IntPtr.Zero, IntPtr.Zero, out plain, out size);
                if (result.Status != 0) return result;
                if (size != 32) throw new InvalidOperationException("Unexpected recovered synthetic size.");
                recovered = new byte[32]; Marshal.Copy(plain, recovered, 0, 32);
                using (var hash = SHA256.Create()) result.Matches =
                    BitConverter.ToString(hash.ComputeHash(recovered)).Replace("-", "") == expectedHash;
                return result;
            }
            finally
            {
                if (recovered != null) Array.Clear(recovered, 0, recovered.Length);
                if (plain != IntPtr.Zero)
                {
                    if (size <= 65536) Marshal.Copy(new byte[size], 0, plain, (int)size);
                    LocalFree(plain);
                }
                if (handle != IntPtr.Zero) NCryptCloseProtectionDescriptor(handle);
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct UserInfo24
        {
            [MarshalAs(UnmanagedType.Bool)] public bool InternetIdentity;
            public uint Flags;
            public IntPtr Provider, Principal, Sid;
        }
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetUserGetInfo(string server, string user, uint level, out IntPtr buffer);
        [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptCreateProtectionDescriptor(string descriptor, uint flags, out IntPtr handle);
        [DllImport("ncrypt.dll")] private static extern int NCryptCloseProtectionDescriptor(IntPtr handle);
        [DllImport("ncrypt.dll")] private static extern int NCryptProtectSecret(IntPtr handle, uint flags, byte[] data, uint size, IntPtr memory, IntPtr window, out IntPtr blob, out uint blobSize);
        [DllImport("ncrypt.dll")] private static extern int NCryptUnprotectSecret(out IntPtr handle, uint flags, byte[] blob, uint blobSize, IntPtr memory, IntPtr window, out IntPtr plain, out uint plainSize);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}

using System.Runtime.InteropServices;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class SmbGeometryMeasurementTests(ITestOutputHelper output)
{
    [SmbFact("FLUFF_HISTORY_SMB_NESTED_CAPACITY_ROOT")]
    public void CompareShareRootAndActualRepositoryDirectoryAcrossServerMount()
    {
        var nested = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_NESTED_CAPACITY_ROOT")!;
        using var f = new SmbTestEnvironment(nested);
        var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var actual = repo.ObserveCapacity(); Assert.InRange(actual.Capacity, 32 * 1024 * 1024, 512 * 1024 * 1024);
        var direct = Query(repo.DirectoryPath); var share = Query(Path.GetPathRoot(repo.DirectoryPath)!);
        var dedicated = Query(Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_CAPACITY_ROOT")!);
        new SmbNetworkMeasurementTests(output).Save("geometry-nested-mount", new { Actual = actual, RepositoryDirectory = direct,
            ShareRoot = share, DedicatedShare = dedicated });
        Assert.True(dedicated.GeometrySuccess); Assert.Equal(1024L, dedicated.AllocationUnit);
        // Phase A records native support/error rather than guessing a root's geometry.
        if (direct.GeometrySuccess) Assert.Equal(dedicated.AllocationUnit, direct.AllocationUnit);
    }
    private sealed record Observation(bool GeometrySuccess, long? AllocationUnit, int GeometryError,
        bool VolumeSuccess, uint VolumeSerial, int VolumeError);
    private static Observation Query(string path)
    {
        if (!Path.EndsInDirectorySeparator(path)) path += Path.DirectorySeparatorChar;
        var geometry = GetDiskFreeSpace(path, out var sectors, out var bytes, out _, out _);
        var error = geometry ? 0 : Marshal.GetLastPInvokeError();
        var volume = GetVolumeInformation(path, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0);
        return new(geometry, geometry ? checked((long)sectors * bytes) : null, error,
            volume, serial, volume ? 0 : Marshal.GetLastPInvokeError());
    }
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpace(string path, out uint sectors, out uint bytes, out uint free, out uint total);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformation(string path, IntPtr volumeName, uint nameSize,
        out uint serial, out uint maximumComponentLength, out uint flags, IntPtr filesystemName, uint filesystemNameSize);
}

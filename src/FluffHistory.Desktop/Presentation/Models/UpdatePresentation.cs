using FluffHistory.Application;

namespace FluffHistory.Desktop.Presentation.Models;

internal static class UpdatePresentation
{
    internal static string? CurrentVersion(IDesktopContext owner) =>
        owner.Updates?.CurrentReleaseVersion ?? owner.InstalledProductVersion;

    internal static bool HasNewerRelease(IDesktopContext owner)
    {
        if (owner.Updates?.Available is not { } release) return false;
        if (ReleaseVersion.TryParse(owner.Updates.CurrentReleaseVersion, out var current))
            return release.Version > current;
        // Development packages have a four-part PE/build version, not a public
        // release identity. Compare the release components for display only;
        // this must never grant permission to install a development update.
        if (!ProductBuildVersion.TryParse(owner.InstalledProductVersion, out var build)) return false;
        var candidate = release.Version;
        return new Version(candidate.Major, candidate.Minor, candidate.Patch) >
            new Version(build.Major, build.Minor, build.Build);
    }
}

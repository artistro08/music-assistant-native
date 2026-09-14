using System.Runtime.InteropServices;

namespace MusicAssistant;

/// <summary>
/// Tells the two ways this app ships apart.
///
/// Packaged (MSIX) builds have package identity: Windows already knows the
/// app's name, icon and AppUserModelID, so the unpackaged-only registration
/// steps (explicit app id, registry entry) must be skipped.
/// </summary>
public static class Packaging
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}

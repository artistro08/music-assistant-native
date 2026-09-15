using Microsoft.Win32;

namespace MusicAssistant.Sendspin;

/// <summary>
/// The PC's make and model as the firmware reports them, for the device details Music Assistant shows for this speaker.
/// </summary>
/// <remarks>
/// Read from the SMBIOS values Windows copies into the registry at boot, the same source ha-windows uses, so both
/// report the same device. Falls back to generic names when the firmware leaves them empty.
/// </remarks>
public static class HardwareIdentity
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    /// <summary>The PC's model, such as "Surface Laptop Studio", or "Windows PC" when the firmware doesn't say.</summary>
    public static string Model { get; } = Read("SystemProductName") ?? "Windows PC";

    /// <summary>The PC's maker, such as "Microsoft Corporation", or "Microsoft" when the firmware doesn't say.</summary>
    public static string Manufacturer { get; } = Read("SystemManufacturer") ?? "Microsoft";

    /// <summary>Reads one SMBIOS value, ignoring the placeholder text some firmware ships with.</summary>
    /// <param name="name">The registry value name.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when it is missing, empty or a placeholder.</returns>
    private static string? Read(string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BiosKey);
            string? value = (key?.GetValue(name) as string)?.Trim();
            bool placeholder = (value is null) || (value.Length == 0) || value.Contains("To be filled", StringComparison.OrdinalIgnoreCase) || value.Equals("System Product Name", StringComparison.OrdinalIgnoreCase);
            return placeholder ? null : value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

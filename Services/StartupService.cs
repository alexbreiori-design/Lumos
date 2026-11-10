using Microsoft.Win32;

namespace Lumos.Services;

internal static class StartupService
{
    private const string ValueName = "Lumos";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key == null)
            return;

        key.DeleteValue("Brilho", throwOnMissingValue: false);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return;
        key.SetValue(ValueName, "\"" + exe + "\" --background");
    }
}

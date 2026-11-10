using System.Text;
using Microsoft.Win32;

namespace Brilho.Services;

internal static class EdidReader
{
    public static bool TryRead(string devicePath, out string serial, out string name)
    {
        serial = "";
        name = "";
        try
        {
            if (!TryRegistryPath(devicePath, out var registryPath))
                return false;

            if (Registry.GetValue(registryPath, "EDID", null) is not byte[] edid || edid.Length < 128)
                return false;

            serial = Descriptor(edid, 0xFF);
            name = Descriptor(edid, 0xFC);
            return serial.Length > 0 || name.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Info("Falha ao ler EDID: " + ex.Message);
            return false;
        }
    }

    public static bool TryRegistryPath(string devicePath, out string registryPath)
    {
        registryPath = "";
        const string marker = "DISPLAY#";
        var start = devicePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        var rest = devicePath[(start + marker.Length)..];
        var split = rest.IndexOf('#');
        if (split <= 0)
            return false;

        var end = rest.IndexOf('#', split + 1);
        var hardware = end < 0 ? rest : rest[..end];
        var parts = hardware.Split('#');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return false;

        registryPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[0] + "\\" + parts[1] + @"\Device Parameters";
        return true;
    }

    private static string Descriptor(byte[] edid, byte tag)
    {
        for (var offset = 54; offset <= 108 && offset + 18 <= edid.Length; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 || edid[offset + 3] != tag)
                continue;
            var text = Encoding.ASCII.GetString(edid, offset + 5, 13);
            return text.Trim('\0', ' ', '\r', '\n', '\t');
        }

        return "";
    }
}

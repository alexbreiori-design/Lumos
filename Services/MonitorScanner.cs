using System.Runtime.InteropServices;
using Brilho.Models;
using Brilho.Native;

namespace Brilho.Services;

internal static class MonitorScanner
{
    public static List<MonitorState> Scan()
    {
        var paths = ReadPaths();
        var screens = ReadScreens();
        var found = new List<MonitorState>();

        foreach (var screen in screens)
        {
            paths.TryGetValue(screen.Device, out var path);
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(screen.Handle, out var count) || count == 0)
            {
                found.Add(Describe(screen, path, handle: 0, hardware: false, current: 0, max: 0, index: 0));
                Log.Info("Sem monitor físico em " + screen.Device);
                continue;
            }

            var physical = new PhysicalMonitor[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(screen.Handle, count, physical))
            {
                Log.Info("Falha ao abrir " + screen.Device + " erro " + Marshal.GetLastWin32Error());
                found.Add(Describe(screen, path, handle: 0, hardware: false, current: 0, max: 0, index: 0));
                continue;
            }

            for (var i = 0; i < physical.Length; i++)
            {
                var handle = physical[i].Handle;
                var hardware = NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    handle,
                    NativeMethods.BrightnessVcp,
                    out _,
                    out var current,
                    out var max);
                if (!hardware || max == 0)
                {
                    Log.Info("DDC indisponível em " + screen.Device + " erro " + Marshal.GetLastWin32Error());
                    NativeMethods.DestroyPhysical(handle);
                    handle = 0;
                    current = 0;
                    max = 0;
                }
                else
                {
                    Log.Info("DDC " + screen.Device + " brilho " + current + "/" + max);
                }

                found.Add(Describe(screen, path, handle, hardware, current, max, i));
            }
        }

        Disambiguate(found);
        return found
            .OrderBy(monitor => monitor.Y)
            .ThenBy(monitor => monitor.X)
            .ToList();
    }

    public static void Release(IEnumerable<MonitorState> monitors)
    {
        foreach (var monitor in monitors)
        {
            NativeMethods.DestroyPhysical(monitor.Handle);
            monitor.Handle = 0;
        }
    }

    private static MonitorState Describe(Screen screen, PathInfo? path, nint handle, bool hardware, uint current, uint max, int index)
    {
        var friendly = path?.Friendly?.Trim() ?? "";
        var serial = "";
        var edidName = "";
        if (path != null)
            EdidReader.TryRead(path.DevicePath, out serial, out edidName);

        var name = CleanName(string.IsNullOrWhiteSpace(edidName) ? friendly : edidName);
        var id = !string.IsNullOrWhiteSpace(serial)
            ? "serial:" + serial
            : "device:" + (path?.DevicePath ?? screen.Device);
        if (index > 0)
            id += "#" + (index + 1);

        return new MonitorState
        {
            Id = id,
            Name = name,
            Serial = serial,
            Position = PositionOf(screen, screen.PrimaryOrigin),
            Connector = ConnectorName(path?.Technology ?? uint.MaxValue),
            X = screen.X,
            Y = screen.Y,
            Width = screen.Width,
            Height = screen.Height,
            Hardware = hardware,
            Handle = handle,
            MaxBrightness = max,
            DetectedPercent = hardware ? BrightnessMath.ToPercent(current, max) : 100
        };
    }

    private static void Disambiguate(List<MonitorState> monitors)
    {
        foreach (var group in monitors.GroupBy(monitor => monitor.Id).Where(group => group.Count() > 1))
        {
            var number = 1;
            foreach (var monitor in group.OrderBy(item => item.X).ThenBy(item => item.Y))
                monitor.Id = monitor.Id + "#" + number++;
        }

        foreach (var group in monitors.GroupBy(monitor => monitor.Position).Where(group => group.Count() > 1))
        {
            var number = 1;
            foreach (var monitor in group.OrderBy(item => item.X).ThenBy(item => item.Y))
                monitor.Position = monitor.Position + " " + number++;
        }
    }

    private static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return "Monitor";
        if (name.Contains("SMILE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Generic", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PnP", StringComparison.OrdinalIgnoreCase))
            return "Monitor";
        return name;
    }

    private static string PositionOf(Screen screen, ScreenOrigin primary)
    {
        var centerX = screen.X + screen.Width / 2;
        var centerY = screen.Y + screen.Height / 2;
        var dx = centerX - primary.CenterX;
        var dy = centerY - primary.CenterY;
        if (Math.Abs(dx) < Math.Max(80, primary.Width / 4) && Math.Abs(dy) < Math.Max(80, primary.Height / 4))
            return "Principal";
        if (Math.Abs(dy) >= Math.Abs(dx))
            return dy < 0 ? "Acima" : "Abaixo";
        return dx < 0 ? "Esquerda" : "Direita";
    }

    private static string ConnectorName(uint technology) => technology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        10 => "DisplayPort",
        11 => "DisplayPort",
        15 => "Sem fio",
        16 => "Indireta",
        17 => "Virtual",
        0x80000000 => "Interno",
        _ => "Vídeo"
    };

    private static Dictionary<string, PathInfo> ReadPaths()
    {
        var map = new Dictionary<string, PathInfo>(StringComparer.OrdinalIgnoreCase);
        if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0
            || pathCount == 0)
            return map;

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        if (NativeMethods.QueryDisplayConfig(NativeMethods.QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0) != 0)
            return map;

        for (var i = 0; i < pathCount; i++)
        {
            var source = new DisplayConfigSourceDeviceName
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = NativeMethods.DisplayConfigGetSourceName,
                    Size = Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                    AdapterId = paths[i].SourceInfo.AdapterId,
                    Id = paths[i].SourceInfo.Id
                }
            };
            var target = new DisplayConfigTargetDeviceName
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = NativeMethods.DisplayConfigGetTargetName,
                    Size = Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                    AdapterId = paths[i].TargetInfo.AdapterId,
                    Id = paths[i].TargetInfo.Id
                }
            };

            var sourceOk = NativeMethods.DisplayConfigGetDeviceInfo(ref source) == 0;
            var targetOk = NativeMethods.DisplayConfigGetDeviceInfo(ref target) == 0;
            if (!sourceOk || string.IsNullOrWhiteSpace(source.ViewGdiDeviceName))
                continue;

            map[source.ViewGdiDeviceName] = new PathInfo
            {
                Friendly = targetOk ? target.MonitorFriendlyDeviceName : "",
                DevicePath = targetOk ? target.MonitorDevicePath : "",
                Technology = paths[i].TargetInfo.OutputTechnology
            };
        }

        return map;
    }

    private static List<Screen> ReadScreens()
    {
        var list = new List<Screen>();
        NativeMethods.MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                return true;

            var width = info.Monitor.Right - info.Monitor.Left;
            var height = info.Monitor.Bottom - info.Monitor.Top;
            list.Add(new Screen
            {
                Handle = monitor,
                Device = info.Device ?? "",
                X = info.Monitor.Left,
                Y = info.Monitor.Top,
                Width = width,
                Height = height,
                Primary = (info.Flags & NativeMethods.MonitorinfoPrimary) != 0
            });
            return true;
        };

        NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);

        var primary = list.FirstOrDefault(screen => screen.Primary) ?? list.FirstOrDefault();
        var origin = primary == null
            ? new ScreenOrigin(0, 0, 1920, 1080)
            : new ScreenOrigin(primary.X + primary.Width / 2, primary.Y + primary.Height / 2, primary.Width, primary.Height);
        foreach (var screen in list)
            screen.PrimaryOrigin = origin;
        return list;
    }

    private sealed class PathInfo
    {
        public string Friendly { get; init; } = "";
        public string DevicePath { get; init; } = "";
        public uint Technology { get; init; }
    }

    private sealed class Screen
    {
        public nint Handle { get; init; }
        public string Device { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public bool Primary { get; init; }
        public ScreenOrigin PrimaryOrigin { get; set; }
    }

    private readonly record struct ScreenOrigin(int CenterX, int CenterY, int Width, int Height);
}

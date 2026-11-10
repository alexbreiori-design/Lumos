using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using Drawing = System.Drawing;

namespace Lumos.Services;

internal static class Theme
{
    public const string DefaultAccent = "#8A37F1";

    private const string TrayDarkUri = "pack://application:,,,/Assets/lumos-tray_icon-dark-theme.png";
    private const string TrayLightUri = "pack://application:,,,/Assets/lumos-tray_icon-light-theme.png";

    public static readonly string[] Presets =
    [
        "#8A37F1",
        "#F5005D",
        "#3EE0B5",
        "#00F5FF",
        "#39FF14",
        "#FF00E5",
        "#FFE600",
        "#F4F7FB"
    ];

    private static BitmapSource? _trayDark;
    private static BitmapSource? _trayLight;

    public static string Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return DefaultAccent;
        var text = hex.Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;
        return TryParse(text, out var color) ? ToHex(color) : DefaultAccent;
    }

    public static bool TryParse(string hex, out Color color)
    {
        color = default;
        var text = hex.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length != 6)
            return false;
        try
        {
            var value = Convert.ToUInt32(text, 16);
            color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static Color Parse(string hex)
    {
        TryParse(Normalize(hex), out var color);
        return color;
    }

    public static string ToHex(Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color ContrastingInk(Color background)
    {
        var luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0;
        return luminance > 0.62
            ? Color.FromRgb(0x12, 0x15, 0x1C)
            : Color.FromRgb(0xF4, 0xF7, 0xFB);
    }

    public static bool IsOsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme") ?? key?.GetValue("AppsUseLightTheme");
            return value is int number && number == 1;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(Color accent)
    {
        var resources = System.Windows.Application.Current.Resources;
        resources["AccentBrush"] = Brush(accent);
        resources["AccentSoftBrush"] = Brush(Mix(Color.FromRgb(0x12, 0x15, 0x1C), accent, 0.22));
        resources["AccentTextBrush"] = Brush(Mix(accent, Color.FromRgb(255, 255, 255), 0.42));
        resources["AccentOnBrush"] = Brush(ContrastingInk(accent));
        resources["AccentHoverBrush"] = Brush(Mix(accent, Color.FromRgb(255, 255, 255), 0.14));
        resources["AppMark"] = CreateMark(accent);
    }

    public static BitmapSource CreateBitmap(Color accent, int size)
        => RenderTintedTray(accent, size);

    public static BitmapFrame CreateWindowIcon(Color accent)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateBitmap(accent, 64)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    public static Drawing.Icon CreateTrayIcon(Color accent)
    {
        var bytes = CreateIco(accent);
        return new Drawing.Icon(new MemoryStream(bytes));
    }

    public static byte[] CreateIco(Color accent)
    {
        var sizes = new[] { 16, 20, 24, 32, 48, 64, 256 };
        var pngs = sizes.Select(size => EncodePng(CreateBitmap(accent, size))).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)pngs.Length);
        var offset = 6 + 16 * pngs.Length;
        for (var i = 0; i < pngs.Length; i++)
        {
            var side = sizes[i] >= 256 ? 0 : sizes[i];
            writer.Write((byte)side);
            writer.Write((byte)side);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(pngs[i].Length);
            writer.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (var png in pngs)
            writer.Write(png);
        return stream.ToArray();
    }

    private static ImageSource CreateMark(Color accent)
        => CreateBitmap(accent, 64);

    private static BitmapSource RenderTintedTray(Color accent, int size)
    {
        var source = LoadTrayBase();
        var scaled = new TransformedBitmap(source, new ScaleTransform(
            (double)size / source.PixelWidth,
            (double)size / source.PixelHeight));
        scaled.Freeze();
        var prepared = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        prepared.Freeze();

        var width = prepared.PixelWidth;
        var height = prepared.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        prepared.CopyPixels(pixels, stride, 0);

        var tipX = width * 0.78;
        var tipY = height * 0.22;
        var radius = Math.Max(width, height) * 0.42;
        RgbToHsv(accent.R, accent.G, accent.B, out var accentHue, out _, out _);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var a = pixels[i + 3];
                if (a < 8 || IsNearBlack(r, g, b))
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    pixels[i + 3] = 0;
                    continue;
                }

                var dx = x - tipX;
                var dy = y - tipY;
                var dist = Math.Sqrt(dx * dx + dy * dy) / radius;
                if (!ShouldRetintGlow(r, g, b, dist))
                    continue;

                RgbToHsv(r, g, b, out _, out var sat, out var val);
                // Keep the hot white core mostly white; tint the purple halo/sparks.
                var core = val > 0.88 && sat < 0.22 && dist < 0.18;
                var nextSat = core ? sat * 0.35 : Math.Clamp(sat * 1.05, 0, 1);
                var nextVal = core ? Math.Max(val, 0.95) : val;
                HsvToRgb(accentHue, nextSat, nextVal, out var nr, out var ng, out var nb);
                pixels[i] = nb;
                pixels[i + 1] = ng;
                pixels[i + 2] = nr;
            }
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static bool ShouldRetintGlow(byte r, byte g, byte b, double dist)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var sat = max == 0 ? 0 : (max - min) / (double)max;
        var purple = b >= g + 12 && b >= 55 && (b + r) > g * 2;
        var magenta = r > 70 && b > 90 && g < Math.Min(r, b) * 0.85;
        var hotCore = max > 210 && sat < 0.28 && dist < 0.22;
        var spark = sat > 0.18 && max > 90 && dist < 1.05 && (purple || magenta);
        return purple || magenta || hotCore || spark;
    }

    private static bool IsNearBlack(byte r, byte g, byte b)
        => r < 18 && g < 18 && b < 18;

    private static BitmapSource LoadTrayBase()
    {
        var light = IsOsLightTheme();
        if (light)
        {
            _trayLight ??= LoadPackBitmap(TrayLightUri);
            return _trayLight;
        }

        _trayDark ??= LoadPackBitmap(TrayDarkUri);
        return _trayDark;
    }

    private static BitmapSource LoadPackBitmap(string uri)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(uri, UriKind.Absolute);
        bitmap.DecodePixelWidth = 256;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static void ResetTrayCache()
    {
        _trayDark = null;
        _trayLight = null;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte start, byte end) => (byte)Math.Clamp(Math.Round(start + (end - start) * amount), 0, 255);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        var rd = r / 255.0;
        var gd = g / 255.0;
        var bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;
        v = max;
        s = max <= 0 ? 0 : delta / max;
        if (delta <= 0.00001)
        {
            h = 0;
            return;
        }

        if (Math.Abs(max - rd) < 0.00001)
            h = (gd - bd) / delta + (gd < bd ? 6 : 0);
        else if (Math.Abs(max - gd) < 0.00001)
            h = (bd - rd) / delta + 2;
        else
            h = (rd - gd) / delta + 4;
        h *= 60;
    }

    private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        double rd, gd, bd;
        if (h < 60) { rd = c; gd = x; bd = 0; }
        else if (h < 120) { rd = x; gd = c; bd = 0; }
        else if (h < 180) { rd = 0; gd = c; bd = x; }
        else if (h < 240) { rd = 0; gd = x; bd = c; }
        else if (h < 300) { rd = x; gd = 0; bd = c; }
        else { rd = c; gd = 0; bd = x; }

        r = (byte)Math.Clamp(Math.Round((rd + m) * 255), 0, 255);
        g = (byte)Math.Clamp(Math.Round((gd + m) * 255), 0, 255);
        b = (byte)Math.Clamp(Math.Round((bd + m) * 255), 0, 255);
    }
}

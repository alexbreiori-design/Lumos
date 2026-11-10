using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Drawing = System.Drawing;

namespace Brilho.Services;

internal static class Theme
{
    public const string DefaultAccent = "#F5005D";

    public static readonly string[] Presets =
    [
        "#F5005D",
        "#3EE0B5",
        "#00F5FF",
        "#39FF14",
        "#FF00E5",
        "#FFE600",
        "#F4F7FB"
    ];

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

    public static void Apply(Color accent)
    {
        var resources = System.Windows.Application.Current.Resources;
        resources["AccentBrush"] = Brush(accent);
        resources["AccentSoftBrush"] = Brush(Mix(Color.FromRgb(0x12, 0x15, 0x1C), accent, 0.20));
        resources["AccentTextBrush"] = Brush(Mix(accent, Color.FromRgb(255, 255, 255), 0.48));
        resources["AccentOnBrush"] = Brush(ContrastingInk(accent));
        resources["AccentHoverBrush"] = Brush(Mix(accent, Color.FromRgb(255, 255, 255), 0.14));
        resources["AppMark"] = CreateMark(accent);
    }

    public static BitmapSource CreateBitmap(Color accent, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            Draw(context, accent, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

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

    private static DrawingImage CreateMark(Color accent)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            Draw(context, accent, 32);
        var image = new DrawingImage(visual.Drawing);
        image.Freeze();
        return image;
    }

    private static void Draw(DrawingContext context, Color accent, double size)
    {
        var brush = Brush(accent);
        var thickness = Math.Max(1.6, size * 0.11);
        var pen = new System.Windows.Media.Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var center = new System.Windows.Point(size / 2, size / 2);
        context.DrawEllipse(brush, null, center, size * 0.17, size * 0.17);
        var inner = size * 0.30;
        var outer = size * 0.46;
        for (var ray = 0; ray < 8; ray++)
        {
            var angle = ray * Math.PI / 4 - Math.PI / 2;
            context.DrawLine(
                pen,
                new System.Windows.Point(center.X + Math.Cos(angle) * inner, center.Y + Math.Sin(angle) * inner),
                new System.Windows.Point(center.X + Math.Cos(angle) * outer, center.Y + Math.Sin(angle) * outer));
        }
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
}

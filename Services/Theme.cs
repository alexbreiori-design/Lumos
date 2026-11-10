using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Drawing = System.Drawing;

namespace Lumos.Services;

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
        var glow = Brush(accent);
        var wood = Brush(Color.FromRgb(0x6B, 0x45, 0x2A));
        var woodDark = Brush(Color.FromRgb(0x3D, 0x27, 0x16));
        var tip = new System.Windows.Point(size * 0.78, size * 0.22);
        var handle = new System.Windows.Point(size * 0.22, size * 0.80);

        // Soft aura behind the tip
        context.DrawEllipse(Brush(Color.FromArgb(72, accent.R, accent.G, accent.B)), null, tip, size * 0.22, size * 0.22);
        context.DrawEllipse(Brush(Mix(Color.FromRgb(255, 255, 255), accent, 0.35)), null, tip, size * 0.10, size * 0.10);
        context.DrawEllipse(glow, null, tip, size * 0.055, size * 0.055);

        // Wand shaft
        var shaft = new System.Windows.Media.Pen(wood, Math.Max(2.0, size * 0.09))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        context.DrawLine(shaft, handle, tip);

        // Handle band
        var mid = new System.Windows.Point(
            handle.X + (tip.X - handle.X) * 0.18,
            handle.Y + (tip.Y - handle.Y) * 0.18);
        var band = new System.Windows.Media.Pen(woodDark, Math.Max(1.4, size * 0.07))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        context.DrawLine(band, handle, mid);

        // Tiny spark rays
        var rayPen = new System.Windows.Media.Pen(glow, Math.Max(1.1, size * 0.035))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        for (var ray = 0; ray < 4; ray++)
        {
            var angle = ray * Math.PI / 2 + Math.PI / 4;
            context.DrawLine(
                rayPen,
                new System.Windows.Point(tip.X + Math.Cos(angle) * size * 0.08, tip.Y + Math.Sin(angle) * size * 0.08),
                new System.Windows.Point(tip.X + Math.Cos(angle) * size * 0.16, tip.Y + Math.Sin(angle) * size * 0.16));
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

namespace Lumos.Models;

internal sealed class MonitorState
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "Monitor";

    public string Serial { get; set; } = "";

    public string Position { get; set; } = "";

    public string Connector { get; set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Hardware { get; set; }

    public nint Handle { get; set; }

    public uint MaxBrightness { get; set; }

    public int DetectedPercent { get; set; } = 100;

    public int Brightness { get; set; } = 100;

    public string Warning { get; set; } = "";

    public string Title => string.IsNullOrWhiteSpace(Position) ? Name : Name + " · " + Position;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Serial))
                parts.Add(Serial);
            if (!string.IsNullOrWhiteSpace(Connector))
                parts.Add(Connector);
            if (Width > 0 && Height > 0)
                parts.Add(Width + "×" + Height);
            parts.Add(Hardware ? "Físico" : "Camada");
            return string.Join(" · ", parts);
        }
    }
}

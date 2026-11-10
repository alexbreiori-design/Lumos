namespace Brilho.Models;

internal sealed class StoredState
{
    public bool StartWithWindows { get; set; } = true;

    public string Accent { get; set; } = "#F5005D";

    public Dictionary<string, int> Levels { get; set; } = new();

    public Dictionary<string, string> Names { get; set; } = new();

    public List<string> Paused { get; set; } = new();

    public bool DefaultsSeeded { get; set; }

    public List<StoredProfile> Profiles { get; set; } = new();
}

internal sealed class StoredProfile
{
    public string Name { get; set; } = "";

    public string Icon { get; set; } = "";

    public int Order { get; set; } = 100;

    public int? Uniform { get; set; }

    public Dictionary<string, int> Levels { get; set; } = new();
}

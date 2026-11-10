using System.IO;
using System.Text.Json;
using Brilho.Models;

namespace Brilho.Services;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public SettingsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brilho");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
    }

    public StoredState Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new StoredState();
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<StoredState>(json, JsonOptions) ?? new StoredState();
            state.Levels ??= new();
            state.Names ??= new();
            state.Paused ??= new();
            state.Profiles ??= new();
            return state;
        }
        catch (Exception ex)
        {
            Log.Info("Falha ao ler configurações: " + ex.Message);
            return new StoredState();
        }
    }

    public void Save(StoredState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}

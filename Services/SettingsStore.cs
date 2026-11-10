using System.IO;
using System.Text.Json;
using Lumos.Models;

namespace Lumos.Services;

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
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(root, "Lumos");
        var legacy = Path.Combine(root, "Brilho");
        if (!Directory.Exists(directory) && Directory.Exists(legacy))
        {
            try { Directory.Move(legacy, directory); }
            catch { /* keep going with a fresh Lumos folder */ }
        }

        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        var legacySettings = Path.Combine(legacy, "settings.json");
        if (!File.Exists(_path) && File.Exists(legacySettings))
        {
            try { File.Copy(legacySettings, _path); }
            catch { /* ignore */ }
        }
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

using System.IO;

namespace Brilho.Services;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brilho");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "log.txt");
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine;
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 200_000)
                    File.WriteAllText(path, "");
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging never interrupts brightness control.
        }
    }
}

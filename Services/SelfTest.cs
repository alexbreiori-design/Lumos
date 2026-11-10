using System.IO;
using System.Text;
using Lumos.Models;
using Lumos.Native;

namespace Lumos.Services;

internal static class SelfTest
{
    public static int Run()
    {
        var report = new StringBuilder();
        var failed = false;

        void Check(string name, bool ok, string detail = "")
        {
            report.AppendLine((ok ? "OK  " : "FAIL") + " " + name + (detail.Length == 0 ? "" : " " + detail));
            if (!ok)
                failed = true;
        }

        var lowered = BrightnessMath.ApplyDelta(new[] { 80, 50 }, -10);
        Check("delta preserva diferença", lowered.SequenceEqual(new[] { 70, 40 }));
        var clamped = BrightnessMath.ApplyDelta(new[] { 80, 50 }, -100);
        Check("delta respeita o mínimo", clamped.SequenceEqual(new[] { 0, 0 }));
        var raised = BrightnessMath.ApplyDelta(new[] { 100, 50 }, 20);
        Check("delta respeita o máximo", raised.SequenceEqual(new[] { 100, 70 }));
        Check("média", BrightnessMath.Average(new[] { 80, 50 }) == 65);
        Check("camada some em 100", BrightnessMath.AlphaForBrightness(100) == 0);
        Check("camada no zero", BrightnessMath.AlphaForBrightness(0) == BrightnessMath.OverlayAlphaAtZero);
        Check("camada na metade", BrightnessMath.AlphaForBrightness(50) == 90);

        List<MonitorState> monitors;
        try
        {
            monitors = MonitorScanner.Scan();
        }
        catch (Exception ex)
        {
            report.AppendLine("FAIL varredura " + ex);
            Write(report, failed: true);
            return 1;
        }

        Check("encontrou monitores", monitors.Count > 0, "quantidade=" + monitors.Count);
        foreach (var monitor in monitors)
        {
            report.AppendLine("MONITOR " + monitor.Title + " | " + monitor.Subtitle + " | id=" + monitor.Id);
            if (!monitor.Hardware)
                continue;

            var read = NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                monitor.Handle,
                NativeMethods.BrightnessVcp,
                out _,
                out var current,
                out var max);
            var wrote = read && NativeMethods.SetVCPFeature(monitor.Handle, NativeMethods.BrightnessVcp, current);
            uint after = 0;
            var readBack = wrote && NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                monitor.Handle,
                NativeMethods.BrightnessVcp,
                out _,
                out after,
                out _);
            Check("gravação física " + monitor.Position, readBack && after == current, "valor=" + current + "/" + max);
        }

        var overlays = monitors.Where(monitor => !monitor.Hardware).ToList();
        if (overlays.Count > 0)
        {
            foreach (var monitor in overlays)
                monitor.Brightness = 99;
            using var dimmer = new OverlayDimmer();
            try
            {
                dimmer.Sync(overlays);
                foreach (var monitor in overlays)
                {
                    var found = dimmer.TryGet(monitor.Id, out var hwnd);
                    var affinityOk = found && NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == NativeMethods.WdaExcludeFromCapture;
                    var alphaOk = found && NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out _) && alpha == BrightnessMath.AlphaForBrightness(99);
                    var boundsOk = found && NativeMethods.GetWindowRect(hwnd, out var rect)
                        && rect.Left == monitor.X && rect.Top == monitor.Y
                        && rect.Right - rect.Left == monitor.Width
                        && rect.Bottom - rect.Top == monitor.Height;
                    Check("camada excluída da captura " + monitor.Position, affinityOk);
                    Check("opacidade da camada " + monitor.Position, alphaOk);
                    Check("camada cobre só a tela " + monitor.Position, boundsOk);
                }
            }
            catch (Exception ex)
            {
                Check("criar camada", false, ex.Message);
            }
        }

        MonitorScanner.Release(monitors);
        Write(report, failed);
        return failed ? 1 : 0;
    }

    private static void Write(StringBuilder report, bool failed)
    {
        report.Insert(0, (failed ? "RESULTADO falhou" : "RESULTADO ok") + Environment.NewLine);
        var path = Path.Combine(Path.GetTempPath(), "lumos-self-test.txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine(report.ToString());
    }
}

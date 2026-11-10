using System.Runtime.InteropServices;
using Brilho.Models;
using Brilho.Native;

namespace Brilho.Services;

internal sealed class OverlayDimmer : IDisposable
{
    private const string ClassName = "BrilhoOverlayWindow";
    private delegate nint OverlayProc(nint hwnd, uint message, nint wParam, nint lParam);

    private readonly Dictionary<string, nint> _windows = new();
    private readonly OverlayProc _wndProc;
    private bool _classRegistered;

    public OverlayDimmer()
    {
        _wndProc = WndProc;
    }

    public void Sync(IReadOnlyList<MonitorState> monitors)
    {
        var wanted = monitors.Where(monitor => !monitor.Hardware).ToDictionary(monitor => monitor.Id);
        foreach (var id in _windows.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            Destroy(id);

        foreach (var monitor in wanted.Values)
        {
            if (!_windows.TryGetValue(monitor.Id, out var hwnd) || !NativeMethods.IsWindow(hwnd))
            {
                hwnd = CreateWindow(monitor);
                _windows[monitor.Id] = hwnd;
            }

            Place(hwnd, monitor);
            var alpha = BrightnessMath.AlphaForBrightness(monitor.Brightness);
            if (!NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture))
                Log.Info("Falha ao excluir camada da captura: " + Marshal.GetLastWin32Error());

            if (alpha == 0)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
                continue;
            }

            NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTopmost,
                monitor.X,
                monitor.Y,
                monitor.Width,
                monitor.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            NativeMethods.InvalidateRect(hwnd, 0, true);
            NativeMethods.UpdateWindow(hwnd);
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LwaAlpha);
        }
    }

    public bool TryGet(string id, out nint hwnd) => _windows.TryGetValue(id, out hwnd);

    public void Dispose()
    {
        foreach (var id in _windows.Keys.ToList())
            Destroy(id);
    }

    private nint CreateWindow(MonitorState monitor)
    {
        EnsureClass();
        var hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExTopmost | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate,
            ClassName,
            "",
            NativeMethods.WsPopup,
            monitor.X,
            monitor.Y,
            monitor.Width,
            monitor.Height,
            0,
            0,
            NativeMethods.GetModuleHandle(null),
            0);
        if (hwnd == 0)
            throw new InvalidOperationException("Não foi possível criar a camada do monitor " + monitor.Title + ".");
        return hwnd;
    }

    private void Place(nint hwnd, MonitorState monitor)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            monitor.X,
            monitor.Y,
            monitor.Width,
            monitor.Height,
            NativeMethods.SwpNoActivate);
    }

    private void Destroy(string id)
    {
        if (!_windows.Remove(id, out var hwnd))
            return;
        if (NativeMethods.IsWindow(hwnd))
            NativeMethods.DestroyWindow(hwnd);
    }

    private void EnsureClass()
    {
        if (_classRegistered)
            return;

        var windowClass = new WndClass
        {
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = NativeMethods.GetModuleHandle(null),
            Cursor = NativeMethods.LoadCursor(0, 32512),
            Background = NativeMethods.CreateSolidBrush(0),
            ClassName = ClassName
        };
        var atom = NativeMethods.RegisterClass(ref windowClass);
        if (atom == 0 && Marshal.GetLastWin32Error() != 1410)
            throw new InvalidOperationException("Não foi possível registrar a camada de brilho.");
        _classRegistered = true;
    }

    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
        => NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
}

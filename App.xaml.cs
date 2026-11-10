using System.Windows;
using Brilho.Services;

namespace Brilho;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _show;
    private MainWindow? _window;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Info(args.Exception.ToString());
            args.Handled = true;
        };

        if (e.Args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(SelfTest.Run());
            return;
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Brilho.Show");
        _mutex = new Mutex(true, @"Local\Brilho.Single", out var first);
        if (!first)
        {
            _show.Set();
            Shutdown();
            return;
        }

        _ownsMutex = true;
        var background = e.Args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase));
        _window = new MainWindow();
        if (background)
            _window.ShowHidden();
        else
            _window.Reveal();

        var shown = _show;
        var window = _window;
        Task.Run(() =>
        {
            try
            {
                while (shown.WaitOne())
                    window.Dispatcher.BeginInvoke(window.Reveal);
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _window?.ShutdownServices();
        _show?.Dispose();
        if (_ownsMutex)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

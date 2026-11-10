using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using TextBox = System.Windows.Controls.TextBox;
using Lumos.Models;
using Lumos.Native;
using Lumos.Services;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Lumos;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly HardwareBus _bus;
    private readonly OverlayDimmer _overlay = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _rescanTimer;
    private readonly Dictionary<string, int> _levels = new();
    private readonly Dictionary<string, MonitorRow> _rows = new();
    private readonly HashSet<string> _retried = new();
    private readonly Forms.NotifyIcon _tray;
    private readonly Popup _monitorPopup;
    private readonly Popup _profilePopup;
    private readonly StackPanel _monitorMenu = new();
    private readonly StackPanel _profileMenu = new();
    private List<MonitorState> _monitors = new();
    private SettingsWindow? _settingsWindow;
    private string? _selectedId;
    private string? _activeProfile;
    private int[]? _masterSnapshot;
    private int _masterStart;
    private int _masterAnchor = 100;
    private bool _masterDragging;
    private bool _suppress;
    private bool _exit;
    private bool _servicesClosed;
    private bool _armHide;
    private int _suspendHide;
    private int _scanning;
    private bool _scanAgain;
    private nint _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        Settings = _store.Load();
        if (string.Equals(Settings.Accent, "#F5005D", StringComparison.OrdinalIgnoreCase))
            Settings.Accent = Theme.DefaultAccent;
        Settings.Accent = Theme.Normalize(Settings.Accent);
        Theme.Apply(Theme.Parse(Settings.Accent));
        UpdateProfileAccent();
        if (EnsureDefaultProfiles())
            SaveNow();
        _bus = new HardwareBus(id => Dispatcher.BeginInvoke(() => OnHardwareFailed(id)));
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _rescanTimer.Tick += (_, _) =>
        {
            _rescanTimer.Stop();
            _ = ScanAsync();
        };

        _monitorPopup = CreatePopup(MonitorButton, _monitorMenu, above: false);
        _profilePopup = CreatePopup(this, _profileMenu, above: true);
        LoadWindowIcon();
        _tray = CreateTray();
        StartupService.Apply(Settings.StartWithWindows);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        ActiveSlider.PreviewMouseLeftButtonDown += (_, _) => BeginMasterGesture();
        ActiveSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginMasterGesture()));
        ActiveSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndMasterGesture()));
        ActiveSlider.PreviewMouseLeftButtonUp += (_, _) => EndMasterGesture();
        ActiveSlider.ValueChanged += ActiveChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => _ = ScanAsync();
        Closing += OnClosing;
        Activated += (_, _) => _armHide = true;
    }

    internal StoredState Settings { get; }

    internal IReadOnlyList<MonitorState> Monitors => _monitors;

    public void ShowHidden()
    {
        ShowInTaskbar = false;
        Left = -20000;
        ShowActivated = false;
        Show();
        Hide();
    }

    public void Reveal()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        UpdateLayout();
        PinToTrayCorner();
        Activate();
    }

    private void PinToTrayCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = Math.Max(area.Top + 8, area.Bottom - ActualHeight - 12);
    }

    public void ShutdownServices()
    {
        if (_servicesClosed)
            return;
        _servicesClosed = true;
        _exit = true;
        _saveTimer.Stop();
        _rescanTimer.Stop();
        SaveNow();
        _settingsWindow?.Close();
        if (_hwnd != 0)
            NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _overlay.Dispose();
        _bus.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color))
            return;
        Dispatcher.BeginInvoke(() =>
        {
            Theme.ResetTrayCache();
            Theme.Apply(Theme.Parse(Settings.Accent));
            ApplyIcons();
        });
    }

    internal string CustomName(string id)
        => Settings.Names.TryGetValue(id, out var name) ? name : "";

    internal void SetCustomName(string id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            Settings.Names.Remove(id);
        else
            Settings.Names[id] = trimmed.Length > 40 ? trimmed[..40] : trimmed;
        RefreshNames();
        SaveSoon();
    }

    internal bool IsPaused(string id) => Settings.Paused.Contains(id);

    internal void SetPaused(string id, bool paused)
    {
        if (paused)
        {
            if (!Settings.Paused.Contains(id))
                Settings.Paused.Add(id);
        }
        else
            Settings.Paused.Remove(id);

        RebuildRows();
        RebuildMonitorMenu();
        PushAll();
        SyncActiveSlider();
        SaveSoon();
    }

    internal int CurrentBrightness()
    {
        var levels = _monitors.Where(monitor => !IsPaused(monitor.Id)).Select(monitor => monitor.Brightness).ToList();
        if (levels.Count == 0)
            levels = _monitors.Select(monitor => monitor.Brightness).ToList();
        return levels.Count == 0 ? 100 : BrightnessMath.Average(levels);
    }

    internal bool SaveEditedProfile(string? previousName, string name, string icon, bool perMonitor, int uniform, Dictionary<string, int> levels)
    {
        name = CleanProfileName(name);
        if (name.Length == 0)
            return false;

        var profile = string.IsNullOrEmpty(previousName)
            ? null
            : Settings.Profiles.FirstOrDefault(item => item.Name == previousName);
        if (Settings.Profiles.Any(item => !ReferenceEquals(item, profile) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (profile == null)
        {
            profile = new StoredProfile
            {
                Order = Settings.Profiles.Count == 0 ? 1 : Settings.Profiles.Max(item => item.Order) + 1
            };
            Settings.Profiles.Add(profile);
        }

        profile.Name = name;
        profile.Icon = string.IsNullOrWhiteSpace(icon) ? "sun" : icon;
        if (perMonitor)
        {
            profile.Uniform = null;
            profile.Levels = levels;
        }
        else
        {
            profile.Uniform = BrightnessMath.Clamp(uniform);
            profile.Levels = new Dictionary<string, int>();
        }

        if (_activeProfile == previousName)
            _activeProfile = name;
        UpdateProfileAccent();
        RebuildProfileMenu();
        SaveNow();
        return true;
    }

    internal void ApplyStartup(bool enabled)
    {
        Settings.StartWithWindows = enabled;
        StartupService.Apply(enabled);
        SaveSoon();
    }

    internal void SaveProfileNamed(string name)
    {
        name = CleanProfileName(name);
        var levels = _monitors.ToDictionary(monitor => monitor.Id, monitor => monitor.Brightness);
        var existing = Settings.Profiles.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
            Settings.Profiles.Add(new StoredProfile { Name = name, Levels = levels });
        else
        {
            existing.Name = name;
            existing.Uniform = null;
            existing.Levels = levels;
        }

        _activeProfile = name;
        UpdateProfileAccent();
        RebuildProfileMenu();
        SaveNow();
    }

    internal bool RenameProfile(string current, string next)
    {
        next = CleanProfileName(next);
        var profile = Settings.Profiles.FirstOrDefault(item => item.Name == current);
        if (profile == null)
            return false;
        if (Settings.Profiles.Any(item => !ReferenceEquals(item, profile) && item.Name.Equals(next, StringComparison.OrdinalIgnoreCase)))
            return false;
        profile.Name = next;
        if (_activeProfile == current)
            _activeProfile = next;
        UpdateProfileAccent();
        RebuildProfileMenu();
        SaveNow();
        return true;
    }

    internal void ReorderProfiles(IReadOnlyList<string> orderedNames)
    {
        for (var index = 0; index < orderedNames.Count; index++)
        {
            var profile = Settings.Profiles.FirstOrDefault(item => item.Name == orderedNames[index]);
            if (profile != null)
                profile.Order = index + 1;
        }

        RebuildProfileMenu();
        SaveNow();
    }

    internal void DeleteProfileNamed(string name)
    {
        Settings.Profiles.RemoveAll(profile => profile.Name == name);
        if (_activeProfile == name)
            _activeProfile = null;
        UpdateProfileAccent();
        RebuildProfileMenu();
        SaveNow();
    }

    internal void RequestScan()
    {
        _retried.Clear();
        _settingsWindow?.SetStatus("Atualizando monitores…");
        _ = ScanAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd = source.Handle;
        source.AddHook(WindowProc);
        NativeMethods.WTSRegisterSessionNotification(_hwnd, NativeMethods.NotifyForThisSession);
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        var resume = message == NativeMethods.WmPowerBroadcast
            && (wParam == NativeMethods.PbtApmResumeAutomatic || wParam == NativeMethods.PbtApmResumeSuspend);
        var unlock = message == NativeMethods.WmWtsSessionChange && wParam == NativeMethods.WtsSessionUnlock;
        if (message == NativeMethods.WmDisplayChange || resume || unlock)
            ScheduleRescan();
        return 0;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_armHide || _exit || _suspendHide > 0)
            return;
        Dispatcher.BeginInvoke(DismissIfIdle, DispatcherPriority.ApplicationIdle);
    }

    private void DismissIfIdle()
    {
        if (_exit || _suspendHide > 0 || !IsVisible)
            return;
        if (IsActive || IsMouseOver || PopupHasPointer(_monitorPopup) || PopupHasPointer(_profilePopup))
            return;
        Dismiss();
    }

    private static bool PopupHasPointer(Popup popup)
        => popup.IsOpen && popup.Child is UIElement child && child.IsMouseOver;

    private void Dismiss()
    {
        _monitorPopup.IsOpen = false;
        _profilePopup.IsOpen = false;
        ShowInTaskbar = false;
        Hide();
    }

    private void ScheduleRescan()
    {
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    private async Task ScanAsync()
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            _scanAgain = true;
            return;
        }

        try
        {
            do
            {
                _scanAgain = false;
                List<MonitorState> found;
                try
                {
                    found = await Task.Run(MonitorScanner.Scan);
                }
                catch (Exception ex)
                {
                    Log.Info(ex.ToString());
                    ShowProblem("Não foi possível verificar os monitores.");
                    _settingsWindow?.SetStatus("Não foi possível verificar os monitores.");
                    return;
                }

                ApplyScan(found);
            }
            while (_scanAgain);
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private void ApplyScan(List<MonitorState> found)
    {
        foreach (var monitor in found)
        {
            if (_levels.TryGetValue(monitor.Id, out var current))
                monitor.Brightness = current;
            else if (Settings.Levels.TryGetValue(monitor.Id, out var saved))
                monitor.Brightness = BrightnessMath.Clamp(saved);
            else
                monitor.Brightness = monitor.DetectedPercent;
            _levels[monitor.Id] = monitor.Brightness;
        }

        foreach (var id in _levels.Keys.Except(found.Select(monitor => monitor.Id)).ToList())
            _levels.Remove(id);

        if (_selectedId != null && found.All(monitor => monitor.Id != _selectedId))
            _selectedId = null;

        _bus.ReplaceLinks(found);
        _monitors = found;
        RebuildRows();
        RebuildMonitorMenu();
        RebuildProfileMenu();
        PushAll();
        SyncActiveSlider();
        ActiveSlider.IsEnabled = _monitors.Count > 0;
        UpdateWarning();
        _settingsWindow?.Reload();
        _settingsWindow?.SetStatus(found.Count + " monitores atualizados.");
        SaveSoon();
    }

    private void PushAll()
    {
        foreach (var monitor in _monitors)
        {
            if (IsPaused(monitor.Id) || !monitor.Hardware)
                continue;
            _bus.SetPercent(monitor.Id, monitor.Brightness);
        }

        _overlay.Sync(OverlayView());
    }

    private void RebuildRows()
    {
        MonitorList.Children.Clear();
        _rows.Clear();
        foreach (var monitor in _monitors)
        {
            var row = BuildRow(monitor);
            _rows[monitor.Id] = row;
            MonitorList.Children.Add(row.Root);
        }
    }

    private MonitorRow BuildRow(MonitorState monitor)
    {
        var title = new TextBlock
        {
            Text = DisplayName(monitor),
            Foreground = (Brush)FindResource("InkBrush"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var value = new TextBlock
        {
            Text = IsPaused(monitor.Id) ? "Pausado" : monitor.Brightness.ToString(),
            Foreground = (Brush)FindResource("MutedBrush"),
            FontWeight = FontWeights.SemiBold
        };
        DockPanel.SetDock(value, Dock.Right);
        var header = new DockPanel();
        header.Children.Add(value);
        header.Children.Add(title);

        var detail = new TextBlock
        {
            Text = monitor.Subtitle,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        var warning = new TextBlock
        {
            Text = monitor.Warning,
            Foreground = new SolidColorBrush(Color.FromRgb(226, 177, 90)),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(monitor.Warning) ? Visibility.Collapsed : Visibility.Visible
        };
        var slider = new Slider { Value = monitor.Brightness, IsEnabled = !IsPaused(monitor.Id) };
        slider.ValueChanged += (_, _) => OnMonitorChanged(monitor, slider);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(detail);
        stack.Children.Add(slider);
        stack.Children.Add(warning);
        var root = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };
        return new MonitorRow(root, title, value, warning, slider);
    }

    private void OnMonitorChanged(MonitorState monitor, Slider slider)
    {
        if (_suppress || IsPaused(monitor.Id))
            return;
        ApplyMonitorValue(monitor, (int)Math.Round(slider.Value), fromRow: true);
    }

    private void ApplyMonitorValue(MonitorState monitor, int value, bool fromRow)
    {
        if (IsPaused(monitor.Id))
            return;
        monitor.Brightness = BrightnessMath.Clamp(value);
        monitor.Warning = "";
        _levels[monitor.Id] = monitor.Brightness;
        _activeProfile = null;
        UpdateProfileAccent();
        RefreshRow(monitor);
        if (monitor.Hardware)
            _bus.SetPercent(monitor.Id, monitor.Brightness);
        else
            _overlay.Sync(OverlayView());

        if (!_masterDragging)
        {
            if (_selectedId == null)
                SyncMasterThumb();
            else if (_selectedId == monitor.Id && fromRow)
                WithoutEvents(() => ActiveSlider.Value = monitor.Brightness);
            UpdateValueLabel();
        }

        UpdateWarning();
        SaveSoon();
    }

    private void BeginMasterGesture()
    {
        if (_selectedId != null || _masterDragging || _monitors.Count == 0)
            return;
        _masterDragging = true;
        _masterSnapshot = _monitors.Select(monitor => monitor.Brightness).ToArray();
        _masterStart = (int)Math.Round(ActiveSlider.Value);
    }

    private void EndMasterGesture()
    {
        if (!_masterDragging)
            return;
        _masterDragging = false;
        _masterSnapshot = null;
        SyncMasterThumb();
        SaveSoon();
    }

    private void ActiveChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || _monitors.Count == 0)
            return;

        if (_selectedId != null)
        {
            var monitor = _monitors.FirstOrDefault(item => item.Id == _selectedId);
            if (monitor == null || IsPaused(monitor.Id))
                return;
            ApplyMonitorValue(monitor, (int)Math.Round(ActiveSlider.Value), fromRow: false);
            return;
        }

        int[] snapshot;
        int delta;
        if (_masterDragging && _masterSnapshot != null)
        {
            snapshot = _masterSnapshot;
            delta = (int)Math.Round(ActiveSlider.Value) - _masterStart;
        }
        else
        {
            var current = (int)Math.Round(ActiveSlider.Value);
            delta = current - _masterAnchor;
            if (delta == 0)
                return;
            snapshot = _monitors.Select(monitor => monitor.Brightness).ToArray();
            _masterAnchor = current;
        }

        _activeProfile = null;
        UpdateProfileAccent();
        var values = BrightnessMath.ApplyDelta(snapshot, delta);
        for (var i = 0; i < _monitors.Count; i++)
        {
            if (IsPaused(_monitors[i].Id))
                continue;
            _monitors[i].Brightness = values[i];
            _monitors[i].Warning = "";
            _levels[_monitors[i].Id] = values[i];
            RefreshRow(_monitors[i]);
            if (_monitors[i].Hardware)
                _bus.SetPercent(_monitors[i].Id, values[i]);
        }

        _overlay.Sync(OverlayView());
        UpdateValueLabel();
        UpdateWarning();
        SaveSoon();
    }

    private void SyncMasterThumb()
    {
        if (_selectedId != null)
            return;
        var levels = _monitors.Where(monitor => !IsPaused(monitor.Id)).Select(monitor => monitor.Brightness).ToList();
        if (levels.Count == 0)
            levels = _monitors.Select(monitor => monitor.Brightness).ToList();
        var average = levels.Count == 0 ? 100 : BrightnessMath.Average(levels);
        WithoutEvents(() => ActiveSlider.Value = average);
        _masterAnchor = average;
        UpdateValueLabel();
    }

    private void SyncActiveSlider()
    {
        if (_selectedId == null)
        {
            ActiveSlider.IsEnabled = _monitors.Any(monitor => !IsPaused(monitor.Id));
            SyncMasterThumb();
            UpdateProfileAccent();
            return;
        }

        var monitor = _monitors.FirstOrDefault(item => item.Id == _selectedId);
        if (monitor == null)
        {
            _selectedId = null;
            SyncMasterThumb();
            UpdateProfileAccent();
            return;
        }

        ActiveSlider.IsEnabled = !IsPaused(monitor.Id);
        WithoutEvents(() => ActiveSlider.Value = monitor.Brightness);
        UpdateValueLabel();
        UpdateProfileAccent();
    }

    private IReadOnlyList<MonitorState> OverlayView()
        => _monitors.Select(monitor =>
        {
            if (monitor.Hardware || !IsPaused(monitor.Id))
                return monitor;
            return new MonitorState
            {
                Id = monitor.Id,
                X = monitor.X,
                Y = monitor.Y,
                Width = monitor.Width,
                Height = monitor.Height,
                Brightness = 100
            };
        }).ToList();

    private void UpdateValueLabel() => ActiveValue.Text = ((int)Math.Round(ActiveSlider.Value)).ToString();

    private string SelectedLabel()
    {
        if (_activeProfile != null)
            return _activeProfile;
        if (_selectedId == null)
            return "Todos";
        var monitor = _monitors.FirstOrDefault(item => item.Id == _selectedId);
        return monitor == null ? "Todos" : DisplayName(monitor);
    }

    private string DisplayName(MonitorState monitor)
    {
        if (Settings.Names.TryGetValue(monitor.Id, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom.Trim();
        return monitor.Title;
    }

    private void RefreshNames()
    {
        foreach (var monitor in _monitors)
            RefreshRow(monitor);
        RebuildMonitorMenu();
        UpdateProfileAccent();
    }

    private void RefreshRow(MonitorState monitor)
    {
        if (!_rows.TryGetValue(monitor.Id, out var row))
            return;
        row.Title.Text = DisplayName(monitor);
        row.Slider.IsEnabled = !IsPaused(monitor.Id);
        row.Value.Text = IsPaused(monitor.Id) ? "Pausado" : monitor.Brightness.ToString();
        row.Warning.Text = monitor.Warning;
        row.Warning.Visibility = string.IsNullOrEmpty(monitor.Warning) ? Visibility.Collapsed : Visibility.Visible;
        if (Math.Abs(row.Slider.Value - monitor.Brightness) > 0.1)
            WithoutEvents(() => row.Slider.Value = monitor.Brightness);
    }

    private void OnHardwareFailed(string id)
    {
        var monitor = _monitors.FirstOrDefault(item => item.Id == id);
        if (monitor == null)
            return;

        if (_retried.Add(id))
        {
            monitor.Warning = "Sem resposta. Tentando de novo…";
            RefreshRow(monitor);
            UpdateWarning();
            ScheduleRescan();
            return;
        }

        monitor.Warning = "Sem resposta do monitor.";
        RefreshRow(monitor);
        UpdateWarning();
    }

    private void UpdateWarning()
    {
        string text = "";
        if (_selectedId != null)
            text = _monitors.FirstOrDefault(monitor => monitor.Id == _selectedId)?.Warning ?? "";
        else if (_monitors.Any(monitor => monitor.Warning.Length > 0))
            text = "Algum monitor não respondeu.";
        ShowProblem(text);
    }

    private void ShowProblem(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildMonitorMenu()
    {
        _monitorMenu.Children.Clear();
        _monitorMenu.Children.Add(MenuButton("Todos", "Mantém a diferença entre as telas", _selectedId == null, () =>
        {
            _selectedId = null;
            SyncActiveSlider();
            UpdateWarning();
        }));
        foreach (var monitor in _monitors)
        {
            var captured = monitor;
            var detail = IsPaused(captured.Id) ? "Pausado" : captured.Subtitle;
            _monitorMenu.Children.Add(MenuButton(DisplayName(captured), detail, _selectedId == captured.Id, () =>
            {
                _selectedId = captured.Id;
                SyncActiveSlider();
                UpdateWarning();
            }));
        }
    }

    private void RebuildProfileMenu()
    {
        _profileMenu.Children.Clear();
        if (Settings.Profiles.Count == 0)
        {
            _profileMenu.Children.Add(new TextBlock
            {
                Text = "Nenhum perfil ainda. Crie um na engrenagem.",
                Foreground = (Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 8, 10, 8),
                MaxWidth = 220
            });
            return;
        }

        foreach (var profile in Settings.Profiles.OrderBy(ProfileSort).ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            _profileMenu.Children.Add(BuildProfileButton(profile));
    }

    private static int ProfileSort(StoredProfile profile) => profile.Order <= 0 ? 100 : profile.Order;

    private bool EnsureDefaultProfiles()
    {
        if (Settings.DefaultsSeeded)
            return false;

        foreach (var preset in new (string Name, string Icon, int Level, int Order)[]
        {
            ("Noite", "moon", 15, 1),
            ("Leitura", "book", 35, 2),
            ("Trabalho", "work", 60, 3),
            ("Game", "game", 85, 4),
            ("Dia", "sun", 100, 5),
            ("Personalizado", "custom", 50, 6)
        })
        {
            if (Settings.Profiles.Any(profile => profile.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            Settings.Profiles.Add(new StoredProfile
            {
                Name = preset.Name,
                Icon = preset.Icon,
                Order = preset.Order,
                Uniform = preset.Level
            });
        }

        Settings.DefaultsSeeded = true;
        return true;
    }

    private System.Windows.Controls.Button BuildProfileButton(StoredProfile profile)
    {
        var selected = profile.Name == _activeProfile;
        var muted = (Brush)FindResource("MutedBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var onAccent = (Brush)FindResource("AccentOnBrush");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = selected ? onAccent : muted;
        var glyph = ProfileGlyph(profile.Icon, mark);
        var name = new TextBlock
        {
            Text = profile.Name,
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = mark,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var level = new TextBlock
        {
            Text = ProfileLevelText(profile),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = mark,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(level, 2);
        row.Children.Add(glyph);
        row.Children.Add(name);
        row.Children.Add(level);

        var normal = Brushes.Transparent;
        var hover = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x42));
        var button = new System.Windows.Controls.Button
        {
            Content = row,
            Style = ProfileRowStyle,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = selected ? accent : normal,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 7, 10, 7),
            Cursor = Cursors.Hand,
            Focusable = false
        };
        button.MouseEnter += (_, _) =>
        {
            if (!selected)
                button.Background = hover;
        };
        button.MouseLeave += (_, _) => button.Background = selected ? accent : normal;
        var profileName = profile.Name;
        button.Click += (_, _) =>
        {
            _monitorPopup.IsOpen = false;
            _profilePopup.IsOpen = false;
            ApplyProfile(profileName);
            Activate();
        };
        return button;
    }

    private static string ProfileLevelText(StoredProfile profile)
    {
        if (profile.Uniform is int uniform)
            return uniform + "%";
        var values = profile.Levels.Values.ToList();
        if (values.Count == 0)
            return "—";
        if (values.Distinct().Count() == 1)
            return values[0] + "%";
        return string.Join(" · ", values);
    }

    internal static UIElement ProfileGlyph(string icon, Brush stroke)
    {
        var glyph = icon switch
        {
            "moon" => "\uE708",
            "book" => "\uE8F1",
            "work" => "\uE821",
            "game" => "\uE7FC",
            "sun" => "\uE706",
            "star" => "\uE734",
            "music" => "\uE8D6",
            "film" => "\uE714",
            "heart" => "\uEB51",
            _ => "\uE9E9"
        };
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = stroke,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 16,
            Height = 16,
            TextAlignment = TextAlignment.Center
        };
    }

    internal static UIElement DualMonitorGlyph(Brush stroke)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        var big = new System.Windows.Shapes.Rectangle
        {
            Width = 10.5,
            Height = 8,
            RadiusX = 1.2,
            RadiusY = 1.2,
            Stroke = stroke,
            StrokeThickness = 1.35,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(big, 0.4);
        Canvas.SetTop(big, 1.2);
        canvas.Children.Add(big);
        var small = new System.Windows.Shapes.Rectangle
        {
            Width = 7.2,
            Height = 5.6,
            RadiusX = 1,
            RadiusY = 1,
            Stroke = stroke,
            StrokeThickness = 1.25,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(small, 8);
        Canvas.SetTop(small, 8.2);
        canvas.Children.Add(small);
        return canvas;
    }

    private static Style? _profileRowStyle;

    private static Style ProfileRowStyle
    {
        get
        {
            if (_profileRowStyle != null)
                return _profileRowStyle;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Background))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.BorderBrush))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.BorderThickness))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Padding))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border };
            var style = new Style(typeof(System.Windows.Controls.Button));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0)));
            _profileRowStyle = style;
            return style;
        }
    }

    private System.Windows.Controls.Button MenuButton(string title, string? subtitle, bool selected, Action click)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var onAccent = (Brush)FindResource("AccentOnBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var ink = selected ? onAccent : (Brush)FindResource("InkBrush");
        var detail = selected ? onAccent : muted;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = ink,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = detail,
                Opacity = selected ? 0.82 : 1,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        var normal = Brushes.Transparent;
        var hover = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x42));
        var button = new System.Windows.Controls.Button
        {
            Content = stack,
            Style = ProfileRowStyle,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = selected ? accent : normal,
            BorderBrush = selected ? accent : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 1 : 0),
            Padding = new Thickness(10, 8, 10, 8),
            Cursor = Cursors.Hand,
            Focusable = false,
            Foreground = ink
        };
        button.MouseEnter += (_, _) =>
        {
            if (!selected)
                button.Background = hover;
        };
        button.MouseLeave += (_, _) => button.Background = selected ? accent : normal;
        button.Click += (_, _) =>
        {
            _monitorPopup.IsOpen = false;
            _profilePopup.IsOpen = false;
            click();
            Activate();
        };
        return button;
    }

    private void ApplyProfile(string name)
    {
        var profile = Settings.Profiles.FirstOrDefault(item => item.Name == name);
        if (profile == null)
            return;

        foreach (var monitor in _monitors)
        {
            if (IsPaused(monitor.Id) || !TryProfileLevel(profile, monitor.Id, out var level))
                continue;
            monitor.Brightness = BrightnessMath.Clamp(level);
            monitor.Warning = "";
            _levels[monitor.Id] = monitor.Brightness;
            RefreshRow(monitor);
        }

        _activeProfile = name;
        UpdateProfileAccent();
        PushAll();
        SyncActiveSlider();
        RebuildProfileMenu();
        SaveSoon();
    }

    private static bool TryProfileLevel(StoredProfile profile, string id, out int level)
    {
        if (profile.Uniform is int uniform)
        {
            level = uniform;
            return true;
        }

        return profile.Levels.TryGetValue(id, out level);
    }

    private void UpdateProfileAccent()
    {
        var ink = new SolidColorBrush(Color.FromRgb(215, 221, 232));
        var accent = (Brush)FindResource("AccentBrush");
        var active = _activeProfile != null;
        ProfileButton.Foreground = active ? accent : ink;
        var icon = "sun";
        if (active)
        {
            var profile = Settings.Profiles.FirstOrDefault(item => item.Name == _activeProfile);
            if (profile != null && !string.IsNullOrWhiteSpace(profile.Icon))
                icon = profile.Icon;
        }

        ProfileGlyphHost.Content = ProfileGlyph(icon, active ? accent : ink);
        MonitorLabel.Text = SelectedLabel();
        UpdateMonitorGlyph(active ? accent : ink, icon);
    }

    private void UpdateMonitorGlyph(Brush stroke, string profileIcon)
    {
        if (_activeProfile != null)
        {
            MonitorGlyphHost.Content = ProfileGlyph(profileIcon, stroke);
            return;
        }

        if (_selectedId == null)
        {
            MonitorGlyphHost.Content = DualMonitorGlyph(stroke);
            return;
        }

        MonitorGlyphHost.Content = new Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Stroke = stroke,
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M1.4,2.2 H13.6 V9.4 H1.4 Z M5.2,11.6 H9.8 M7.5,9.4 V11.6")
        };
    }

    private void NudgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RepeatButton button || _monitors.Count == 0)
            return;
        var step = int.Parse((string)button.Tag);
        if (_selectedId == null)
        {
            ActiveSlider.Value = BrightnessMath.Clamp((int)Math.Round(ActiveSlider.Value) + step);
            return;
        }

        var monitor = _monitors.FirstOrDefault(item => item.Id == _selectedId);
        if (monitor == null)
            return;
        ApplyMonitorValue(monitor, monitor.Brightness + step, fromRow: true);
    }

    private void MonitorButton_Click(object sender, RoutedEventArgs e)
    {
        _profilePopup.IsOpen = false;
        _monitorPopup.IsOpen = !_monitorPopup.IsOpen;
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _monitorPopup.IsOpen = false;
        RebuildProfileMenu();
        _profilePopup.IsOpen = !_profilePopup.IsOpen;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _suspendHide++;
        _monitorPopup.IsOpen = false;
        _profilePopup.IsOpen = false;
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _suspendHide = Math.Max(0, _suspendHide - 1);
            Dispatcher.BeginInvoke(DismissIfIdle, DispatcherPriority.ApplicationIdle);
        };
        _settingsWindow.Show();
    }

    private void ExpandChanged(object sender, RoutedEventArgs e)
    {
        var open = ExpandButton.IsChecked == true;
        MonitorList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ListSeparator.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.ToolTip = open ? "Ocultar monitores" : "Mostrar monitores";
        ExpandGlyph.Data = Geometry.Parse(open
            ? "M1,4 L6,8 L11,4 M1,8 L6,12 L11,8"
            : "M1,8 L6,4 L11,8 M1,12 L6,8 L11,12");
        UpdateLayout();
        PinToTrayCorner();
        Dispatcher.BeginInvoke(PinToTrayCorner, DispatcherPriority.Loaded);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !IsInteractive(e.OriginalSource as DependencyObject))
            DragMove();
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBox)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private Popup CreatePopup(UIElement target, StackPanel menu, bool above)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x34, 0x3C, 0x4E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            MinWidth = above ? 248 : 220,
            MaxWidth = 340,
            Margin = above ? new Thickness(0, 0, 0, 10) : new Thickness(0, 8, 0, 12),
            Child = menu
        };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 4,
            Opacity = 0.4,
            Color = Colors.Black
        };
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = above ? PlacementMode.Custom : PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = card
        };
        if (above)
        {
            popup.CustomPopupPlacementCallback = (size, targetSize, _) =>
            {
                var x = Math.Max(8, targetSize.Width - size.Width - 8);
                var abovePoint = new System.Windows.Point(x, -size.Height - 4);
                var belowPoint = new System.Windows.Point(x, targetSize.Height + 4);
                var showAbove = true;
                try
                {
                    var screen = target.PointToScreen(abovePoint);
                    showAbove = screen.Y >= SystemParameters.WorkArea.Top + 4;
                }
                catch (InvalidOperationException)
                {
                    showAbove = true;
                }

                var chosen = showAbove ? abovePoint : belowPoint;
                return new[] { new CustomPopupPlacement(chosen, PopupPrimaryAxis.Horizontal) };
            };
        }

        return popup;
    }

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        foreach (var pair in _levels)
            Settings.Levels[pair.Key] = pair.Value;
        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex)
        {
            Log.Info("Falha ao salvar: " + ex.Message);
        }
    }

    private void WithoutEvents(Action action)
    {
        var previous = _suppress;
        _suppress = true;
        try
        {
            action();
        }
        finally
        {
            _suppress = previous;
        }
    }

    private static string CleanProfileName(string name)
    {
        name = name.Trim();
        return name.Length > 40 ? name[..40] : name;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exit)
            return;
        e.Cancel = true;
        Dismiss();
    }

    internal void SetAccent(string hex)
    {
        Settings.Accent = Theme.Normalize(hex);
        var color = Theme.Parse(Settings.Accent);
        Theme.Apply(color);
        ApplyIcons();
        UpdateProfileAccent();
        RebuildProfileMenu();
        RebuildMonitorMenu();
        Dispatcher.BeginInvoke(() => _settingsWindow?.ShowColors());
        SaveSoon();
    }

    private void LoadWindowIcon() => ApplyIcons();

    private void ApplyIcons()
    {
        var color = Theme.Parse(Settings.Accent);
        Icon = Theme.CreateWindowIcon(color);
        if (_tray == null)
            return;
        var next = Theme.CreateTrayIcon(color);
        var previous = _tray.Icon;
        _tray.Icon = next;
        if (previous != null && previous != Drawing.SystemIcons.Application)
            previous.Dispose();
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => Reveal());
        menu.Items.Add("Configurações", null, (_, _) => Dispatcher.Invoke(() => SettingsButton_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) =>
        {
            _exit = true;
            System.Windows.Application.Current.Shutdown();
        });

        var icon = new Forms.NotifyIcon
        {
            Icon = Theme.CreateTrayIcon(Theme.Parse(Settings.Accent)),
            Visible = true,
            Text = "Lumos",
            ContextMenuStrip = menu
        };
        icon.MouseDown += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                DismissTrayOverflow();
        };
        icon.MouseUp += (_, args) =>
        {
            if (args.Button != Forms.MouseButtons.Left)
                return;
            DismissTrayOverflow();
            Reveal();
        };
        return icon;
    }

    private static void DismissTrayOverflow()
    {
        foreach (var className in new[] { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" })
        {
            var hwnd = NativeMethods.FindWindow(className, null);
            if (hwnd != 0)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
        }
    }

    private sealed record MonitorRow(Border Root, TextBlock Title, TextBlock Value, TextBlock Warning, Slider Slider);
}

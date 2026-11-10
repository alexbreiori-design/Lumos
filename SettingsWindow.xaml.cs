using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using TextBox = System.Windows.Controls.TextBox;
using Lumos.Models;
using Lumos.Services;
using Point = System.Windows.Point;

namespace Lumos;

public partial class SettingsWindow : Window
{
    private enum ProfileMode
    {
        Browse,
        Create,
        Edit,
        Remove
    }

    private static readonly (string Key, string Tip)[] Icons =
    [
        ("moon", "Lua"),
        ("book", "Livro"),
        ("work", "Trabalho"),
        ("game", "Game"),
        ("sun", "Sol"),
        ("star", "Estrela"),
        ("music", "Música"),
        ("film", "Filme"),
        ("heart", "Coração"),
        ("custom", "Controles")
    ];

    private readonly MainWindow _main;
    private readonly Dictionary<string, int> _draftLevels = new();
    private readonly List<string> _order = new();
    private readonly ObservableCollection<ProfileRowVm> _profileItems = new();
    private bool _suppress;
    private bool _centerPending;
    private ProfileMode _mode = ProfileMode.Browse;
    private string? _editing;
    private string? _renamingMonitor;
    private string _icon = "sun";
    private ProfileRowVm? _dragItem;
    private Border? _ghostBody;
    private double _dragOffsetY;
    private bool _dragActive;
    private Point _dragStart;

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        MaxHeight = SystemParameters.WorkArea.Height - 16;
        ProfileListBox.ItemsSource = _profileItems;
        StartupCheck.Checked += StartupChanged;
        StartupCheck.Unchecked += StartupChanged;
        Loaded += (_, _) => CenterOnWorkArea();
        Reload();
        ShowColors();
        ShowBrowse();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            if (_dragActive)
            {
                CancelListDrag();
                e.Handled = true;
                return;
            }

            if (_mode is ProfileMode.Create or ProfileMode.Edit && EditorPanel.Visibility == Visibility.Visible)
                CancelEditor();
            else
                Close();
            e.Handled = true;
        };
    }

    public void Reload()
    {
        _suppress = true;
        StartupCheck.IsChecked = _main.Settings.StartWithWindows;
        RebuildMonitors();
        RefreshOrder();
        if (_editing != null && _main.Settings.Profiles.All(profile => profile.Name != _editing))
            CancelEditor();
        else
        {
            RebuildProfileList();
            if (EditorPanel.Visibility == Visibility.Visible)
                RebuildPerMonitor();
        }
        _suppress = false;
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void ShowColors()
    {
        Icon = Theme.CreateWindowIcon(Theme.Parse(_main.Settings.Accent));
        ColorChoices.Children.Clear();
        var current = Theme.Normalize(_main.Settings.Accent);
        foreach (var hex in Theme.Presets)
            ColorChoices.Children.Add(ColorSwatch(hex, hex.Equals(current, StringComparison.OrdinalIgnoreCase), custom: false));
        var custom = !Theme.Presets.Contains(current, StringComparer.OrdinalIgnoreCase);
        ColorChoices.Children.Add(ColorSwatch(custom ? current : "#2A3142", custom, custom: true));
        RebuildMonitors();
    }

    private void CenterOnWorkArea()
    {
        var work = SystemParameters.WorkArea;
        MaxHeight = work.Height - 16;
        UpdateLayout();
        Left = work.Left + Math.Max(0, (work.Width - ActualWidth) / 2);
        Top = work.Top + Math.Max(8, (work.Height - ActualHeight) / 2);
    }

    private void RequestCenter()
    {
        _centerPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_centerPending)
                return;
            _centerPending = false;
            CenterOnWorkArea();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || e.PreviousSize.Height <= 0)
            return;
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 1)
            return;
        RequestCenter();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            return;
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;
        DragMove();
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            return;
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;
        if (e.OriginalSource is DependencyObject source)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, TitleBar))
                    return;
                source = VisualTreeHelper.GetParent(source);
            }
        }

        // only empty chrome padding / non-controls
    }

    private Button ColorSwatch(string hex, bool selected, bool custom)
    {
        var color = Theme.Parse(hex);
        UIElement face;
        if (custom && !selected)
        {
            var plus = new Path
            {
                Data = Geometry.Parse("M8,3.2 V12.8 M3.2,8 H12.8"),
                Stroke = (Brush)FindResource("MutedBrush"),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            face = new Grid
            {
                Width = 16,
                Height = 16,
                Children =
                {
                    new Ellipse
                    {
                        Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x42)),
                        Width = 16,
                        Height = 16
                    },
                    plus
                }
            };
        }
        else
        {
            face = new Ellipse
            {
                Fill = new SolidColorBrush(color),
                Width = 16,
                Height = 16
            };
        }

        var ring = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            BorderBrush = selected ? (Brush)FindResource("InkBrush") : (Brush)FindResource("LineBrush"),
            Child = new Viewbox
            {
                Width = 16,
                Height = 16,
                Child = face,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = ring,
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = custom ? "Outra cor" : hex,
            Name = custom ? "CorPersonalizada" : "Cor" + hex.TrimStart('#')
        };
        button.Template = ColorSwatchTemplate;
        button.Click += (_, e) =>
        {
            if (!custom)
            {
                _main.SetAccent(hex);
                return;
            }

            OpenModernColorPicker(button);
        };
        return button;
    }

    private void OpenModernColorPicker(Button anchor)
    {
        var current = Theme.Parse(_main.Settings.Accent);
        RgbToHsv(current.R, current.G, current.B, out var hue, out var sat, out var val);

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true
        };

        var preview = new Ellipse { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        var hexBox = new TextBox { Style = (Style)FindResource("Field"), Width = 96, Text = Theme.ToHex(current) };
        var sv = new Border
        {
            Width = 220,
            Height = 140,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Cursor = Cursors.Cross
        };
        var hueBar = new Border
        {
            Height = 16,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Hand,
            ClipToBounds = true
        };
        var svMarker = new Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        var svCanvas = new Canvas { IsHitTestVisible = false };
        svCanvas.Children.Add(svMarker);
        sv.Child = svCanvas;

        void ApplyPreview()
        {
            var color = HsvToColor(hue, sat, val);
            preview.Fill = new SolidColorBrush(color);
            sv.Background = BuildSvBrush(hue);
            hexBox.Text = Theme.ToHex(color);
            var markerLeft = Math.Clamp(sat * Math.Max(1, sv.ActualWidth) - 5, 0, Math.Max(0, sv.ActualWidth - 10));
            var markerTop = Math.Clamp((1 - val) * Math.Max(1, sv.ActualHeight) - 5, 0, Math.Max(0, sv.ActualHeight - 10));
            Canvas.SetLeft(svMarker, markerLeft);
            Canvas.SetTop(svMarker, markerTop);
        }

        hueBar.Background = BuildHueBrush();
        ApplyPreview();
        sv.SizeChanged += (_, _) => ApplyPreview();

        void PickSv(Point point)
        {
            sat = Math.Clamp(point.X / sv.ActualWidth, 0, 1);
            val = 1 - Math.Clamp(point.Y / sv.ActualHeight, 0, 1);
            ApplyPreview();
        }

        void PickHue(Point point)
        {
            hue = Math.Clamp(point.X / Math.Max(1, hueBar.ActualWidth), 0, 1) * 360;
            ApplyPreview();
        }

        sv.MouseLeftButtonDown += (_, e) =>
        {
            sv.CaptureMouse();
            PickSv(e.GetPosition(sv));
        };
        sv.MouseMove += (_, e) =>
        {
            if (sv.IsMouseCaptured)
                PickSv(e.GetPosition(sv));
        };
        sv.MouseLeftButtonUp += (_, _) =>
        {
            if (sv.IsMouseCaptured)
                sv.ReleaseMouseCapture();
        };
        hueBar.MouseLeftButtonDown += (_, e) =>
        {
            hueBar.CaptureMouse();
            PickHue(e.GetPosition(hueBar));
        };
        hueBar.MouseMove += (_, e) =>
        {
            if (hueBar.IsMouseCaptured)
                PickHue(e.GetPosition(hueBar));
        };
        hueBar.MouseLeftButtonUp += (_, _) =>
        {
            if (hueBar.IsMouseCaptured)
                hueBar.ReleaseMouseCapture();
        };

        var apply = new Button { Content = "Usar cor", Style = (Style)FindResource("AccentButton"), Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (Theme.TryParse(hexBox.Text, out var typed))
                _main.SetAccent(Theme.ToHex(typed));
            else
                _main.SetAccent(Theme.ToHex(HsvToColor(hue, sat, val)));
            popup.IsOpen = false;
        };
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            if (Theme.TryParse(hexBox.Text, out var typed))
            {
                _main.SetAccent(Theme.ToHex(typed));
                popup.IsOpen = false;
                e.Handled = true;
            }
        };

        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(preview);
        row.Children.Add(hexBox);
        row.Children.Add(apply);

        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14),
            Child = new StackPanel { Children = { sv, hueBar, row } }
        };
        panel.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 6,
            Opacity = 0.45,
            Color = Colors.Black
        };
        popup.Child = panel;
        popup.IsOpen = true;
    }

    private static LinearGradientBrush BuildHueBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (var i = 0; i <= 6; i++)
            brush.GradientStops.Add(new GradientStop(HsvToColor(i * 60, 1, 1), i / 6.0));
        brush.Freeze();
        return brush;
    }

    private static Brush BuildSvBrush(double hue)
    {
        var horizontal = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        horizontal.GradientStops.Add(new GradientStop(Colors.White, 0));
        horizontal.GradientStops.Add(new GradientStop(HsvToColor(hue, 1, 1), 1));
        var vertical = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        vertical.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        vertical.GradientStops.Add(new GradientStop(Colors.Black, 1));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(horizontal, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        group.Children.Add(new GeometryDrawing(vertical, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        var rd = r / 255.0;
        var gd = g / 255.0;
        var bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;
        v = max;
        s = max <= 0 ? 0 : delta / max;
        if (delta <= 0.00001)
        {
            h = 0;
            return;
        }

        if (Math.Abs(max - rd) < 0.00001)
            h = 60 * (((gd - bd) / delta) % 6);
        else if (Math.Abs(max - gd) < 0.00001)
            h = 60 * (((bd - rd) / delta) + 2);
        else
            h = 60 * (((rd - gd) / delta) + 4);
        if (h < 0)
            h += 360;
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }

    private static ControlTemplate? _colorSwatchTemplate;

    private static ControlTemplate ColorSwatchTemplate
    {
        get
        {
            if (_colorSwatchTemplate != null)
                return _colorSwatchTemplate;
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            _colorSwatchTemplate = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
            return _colorSwatchTemplate;
        }
    }

    private void MonitorHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_renamingMonitor != null)
            return;
        if (e.NewSize.Width < 40 || e.NewSize.Height < 40)
            return;
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5 &&
            Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5)
            return;
        RebuildMonitors();
    }

    private void RebuildMonitors()
    {
        MonitorMap.Children.Clear();
        var monitors = _main.Monitors;
        EmptyMonitors.Visibility = monitors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (monitors.Count == 0)
        {
            MonitorMap.Width = 0;
            MonitorMap.Height = 0;
            return;
        }

        var pad = 8.0;
        var hostW = Math.Max(80, MonitorHost.ActualWidth - pad * 2);
        var hostH = Math.Max(80, MonitorHost.ActualHeight - pad * 2);
        if (hostW < 80 || hostH < 80)
            return;

        var minX = monitors.Min(monitor => monitor.X);
        var minY = monitors.Min(monitor => monitor.Y);
        var spanW = Math.Max(1.0, monitors.Max(monitor => monitor.X + monitor.Width) - minX);
        var spanH = Math.Max(1.0, monitors.Max(monitor => monitor.Y + monitor.Height) - minY);
        var scale = Math.Min(hostW / spanW, hostH / spanH) * 0.9;
        MonitorMap.Width = spanW * scale;
        MonitorMap.Height = spanH * scale;
        const double gap = 14;

        foreach (var monitor in monitors)
        {
            var captured = monitor;
            var paused = _main.IsPaused(captured.Id);
            var left = (captured.X - minX) * scale + gap / 2;
            var top = (captured.Y - minY) * scale + gap / 2;
            var width = captured.Width * scale - gap;
            var height = captured.Height * scale - gap;
            if (width < 36) width = 36;
            if (height < 32) height = 32;
            if (left + width > MonitorMap.Width - 0.5)
                width = Math.Max(24, MonitorMap.Width - left - 0.5);
            if (top + height > MonitorMap.Height - 0.5)
                height = Math.Max(24, MonitorMap.Height - top - 0.5);
            var label = DisplayName(captured);
            var accentColor = Theme.Parse(_main.Settings.Accent);
            var accent = new SolidColorBrush(accentColor);
            var muted = (Brush)FindResource("MutedBrush");
            var line = (Brush)FindResource("LineBrush");
            var nameColor = paused
                ? muted
                : new SolidColorBrush(Theme.ContrastingInk(accentColor));

            var nameText = new TextBlock
            {
                Text = label,
                Foreground = nameColor,
                FontWeight = FontWeights.SemiBold,
                FontSize = Math.Clamp(Math.Min(width, height) * 0.12, 12, 16),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 0, 10, 0),
                Cursor = Cursors.IBeam,
                Tag = "name"
            };

            var host = new Grid();
            host.Children.Add(nameText);

            if (_renamingMonitor == captured.Id)
            {
                var box = new TextBox
                {
                    Style = (Style)FindResource("Field"),
                    Text = _main.CustomName(captured.Id).Length > 0 ? _main.CustomName(captured.Id) : captured.Name,
                    FontSize = 12,
                    Padding = new Thickness(8, 4, 8, 4),
                    MaxWidth = Math.Max(60, width - 16),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = "name"
                };
                void Commit()
                {
                    _main.SetCustomName(captured.Id, box.Text);
                    _renamingMonitor = null;
                    RebuildMonitors();
                }

                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        _renamingMonitor = null;
                        RebuildMonitors();
                        e.Handled = true;
                        return;
                    }

                    if (e.Key != Key.Enter)
                        return;
                    Commit();
                    e.Handled = true;
                };
                host.Children.Clear();
                host.Children.Add(box);
                Dispatcher.BeginInvoke(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
            }
            else
            {
                nameText.MouseLeftButtonDown += (_, e) =>
                {
                    _renamingMonitor = captured.Id;
                    RebuildMonitors();
                    e.Handled = true;
                };
            }

            var tile = new Border
            {
                Width = width,
                Height = height,
                Background = paused ? Brushes.Transparent : accent,
                BorderBrush = paused ? line : accent,
                BorderThickness = new Thickness(1.6),
                CornerRadius = new CornerRadius(6),
                Child = host,
                Cursor = Cursors.Hand,
                ToolTip = captured.Hardware ? "Físico" : "Camada"
            };
            tile.MouseLeftButtonDown += (_, e) =>
            {
                if (_renamingMonitor == captured.Id)
                    return;
                if (e.OriginalSource is DependencyObject source && IsNameHit(source))
                    return;
                _main.SetPaused(captured.Id, !paused);
                RebuildMonitors();
                RebuildPerMonitor();
                e.Handled = true;
            };

            Canvas.SetLeft(tile, left);
            Canvas.SetTop(tile, top);
            MonitorMap.Children.Add(tile);
        }

        UpdateMonitorMethodLegend(monitors);
    }

    private void UpdateMonitorMethodLegend(IReadOnlyList<MonitorState> monitors)
    {
        if (MonitorMethodLegend == null)
            return;
        MonitorMethodLegend.Inlines.Clear();
        if (monitors.Count == 0)
            return;

        string Label(MonitorState monitor)
        {
            var custom = _main.CustomName(monitor.Id);
            return custom.Length > 0 ? custom : monitor.Name;
        }

        var accent = (Brush)FindResource("AccentTextBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var physical = monitors.Where(monitor => monitor.Hardware).Select(Label).ToList();
        var overlay = monitors.Where(monitor => !monitor.Hardware).Select(Label).ToList();
        var first = true;
        void Append(string title, IReadOnlyList<string> names)
        {
            if (names.Count == 0)
                return;
            if (!first)
                MonitorMethodLegend.Inlines.Add(new System.Windows.Documents.Run("  ·  ") { Foreground = muted });
            first = false;
            MonitorMethodLegend.Inlines.Add(new System.Windows.Documents.Run(title)
            {
                Foreground = accent,
                FontWeight = FontWeights.SemiBold
            });
            MonitorMethodLegend.Inlines.Add(new System.Windows.Documents.Run(": " + string.Join(", ", names))
            {
                Foreground = muted
            });
        }

        Append("Físico", physical);
        Append("Camada", overlay);
    }

    private static bool IsNameHit(DependencyObject source)
    {
        while (source != null)
        {
            if (source is FrameworkElement element && Equals(element.Tag, "name"))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void RefreshOrder()
    {
        _order.Clear();
        foreach (var profile in _main.Settings.Profiles
                     .OrderBy(profile => profile.Order <= 0 ? 100 : profile.Order)
                     .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            _order.Add(profile.Name);
    }

    private void RebuildProfileList()
    {
        _suppress = true;
        _profileItems.Clear();
        EmptyProfiles.Visibility = _order.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileListBox.Visibility = _order.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var ink = (Brush)FindResource("InkBrush");
        foreach (var name in _order)
        {
            var profile = _main.Settings.Profiles.FirstOrDefault(item => item.Name == name);
            if (profile == null)
                continue;
            _profileItems.Add(new ProfileRowVm
            {
                Name = profile.Name,
                Level = LevelLabel(profile),
                Icon = MainWindow.ProfileGlyph(string.IsNullOrWhiteSpace(profile.Icon) ? "custom" : profile.Icon, ink),
                GripVisibility = _mode == ProfileMode.Edit ? Visibility.Visible : Visibility.Collapsed,
                RemoveVisibility = _mode == ProfileMode.Remove ? Visibility.Visible : Visibility.Collapsed
            });
        }

        if (_editing != null)
            ProfileListBox.SelectedItem = _profileItems.FirstOrDefault(item => item.Name == _editing);
        else
            ProfileListBox.SelectedItem = null;
        _suppress = false;
    }

    private void ProfileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _mode != ProfileMode.Edit || _dragActive)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_suppress || _mode != ProfileMode.Edit || _dragActive)
                return;
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;
            if (ProfileListBox.SelectedItem is ProfileRowVm row && !row.IsPlaceholder)
                OpenEditor(row.Name);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RemoveProfileRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;
        _main.DeleteProfileNamed(name);
        if (_editing == name)
            CancelEditor();
        RefreshOrder();
        RebuildProfileList();
        SetStatus("Perfil \"" + name + "\" removido.");
    }

    private void ProfileListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != ProfileMode.Edit)
            return;
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (FindAncestorButton(source) != null && !IsGripSource(source))
            return;

        var row = FindRowVm(source);
        if (row == null || row.IsPlaceholder)
            return;

        _dragItem = row;
        _dragActive = false;
        _dragStart = e.GetPosition(ProfileListBox);
        var host = (IInputElement?)FindListBoxItem(source) ?? ProfileListBox;
        _dragOffsetY = e.GetPosition(host).Y;
        ProfileListBox.CaptureMouse();
        // Don't mark handled yet — selection can still happen; drag starts after threshold.
    }

    private void ProfileListBox_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragItem == null)
            return;
        if (!ProfileListBox.IsMouseCaptured && e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(ProfileListBox);
        if (!_dragActive)
        {
            if (Math.Abs(pos.X - _dragStart.X) < 5 && Math.Abs(pos.Y - _dragStart.Y) < 5)
                return;
            StartListDrag();
        }

        MoveGhost(e.GetPosition(DragCanvas));
        MoveDraggedItem(InsertIndexAt(pos.Y));
    }

    private void ProfileListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem == null)
            return;
        if (_dragActive)
        {
            CommitListDrag();
            e.Handled = true;
        }
        else
        {
            // click without drag — release capture and allow selection/edit
            var row = _dragItem;
            CleanupListDrag();
            if (row != null)
                OpenEditor(row.Name);
        }
    }

    private void ProfileListBox_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragItem == null)
            return;
        if (_dragActive)
            CommitListDrag();
        else
            CleanupListDrag();
    }

    private void StartListDrag()
    {
        if (_dragItem == null || _dragActive)
            return;
        _dragActive = true;
        _dragItem.IsPlaceholder = true;
        Mouse.OverrideCursor = Cursors.SizeAll;

        _ghostBody = BuildGhost(_dragItem);
        _ghostBody.Width = Math.Max(180, ProfileListBox.ActualWidth - 8);
        DragCanvas.Children.Clear();
        DragCanvas.Children.Add(_ghostBody);
        DragCanvas.IsHitTestVisible = false;
    }

    private Border BuildGhost(ProfileRowVm row)
    {
        var ink = (Brush)FindResource("InkBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        var grip = new Border
        {
            Width = 22,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock { Text = "⠿", Foreground = muted, FontSize = 16, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Grid.SetColumn(grip, 0);
        grid.Children.Add(grip);
        var icon = new ContentControl
        {
            Content = MainWindow.ProfileGlyph(FindIconKey(row.Name), ink),
            Margin = new Thickness(2, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        var title = new TextBlock { Text = row.Name, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);
        var level = new TextBlock
        {
            Text = row.Level,
            Foreground = muted,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(level, 3);
        grid.Children.Add(level);

        return new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 7, 8, 7),
            Opacity = 0.96,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 5,
                Opacity = 0.45,
                Color = Colors.Black
            },
            Child = grid
        };
    }

    private string FindIconKey(string name)
    {
        var profile = _main.Settings.Profiles.FirstOrDefault(item => item.Name == name);
        return string.IsNullOrWhiteSpace(profile?.Icon) ? "custom" : profile!.Icon;
    }

    private void MoveGhost(Point canvasPoint)
    {
        if (_ghostBody == null)
            return;
        Canvas.SetLeft(_ghostBody, canvasPoint.X - 24);
        Canvas.SetTop(_ghostBody, canvasPoint.Y - _dragOffsetY);
    }

    private int InsertIndexAt(double y)
    {
        var index = 0;
        for (var i = 0; i < _profileItems.Count; i++)
        {
            var container = ProfileListBox.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), ProfileListBox).Y;
            var mid = top + container.ActualHeight / 2;
            if (y < mid)
                return index;
            index++;
        }

        return _profileItems.Count;
    }

    private void MoveDraggedItem(int insertAt)
    {
        if (_dragItem == null)
            return;
        var from = _profileItems.IndexOf(_dragItem);
        if (from < 0)
            return;
        if (insertAt > from)
            insertAt--;
        insertAt = Math.Clamp(insertAt, 0, _profileItems.Count - 1);
        if (insertAt == from)
            return;
        _profileItems.Move(from, insertAt);
    }

    private void CommitListDrag()
    {
        if (_dragItem != null)
            _dragItem.IsPlaceholder = false;
        _order.Clear();
        foreach (var item in _profileItems)
            _order.Add(item.Name);
        _main.ReorderProfiles(_order);
        CleanupListDrag();
        SetStatus("Ordem dos perfis atualizada.");
    }

    private void CancelListDrag()
    {
        if (_dragItem != null)
            _dragItem.IsPlaceholder = false;
        CleanupListDrag();
        RefreshOrder();
        RebuildProfileList();
    }

    private void CleanupListDrag()
    {
        _dragActive = false;
        _dragItem = null;
        _ghostBody = null;
        DragCanvas.Children.Clear();
        Mouse.OverrideCursor = null;
        if (ProfileListBox.IsMouseCaptured)
            ProfileListBox.ReleaseMouseCapture();
    }

    private static bool IsGripSource(DependencyObject source)
    {
        while (source != null)
        {
            if (source is FrameworkElement element && Equals(element.Tag, "grip"))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static ProfileRowVm? FindRowVm(DependencyObject source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { DataContext: ProfileRowVm row })
                return row;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject source)
    {
        while (source != null)
        {
            if (source is ListBoxItem item)
                return item;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static Button? FindAncestorButton(DependencyObject source)
    {
        while (source != null)
        {
            if (source is Button button)
                return button;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void ShowBrowse()
    {
        _mode = ProfileMode.Browse;
        _editing = null;
        EditorPanel.Visibility = Visibility.Collapsed;
        ProfileActions.Visibility = Visibility.Visible;
        ProfilesHint.Text = "Ícone, nome e brilho de cada perfil.";
        StyleModeButtons();
        RebuildProfileList();
        RequestCenter();
        SetStatus("");
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        _mode = ProfileMode.Create;
        _editing = null;
        _icon = "sun";
        _draftLevels.Clear();
        _suppress = true;
        ProfileName.Text = "";
        PerMonitorCheck.IsChecked = false;
        ProfileLevel.Value = _main.CurrentBrightness();
        ProfileLevelText.Text = ((int)Math.Round(ProfileLevel.Value)).ToString();
        _suppress = false;
        EditorTitle.Text = "Novo perfil";
        CommitProfileButton.Content = "Criar";
        EditorPanel.Visibility = Visibility.Visible;
        ProfilesHint.Text = "Defina ícone, nome e Lumos.";
        StyleModeButtons();
        ShowIcons();
        RebuildPerMonitor();
        RebuildProfileList();
        RequestCenter();
        ProfileName.Focus();
        SetStatus("");
    }

    private void EditMode_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == ProfileMode.Edit && EditorPanel.Visibility != Visibility.Visible)
        {
            ShowBrowse();
            return;
        }

        _mode = ProfileMode.Edit;
        EditorPanel.Visibility = Visibility.Collapsed;
        _editing = null;
        ProfilesHint.Text = "Arraste para reordenar ou clique para editar.";
        StyleModeButtons();
        RebuildProfileList();
        RequestCenter();
        SetStatus("");
    }

    private void RemoveMode_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == ProfileMode.Remove)
        {
            ShowBrowse();
            return;
        }

        _mode = ProfileMode.Remove;
        EditorPanel.Visibility = Visibility.Collapsed;
        _editing = null;
        ProfilesHint.Text = "Toque no X da linha para remover.";
        StyleModeButtons();
        RebuildProfileList();
        RequestCenter();
        SetStatus("");
    }

    private void StyleModeButtons()
    {
        NewProfileButton.Style = (Style)FindResource(_mode == ProfileMode.Create ? "AccentButton" : "QuietButton");
        EditModeButton.Style = (Style)FindResource(_mode == ProfileMode.Edit ? "AccentButton" : "QuietButton");
        RemoveModeButton.Style = (Style)FindResource(_mode == ProfileMode.Remove ? "AccentButton" : "QuietButton");
    }

    private void OpenEditor(string name)
    {
        var profile = _main.Settings.Profiles.FirstOrDefault(item => item.Name == name);
        if (profile == null)
            return;

        _mode = ProfileMode.Edit;
        _editing = profile.Name;
        _icon = string.IsNullOrWhiteSpace(profile.Icon) ? "custom" : profile.Icon;
        _draftLevels.Clear();
        foreach (var pair in profile.Levels)
            _draftLevels[pair.Key] = pair.Value;
        _suppress = true;
        ProfileName.Text = profile.Name;
        if (profile.Uniform is int level)
        {
            PerMonitorCheck.IsChecked = false;
            ProfileLevel.Value = level;
        }
        else
        {
            PerMonitorCheck.IsChecked = true;
            var sample = profile.Levels.Values.DefaultIfEmpty(_main.CurrentBrightness()).First();
            ProfileLevel.Value = sample;
        }

        ProfileLevelText.Text = ((int)Math.Round(ProfileLevel.Value)).ToString();
        _suppress = false;
        EditorTitle.Text = "Editar perfil";
        CommitProfileButton.Content = "Salvar";
        EditorPanel.Visibility = Visibility.Visible;
        ProfilesHint.Text = "Arraste para reordenar ou ajuste o perfil abaixo.";
        StyleModeButtons();
        ShowIcons();
        RebuildPerMonitor();
        RebuildProfileList();
        RequestCenter();
        SetStatus("");
    }

    private void CancelEditor_Click(object sender, RoutedEventArgs e) => CancelEditor();

    private void CancelEditor()
    {
        if (_mode == ProfileMode.Create)
            ShowBrowse();
        else
        {
            _editing = null;
            EditorPanel.Visibility = Visibility.Collapsed;
            _mode = ProfileMode.Edit;
            ProfilesHint.Text = "Arraste para reordenar ou clique para editar.";
            StyleModeButtons();
            RebuildProfileList();
            RequestCenter();
            SetStatus("");
        }
    }

    private void ShowIcons()
    {
        IconChoices.Children.Clear();
        IconChoices.Columns = Icons.Length;
        for (var i = 0; i < Icons.Length; i++)
        {
            var (key, tip) = Icons[i];
            var selected = key == _icon;
            var accentColor = Theme.Parse(_main.Settings.Accent);
            var onAccent = new SolidColorBrush(Theme.ContrastingInk(accentColor));
            var muted = (Brush)FindResource("MutedBrush");
            var glyph = MainWindow.ProfileGlyph(key, selected ? onAccent : muted);
            var plate = new Border
            {
                Height = 28,
                CornerRadius = new CornerRadius(5),
                Background = selected ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
                BorderBrush = selected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                Child = glyph,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            var captured = key;
            var button = new Button
            {
                Content = plate,
                Height = 28,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = tip
            };
            button.Template = ColorSwatchTemplate;
            button.Click += (_, _) =>
            {
                _icon = captured;
                ShowIcons();
            };
            IconChoices.Children.Add(button);
        }
    }

    private void RebuildPerMonitor()
    {
        if (PerMonitorLevels == null || PerMonitorCheck == null || GlobalBrightness == null)
            return;
        PerMonitorLevels.Children.Clear();
        var perMonitor = PerMonitorCheck.IsChecked == true;
        PerMonitorLevels.Visibility = perMonitor ? Visibility.Visible : Visibility.Collapsed;
        GlobalBrightness.Visibility = perMonitor ? Visibility.Collapsed : Visibility.Visible;
        if (!perMonitor)
        {
            RequestCenter();
            return;
        }

        var active = _main.Monitors.Where(monitor => !_main.IsPaused(monitor.Id)).ToList();
        if (active.Count == 0)
        {
            PerMonitorLevels.Children.Add(new TextBlock
            {
                Text = "Todos os monitores estão pausados.",
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(2, 0, 0, 6)
            });
            RequestCenter();
            return;
        }

        foreach (var monitor in active)
        {
            var captured = monitor;
            var value = _draftLevels.TryGetValue(captured.Id, out var stored) ? stored : (int)Math.Round(ProfileLevel.Value);
            var slider = new Slider { Value = value, Margin = new Thickness(0, 0, 8, 0) };
            var readout = new TextBlock
            {
                Text = value.ToString(),
                Width = 28,
                Foreground = (Brush)FindResource("InkBrush"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            slider.ValueChanged += (_, _) =>
            {
                var level = (int)Math.Round(slider.Value);
                _draftLevels[captured.Id] = level;
                readout.Text = level.ToString();
            };
            var line = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = DisplayName(captured),
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(readout, 2);
            line.Children.Add(title);
            line.Children.Add(slider);
            line.Children.Add(readout);
            PerMonitorLevels.Children.Add(line);
        }

        RequestCenter();
    }

    private string DisplayName(MonitorState monitor)
    {
        var custom = _main.CustomName(monitor.Id);
        return custom.Length > 0 ? custom : monitor.Name;
    }

    private Dictionary<string, int> CollectLevels()
    {
        var levels = new Dictionary<string, int>();
        foreach (var monitor in _main.Monitors.Where(monitor => !_main.IsPaused(monitor.Id)))
            levels[monitor.Id] = _draftLevels.TryGetValue(monitor.Id, out var level)
                ? BrightnessMath.Clamp(level)
                : (int)Math.Round(ProfileLevel.Value);
        return levels;
    }

    private static string LevelLabel(StoredProfile profile)
    {
        if (profile.Uniform is int uniform)
            return uniform + "%";
        var values = profile.Levels.Values.ToList();
        if (values.Count == 0)
            return "—";
        if (values.Distinct().Count() == 1)
            return values[0] + "%";
        return "misto";
    }

    private void ProfileLevel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ProfileLevelText == null)
            return;
        ProfileLevelText.Text = ((int)Math.Round(ProfileLevel.Value)).ToString();
    }

    private void PerMonitor_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || PerMonitorLevels == null)
            return;
        if (PerMonitorCheck.IsChecked == true)
        {
            var seed = (int)Math.Round(ProfileLevel.Value);
            foreach (var monitor in _main.Monitors.Where(monitor => !_main.IsPaused(monitor.Id)))
                _draftLevels.TryAdd(monitor.Id, seed);
        }

        RebuildPerMonitor();
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;
        _main.ApplyStartup(StartupCheck.IsChecked == true);
    }

    private void CommitProfile_Click(object sender, RoutedEventArgs e)
    {
        var previous = _mode == ProfileMode.Create ? null : _editing;
        var name = ProfileName.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("Dê um nome ao perfil.");
            return;
        }

        if (name.Length > 40)
            name = name[..40];
        var perMonitor = PerMonitorCheck.IsChecked == true;
        if (!_main.SaveEditedProfile(previous, name, _icon, perMonitor, (int)Math.Round(ProfileLevel.Value), CollectLevels()))
        {
            SetStatus("Já existe um perfil com esse nome.");
            return;
        }

        var created = previous == null;
        RefreshOrder();
        if (created)
        {
            ShowBrowse();
            SetStatus("Perfil \"" + name + "\" criado.");
            return;
        }

        _editing = name;
        RebuildProfileList();
        SetStatus("Perfil \"" + name + "\" salvo.");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _main.RequestScan();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button or TextBox or Slider or CheckBox or System.Windows.Controls.Primitives.ScrollBar)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private sealed class ProfileRowVm : INotifyPropertyChanged
    {
        private bool _isDragging;
        private bool _isPlaceholder;

        public string Name { get; init; } = "";
        public string Level { get; init; } = "";
        public UIElement Icon { get; init; } = new Grid();
        public Visibility GripVisibility { get; init; } = Visibility.Collapsed;
        public Visibility RemoveVisibility { get; init; } = Visibility.Collapsed;

        public bool IsDragging
        {
            get => _isDragging;
            set
            {
                if (_isDragging == value)
                    return;
                _isDragging = value;
                OnPropertyChanged();
            }
        }

        public bool IsPlaceholder
        {
            get => _isPlaceholder;
            set
            {
                if (_isPlaceholder == value)
                    return;
                _isPlaceholder = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlaceholderVisibility));
                OnPropertyChanged(nameof(ContentVisibility));
            }
        }

        public Visibility PlaceholderVisibility => IsPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ContentVisibility => IsPlaceholder ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

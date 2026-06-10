using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PadBridge.Gui.Core;
using PadBridge.Gui.Evdev;

namespace PadBridge.Gui;

public partial class MainWindow : Window
{
    /// <summary>Devices the daemon creates; never sources, never capture inputs.</summary>
    private static bool IsPadBridgeDevice(string name) =>
        name.StartsWith("PadBridge Virtual") || name.EndsWith("(PadBridge)");

    private readonly ObservableCollection<MappingRow> _rows = new();
    private readonly ConfigStore _store = new();
    private readonly InputMonitor _monitor = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _bannerTimer;
    private List<InputDeviceInfo> _devices = new();
    private BridgeConfig _config = new();
    private MappingRow? _capturingRow;
    private string _serviceState = "unknown";

    public MainWindow()
    {
        InitializeComponent();
        MappingList.ItemsSource = _rows;

        _monitor.KeyEvent += OnEvdevKey;

        _store.EnsureInitialized();
        if (File.Exists(ConfigFile.DefaultPath))
        {
            // The active file is the live truth (it may carry manual edits).
            _config = ConfigFile.Load(ConfigFile.DefaultPath);
            if (_config.Name != null && !File.Exists(ConfigStore.PathFor(_config.Name)))
                _config.Name = null;   // its library copy was deleted
        }
        else
        {
            _config = _store.Load(_store.ListNames().First());
            _store.Activate(_config);
        }
        RefreshConfigList(_config.Name);
        GrabCheck.IsChecked = _config.Grab;

        RefreshDevices(preferName: _config.Device);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += async (_, _) => await RefreshServiceStatus();
        _statusTimer.Start();

        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _bannerTimer.Tick += (_, _) => { Banner.IsVisible = false; _bannerTimer.Stop(); };
        UpdateConfigTooltip();
        _ = RefreshServiceStatus();

        Closing += (_, _) => _monitor.Dispose();
    }

    // ---- devices ----

    private void RefreshDevices(string? preferName = null)
    {
        var previous = preferName ?? (DeviceCombo.SelectedItem as InputDeviceInfo)?.Name;

        _devices = InputDeviceInfo.Enumerate()
            .Where(d => !IsPadBridgeDevice(d.Name))
            .ToList();

        var gamepads = _devices.Where(d => d.IsGamepad).ToList();
        DeviceCombo.ItemsSource = gamepads;
        DeviceCombo.SelectedItem =
            gamepads.FirstOrDefault(d => d.Name == previous) ?? gamepads.FirstOrDefault();

        RestartMonitor();
        RebuildRows();
    }

    private InputDeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as InputDeviceInfo;

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        try
        {
            RefreshDevices();
            var found = (DeviceCombo.ItemsSource as IEnumerable<InputDeviceInfo>)?.Count() ?? 0;
            ShowBanner(found > 0
                    ? $"Controller list refreshed — {found} controller{(found == 1 ? "" : "s")} found."
                    : "Controller list refreshed — no controllers found. Is it plugged in / turned on?",
                success: found > 0);
        }
        catch (Exception ex)
        {
            ShowBanner($"Refreshing controllers failed: {ex.Message}", success: false);
        }
    }

    // ---- config library ----

    private void RefreshConfigList(string? select)
    {
        ConfigCombo.ItemsSource = _store.ListNames();
        ConfigCombo.SelectedItem = select;
    }

    private void OnConfigSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ConfigCombo.SelectedItem is not string name || name == _config.Name) return;
        _config = _store.Load(name);
        _store.Activate(_config);
        GrabCheck.IsChecked = _config.Grab;
        RefreshDevices(preferName: _config.Device);
        UpdateConfigTooltip();
        SaveButton.Content = "Save config";
        SetHint($"Config '{name}' is now active.");
    }

    private void OnNewConfig(object? sender, RoutedEventArgs e)
    {
        _config = new BridgeConfig { Device = SelectedDevice?.Name ?? "" };
        ConfigCombo.SelectedItem = null;
        GrabCheck.IsChecked = false;
        RebuildRows();
        UpdateConfigTooltip();
        MarkDirty();
        SetHint("New config: bind some buttons, then Save — you'll be asked to name it. " +
                "The previous config stays active until then.");
    }

    private void OnGrabChanged(object? sender, RoutedEventArgs e)
    {
        var grab = GrabCheck.IsChecked == true;
        if (grab == _config.Grab) return;
        _config.Grab = grab;
        MarkDirty();
        SetHint(grab
                ? "Exclusive mode: the bridge takes over the controller and remaps buttons in place. Stop it (■) while remapping here, and note rumble is disabled."
                : "Exclusive mode off: button-to-button mappings won't be visible to games.",
            warning: true);
    }

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice is { } dev && dev.Name != _config.Device)
        {
            _config.Device = dev.Name;
            MarkDirty();
        }
        RebuildRows();
    }

    private void RebuildRows()
    {
        CancelCapture();
        _rows.Clear();

        // Left column: every button the device reports, plus any configured
        // sources the device doesn't currently advertise (e.g. unplugged).
        var sources = new List<int>(SelectedDevice?.SupportedKeyCodes ?? Array.Empty<int>());
        foreach (var src in _config.Mappings.Keys)
            if (!sources.Contains(src))
                sources.Add(src);

        foreach (var src in sources.OrderBy(c => c))
        {
            var row = new MappingRow(src);
            if (_config.Mappings.TryGetValue(src, out var dst))
                row.TargetCode = dst;
            _rows.Add(row);
        }
    }

    // ---- evdev events ----

    private void RestartMonitor()
    {
        // Monitor every key-capable device so capture can take input from
        // the keyboard or any controller. Some keyboards (e.g. QMK boards
        // with wheel emulation) also report pointer capabilities, so mouse
        // clicks are filtered out per-event in HandleKey, not per-device.
        _monitor.Start(_devices);
    }

    private void OnEvdevKey(InputMonitor.Key key) =>
        Dispatcher.UIThread.Post(() => HandleKey(key));

    private void HandleKey(InputMonitor.Key key)
    {
        if (_capturingRow is { } row)
        {
            if (key.Value != 1) return;
            // Ignore mouse buttons (BTN_LEFT..BTN_TASK) so interacting with
            // the UI during capture can't become the binding.
            if (key.Code is >= 0x110 and <= 0x117) return;
            if (key.Code == EventCodes.NameToCode["KEY_ESC"])
            {
                CancelCapture();
                return;
            }
            row.TargetCode = key.Code;
            row.IsCapturing = false;
            _capturingRow = null;
            _config.Mappings[row.SourceCode] = key.Code;
            MarkDirty();
            SetHint($"Bound {EventCodes.FriendlyName(row.SourceCode)} to {EventCodes.FriendlyName(key.Code)}.");
            return;
        }

        // Not capturing: light up the matching row so the user can find
        // which physical button is which.
        if (key.DevicePath != SelectedDevice?.Path) return;
        var match = _rows.FirstOrDefault(r => r.SourceCode == key.Code);
        if (match != null) match.IsHighlighted = key.Value != 0;
    }

    // ---- rebinding ----

    private void OnRebindClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not MappingRow row) return;
        CancelCapture();
        _capturingRow = row;
        row.IsCapturing = true;
        if (_config.Grab && _serviceState == "active")
            SetHint("Press a button or key. Esc cancels. NOTE: the bridge has exclusive control of the controller — stop it (■) if controller presses don't register.",
                warning: true);
        else
            SetHint("Press a button on your controller, or a key on your keyboard. Esc cancels.");
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not MappingRow row) return;
        if (_capturingRow == row) CancelCapture();
        row.TargetCode = null;
        _config.Mappings.Remove(row.SourceCode);
        MarkDirty();
    }

    private void CancelCapture()
    {
        if (_capturingRow is { } row) row.IsCapturing = false;
        _capturingRow = null;
        SetHint("Press buttons on your controller to find them in the list.");
    }

    // ---- config ----

    private void MarkDirty() => SaveButton.Content = "Save config ●";

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var wasUnnamed = _config.Name == null;
        if (wasUnnamed)
        {
            var name = await new NameConfigDialog().ShowDialog<string?>(this);
            if (name == null) return;
            _config.Name = name;
        }
        try
        {
            _store.Save(_config);
        }
        catch (Exception ex)
        {
            if (wasUnnamed) _config.Name = null;   // still unsaved; re-prompt next time
            ShowBanner($"Saving failed: {ex.Message}", success: false);
            return;
        }
        RefreshConfigList(_config.Name);
        UpdateConfigTooltip();
        SaveButton.Content = "Save config";
        ShowBanner($"Config '{_config.Name}' saved and activated.", success: true);
        SetHint($"Stored at {ConfigStore.PathFor(_config.Name!)}. The bridge reloads automatically.");
    }

    // ---- service control ----

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        await SystemdControl.StartAsync();
        await RefreshServiceStatus();
    }

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        await SystemdControl.StopAsync();
        await RefreshServiceStatus();
    }

    private async Task RefreshServiceStatus()
    {
        var state = await SystemdControl.StatusAsync();
        _serviceState = state;
        StatusText.Text = state;
        StatusDot.Fill = state switch
        {
            "active" => Brushes.LimeGreen,
            "failed" => Brushes.OrangeRed,
            _ => Brushes.Gray,
        };
        PlayButton.IsEnabled = state != "active";
        StopButton.IsEnabled = state == "active";
    }

    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F85149"));
    private static readonly IBrush BannerSuccessBrush = new SolidColorBrush(Color.Parse("#2EA043"));
    private static readonly IBrush BannerErrorBrush = new SolidColorBrush(Color.Parse("#D64545"));

    private void ShowBanner(string text, bool success)
    {
        BannerText.Text = text;
        Banner.Background = success ? BannerSuccessBrush : BannerErrorBrush;
        Banner.IsVisible = true;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void UpdateConfigTooltip()
    {
        var tip = _config.Name is { } name
            ? $"Stored at: {ConfigStore.PathFor(name)}"
            : $"Unsaved config — will be created in {ConfigStore.ConfigsDir}";
        ToolTip.SetTip(ConfigLabel, tip);
        ToolTip.SetTip(ConfigCombo, tip);
    }

    private void SetHint(string text, bool warning = false)
    {
        HintText.Text = text;
        HintText.Foreground = warning ? WarningBrush : HintBrush;
    }
}

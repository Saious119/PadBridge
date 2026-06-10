using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
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

    /// <summary>Default input names for devices PadBridge ships a companion
    /// daemon for; user nicknames (config labels) override these.</summary>
    private static Dictionary<string, Dictionary<int, string>>? _wellKnownLabels;
    private static Dictionary<string, Dictionary<int, string>> WellKnownLabels =>
        _wellKnownLabels ??= new()
        {
            ["Flydigi Vader 5 Pro Paddles"] = new()
            {
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY1"]] = "C",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY2"]] = "Z",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY3"]] = "M1 (back paddle)",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY4"]] = "M2 (back paddle)",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY5"]] = "M3 (back paddle)",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY6"]] = "M4 (back paddle)",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY7"]] = "LM (extra bumper)",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY8"]] = "RM (extra bumper)",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY9"]] = "O (circle)",
                [EventCodes.NameToCode["BTN_TRIGGER_HAPPY10"]] = "Home",
            },
        };

    private readonly ObservableCollection<MappingRow> _rows = new();
    private readonly ConfigStore _store = new();
    private readonly InputMonitor _monitor = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _bannerTimer;
    private List<InputDeviceInfo> _devices = new();
    private BridgeConfig _config = new();
    private MappingRow? _capturingRow;
    private bool _addingInput;
    /// <summary>Inputs added via "＋ Add input" this session; they persist in the
    /// config only once mapped or nicknamed.</summary>
    private readonly HashSet<int> _extraSources = new();
    private string _serviceState = "unknown";

    private readonly string? _version;
    private UpdateInfo? _update;
    private bool _updateInstalled;

    public MainWindow()
    {
        InitializeComponent();

        // Version comes from the repo's VERSION file via the csproj;
        // InformationalVersion may carry a "+commit" suffix - drop it.
        _version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion?.Split('+')[0];
        if (!string.IsNullOrEmpty(_version))
            Title = $"PadBridge {_version}";

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
        _ = CheckForUpdatesAsync();

        Closing += (_, _) => _monitor.Dispose();
    }

    // ---- updates ----

    private async Task CheckForUpdatesAsync()
    {
        if (_version == null) return;
        try
        {
            _update = await Updater.CheckAsync(_version);
        }
        catch
        {
            return;   // offline or rate-limited; we'll check again next launch
        }
        if (_update == null) return;
        UpdateText.Text = $"PadBridge {_update.Version} is available (you have {_version}).";
        UpdateBar.IsVisible = true;
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_updateInstalled) { Updater.RestartApp(); return; }
        if (_update == null) return;

        UpdateButton.IsEnabled = false;
        try
        {
            UpdateText.Text = $"Downloading PadBridge {_update.Version}...";
            var tarball = await Updater.DownloadAsync(_update);
            UpdateText.Text = "Installing...";
            await Updater.InstallAsync(tarball);
        }
        catch (Exception ex)
        {
            UpdateBar.IsVisible = false;
            UpdateButton.IsEnabled = true;
            ShowBanner($"Update failed: {ex.Message}", success: false);
            return;
        }
        _updateInstalled = true;
        UpdateText.Text = $"Updated to {_update.Version} — restart the app to finish.";
        UpdateButton.Content = "Restart";
        UpdateButton.IsEnabled = true;
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
        _extraSources.Clear();
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
        _extraSources.Clear();
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
                ? "Exclusive mode: the bridge takes over the controller and remaps buttons in place. Stop it (■) while remapping here."
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
        StopAddInput();
        _rows.Clear();

        // Left column: every button the device reports, plus any configured
        // or nicknamed sources the device doesn't currently advertise
        // (e.g. unplugged), plus inputs added by hand this session.
        var sources = new HashSet<int>(SelectedDevice?.SupportedKeyCodes ?? Array.Empty<int>());
        sources.UnionWith(_config.Mappings.Keys);
        sources.UnionWith(_config.Labels.Keys);
        sources.UnionWith(_extraSources);

        WellKnownLabels.TryGetValue(SelectedDevice?.Name ?? "", out var known);
        foreach (var src in sources.OrderBy(c => c))
        {
            var row = new MappingRow(src);
            if (_config.Mappings.TryGetValue(src, out var dst))
                row.TargetCode = dst;
            if (_config.Labels.TryGetValue(src, out var label))
                row.Nickname = label;
            else if (known != null && known.TryGetValue(src, out var name))
                row.Nickname = name;
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

        if (_addingInput && key.Value == 1)
        {
            if (key.Code is >= 0x110 and <= 0x117) return;   // mouse buttons
            if (key.Code == EventCodes.NameToCode["KEY_ESC"])
            {
                CancelAddInput();
                return;
            }
            if (key.DevicePath != SelectedDevice?.Path)
            {
                // Tell the user where the input actually lives: extra buttons
                // often arrive via a sibling device (e.g. a second HID
                // interface), which the bridge can't read from this config.
                SetHint($"That came from '{key.DeviceName}', not '{SelectedDevice?.Name}'. " +
                        "Still listening — press a button on the selected controller, or Esc to cancel.",
                    warning: true);
                return;
            }
            StopAddInput();
            if (_rows.FirstOrDefault(r => r.SourceCode == key.Code) is { } existing)
            {
                SetHint($"{EventCodes.FriendlyName(key.Code)} ({EventCodes.CanonicalName(key.Code)}) is already in the list.");
            }
            else
            {
                _extraSources.Add(key.Code);
                var added = new MappingRow(key.Code);
                _rows.Insert(_rows.TakeWhile(r => r.SourceCode < key.Code).Count(), added);
                SetHint($"Added {EventCodes.CanonicalName(key.Code)} — click \"(not mapped)\" to bind it, or ✎ to nickname it.");
            }
            // Fall through so the row lights up like any other press.
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

    // ---- adding inputs ----

    private void OnAddInput(object? sender, RoutedEventArgs e)
    {
        if (_addingInput)
        {
            CancelAddInput();
            return;
        }
        CancelCapture();
        _addingInput = true;
        AddInputButton.Content = "Listening… (Esc)";
        SetHint($"Press the input on '{SelectedDevice?.Name}' you want to add to the list. Esc cancels.");
    }

    private void StopAddInput()
    {
        _addingInput = false;
        AddInputButton.Content = "＋ Add input";
    }

    private void CancelAddInput()
    {
        StopAddInput();
        SetHint("Press buttons on your controller to find them in the list.");
    }

    // ---- nicknames ----

    private void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not MappingRow row) return;
        foreach (var r in _rows)
            if (r != row && r.IsEditingName)
                r.IsEditingName = false;
        row.NameDraft = row.Nickname ?? "";
        row.IsEditingName = true;

        var box = ((sender as Button)?.Parent as Grid)?.Children.OfType<TextBox>().FirstOrDefault();
        Dispatcher.UIThread.Post(() => { box?.Focus(); box?.SelectAll(); });
    }

    private void OnNicknameKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is not MappingRow row) return;
        if (e.Key == Key.Enter)
        {
            CommitNickname(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.IsEditingName = false;   // discard the draft
            e.Handled = true;
        }
    }

    private void OnNicknameLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is MappingRow { IsEditingName: true } row)
            CommitNickname(row);
    }

    private void CommitNickname(MappingRow row)
    {
        row.IsEditingName = false;
        var name = row.NameDraft.Trim();
        var nickname = name.Length == 0 ? null : name;
        if (nickname == row.Nickname) return;

        row.Nickname = nickname;
        if (nickname == null) _config.Labels.Remove(row.SourceCode);
        else _config.Labels[row.SourceCode] = nickname;
        // Keep the row around even if the device doesn't advertise this code.
        _extraSources.Add(row.SourceCode);
        MarkDirty();
        SetHint(nickname == null
            ? $"Nickname removed from {EventCodes.CanonicalName(row.SourceCode)}."
            : $"{EventCodes.CanonicalName(row.SourceCode)} will now show as \"{nickname}\".");
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

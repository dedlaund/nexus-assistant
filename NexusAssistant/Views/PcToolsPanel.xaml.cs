using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NexusAssistant.Services;

namespace NexusAssistant.Views;

public partial class PcToolsPanel : UserControl
{
    private sealed record ShortcutOption(QuickShortcut Key, string Label);
    private static readonly ShortcutOption[] Shortcuts =
    [
        new(QuickShortcut.Ctrl_Alt_N, "Ctrl + Alt + N"), new(QuickShortcut.Ctrl_Alt_Space, "Ctrl + Alt + пробел"),
        new(QuickShortcut.Ctrl_Shift_Space, "Ctrl + Shift + пробел"), new(QuickShortcut.S_F1, "S + F1"),
        new(QuickShortcut.Disabled, "Отключить глобальную комбинацию")
    ];

    private readonly HashSet<TextBlock> _running = [];
    private readonly SemaphoreSlim _save = new(1);
    private readonly DispatcherTimer _live = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
    private LocalStore? _store;
    private GameTracker? _tracker;
    private QuickInputHotkey? _hotkey;
    private CancellationToken _lifetime;
    private PcToolsSettings _settings = new();
    private bool _opened;
    private bool _ready;

    public PcToolsPanel()
    {
        InitializeComponent();
        _live.Tick += async (_, _) =>
        {
            if (IsVisible && Window.GetWindow(this)?.WindowState != WindowState.Minimized)
                await RunAsync(LoadResult, PcDiagnostics.LoadAsync);
        };
        Unloaded += (_, _) => _live.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && LiveLoad.IsChecked == true) _live.Start();
            else _live.Stop();
            if (!IsVisible || !_ready || _opened) return;
            _opened = true;
            _ = ReadDisksAsync();
            _ = ReadAudioAsync();
            RefreshGames();
        };
    }

    internal void Configure(LocalStore store, GameTracker tracker, QuickInputHotkey hotkey, PcToolsSettings settings,
        CancellationToken lifetime, string? error)
    {
        _store = store;
        _tracker = tracker;
        _hotkey = hotkey;
        _settings = settings;
        _lifetime = lifetime;
        Shortcut.ItemsSource = Shortcuts;
        Shortcut.SelectedItem = Shortcuts.FirstOrDefault(option => option.Key == settings.Shortcut) ?? Shortcuts[0];
        Games.Text = settings.Games;
        CloseApps.Text = settings.CloseForGames;
        ShortcutResult.Text = error;
        tracker.Updated += RefreshGames;
        _ready = true;
    }

    private async Task RunAsync(TextBlock output, Func<CancellationToken, Task<string>> action)
    {
        if (!_running.Add(output)) return;
        output.Text = "Выполняю…";
        try { output.Text = await action(_lifetime); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { output.Text = "Замер прерван по тайм-ауту."; }
        catch (Exception error) { output.Text = error.Message; }
        finally { _running.Remove(output); }
    }

    private async void Temperatures_Click(object sender, RoutedEventArgs e) => await RunAsync(SensorResult, PcDiagnostics.TemperaturesAsync);
    private async void Load_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadResult, PcDiagnostics.LoadAsync);
    private async void LiveLoad_Changed(object sender, RoutedEventArgs e)
    {
        if (LiveLoad.IsChecked == true && IsVisible)
        {
            _live.Start();
            await RunAsync(LoadResult, PcDiagnostics.LoadAsync);
        }
        else _live.Stop();
    }
    private async void Threats_Click(object sender, RoutedEventArgs e) => await RunAsync(DefenderResult, token => PcDiagnostics.DefenderAsync(false, token));
    private async void Defender_Click(object sender, RoutedEventArgs e) => await RunAsync(DefenderResult, token => PcDiagnostics.DefenderAsync(true, token));
    private async void Ping_Click(object sender, RoutedEventArgs e) => await RunAsync(PingResult, token => PcDiagnostics.PingAsync(PingHost.Text, token));
    private async void Speed_Click(object sender, RoutedEventArgs e) => await RunAsync(SpeedResult, PcDiagnostics.SpeedAsync);

    private async Task ReadDisksAsync() => await RunAsync(DiskResult, async token =>
    {
        var selected = (Disks.SelectedItem as PcDiagnostics.Disk)?.Root;
        var disks = await Task.Run(PcDiagnostics.Disks, token);
        Disks.ItemsSource = disks;
        Disks.SelectedItem = disks.FirstOrDefault(disk => disk.Root == selected) ?? disks.FirstOrDefault();
        return disks.Length == 0 ? "Доступные локальные диски не найдены." : "";
    });

    private async void Disks_Click(object sender, RoutedEventArgs e) => await ReadDisksAsync();

    private async void DiskScan_Click(object sender, RoutedEventArgs e)
    {
        if (Disks.SelectedItem is PcDiagnostics.Disk disk)
            await RunAsync(DiskResult, token => Task.Run(() => PcDiagnostics.ScanDisk(disk.Root, token), token));
    }

    private async void Recycle_Click(object sender, RoutedEventArgs e) => await RunAsync(RecycleResult, _ =>
        Task.FromResult(PcDiagnostics.EmptyRecycleBin(new WindowInteropHelper(Window.GetWindow(this)).Handle)));

    private async Task ReadAudioAsync() => await RunAsync(AudioResult, _ =>
    {
        var (devices, current) = AudioOutput.List();
        AudioDevices.ItemsSource = devices;
        AudioDevices.SelectedItem = devices.FirstOrDefault(device => device.Id == current) ?? devices.FirstOrDefault();
        return Task.FromResult(devices.Length == 0 ? "Активных устройств вывода нет." :
            current is null ? "Выберите устройство вывода." : $"Сейчас: {devices.FirstOrDefault(device => device.Id == current)?.Name ?? "системное устройство"}");
    });

    private async void AudioList_Click(object sender, RoutedEventArgs e) => await ReadAudioAsync();

    private async void AudioSet_Click(object sender, RoutedEventArgs e)
    {
        if (AudioDevices.SelectedItem is not AudioOutput.Device device) return;
        await RunAsync(AudioResult, _ =>
        {
            AudioOutput.SetDefault(device.Id);
            return Task.FromResult($"Вывод по умолчанию: {device.Name}");
        });
    }

    private async void AudioSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(AudioResult, _ =>
    {
        Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true })?.Dispose();
        return Task.FromResult("");
    });

    private void Quick_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowQuickInput();

    private async void Shortcut_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _hotkey is null || Shortcut.SelectedItem is not ShortcutOption option) return;
        var error = await SaveAsync(settings => settings with { Shortcut = option.Key });
        if (error is not null) { ShortcutResult.Text = error; return; }
        _hotkey.Shortcut = option.Key;
        ShortcutResult.Text = option.Key == QuickShortcut.Disabled ? "Глобальная комбинация отключена." :
            _hotkey.Active ? $"Комбинация сохранена · {option.Label}." : "Сохранено. Глобальная комбинация сейчас недоступна.";
    }

    private async void GameLists_Click(object sender, RoutedEventArgs e)
    {
        var games = Games.Text;
        var close = CloseApps.Text;
        var error = await SaveAsync(settings => settings with { Games = games, CloseForGames = close });
        if (error is not null) { GameStatus.Text = error; return; }
        _tracker?.SetGames(games);
        GameStatus.Text = "Списки сохранены.";
    }

    private async Task<string?> SaveAsync(Func<PcToolsSettings, PcToolsSettings> update)
    {
        await _save.WaitAsync();
        try
        {
            var next = update(_settings);
            await _store!.WriteAsync("pc-tools.json", next);
            _settings = next;
            return null;
        }
        catch (Exception error) { return $"Не удалось сохранить настройки. {error.Message}"; }
        finally { _save.Release(); }
    }

    private void GameMode_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker is null) return;
        if (_tracker.GamingMode)
        {
            _tracker.DisableMode();
            GameStatus.Text = "Игровой режим выключен.";
            return;
        }

        var names = GameTracker.Names(_settings.CloseForGames).Except(GameTracker.Names(_settings.Games), StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 0 && MessageBox.Show(Window.GetWindow(this),
                $"NEXUS отправит обычный запрос на закрытие следующим приложениям:\n{string.Join(", ", names)}\n\nВключить игровой режим?",
                "Игровой режим NEXUS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        GameStatus.Text = _tracker.EnableMode(_settings.CloseForGames);
    }

    private void RefreshGames()
    {
        if (!IsVisible || _tracker is null) return;
        GameMode.Content = _tracker.GamingMode ? "Выключить режим" : "Включить режим";
        var summary = _tracker.Describe();
        GameTime.Text = summary + (_tracker.StorageError is { } error ? $"\nХранилище: {error}" : "");
    }
}

using System.Windows;
using NexusAssistant.Services;
using NexusAssistant.Views;
using Forms = System.Windows.Forms;

namespace NexusAssistant;

public partial class App : Application
{
    private Mutex? _instance;
    private HttpClient? _http;
    private AssistantViewModel? _viewModel;
    private TelegramControl? _remote;
    private EventWaitHandle? _showWindow;
    private RegisteredWaitHandle? _showRegistration;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private readonly CancellationTokenSource _pcLifetime = new();
    private GameTracker? _games;
    private QuickInputHotkey? _quickHotkey;
    private QuickInputWindow? _quickWindow;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SessionEnding += (_, _) => _exiting = true;
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var data = Environment.GetEnvironmentVariable("NEXUS_ASSISTANT_DATA");
        var instanceName = @"Local\NexusAssistant";
        if (!string.IsNullOrWhiteSpace(data))
        {
            data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(data));
            var bytes = System.Text.Encoding.UTF8.GetBytes(data.ToUpperInvariant());
            instanceName += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        _showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".ShowWindow");
        _instance = new Mutex(true, instanceName, out var first);
        if (!first)
        {
            if (!background) _showWindow.Set();
            Shutdown();
            return;
        }

        if (string.IsNullOrWhiteSpace(data))
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusAssistant");
        var store = new LocalStore(Path.GetFullPath(data));
        _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(90) };
        var windows = new WindowsActions(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var browser = new MusicBrowser(windows.OpenWebsite, () => windows.Browser);
        var custom = new CustomCommands(store, windows);
        var router = new CommandRouter(windows, browser, new LocalChat(_http), custom);
        _viewModel = new AssistantViewModel(router, new WindowsSpeech(store.DirectoryPath), store, custom, value => windows.Browser = value);
        await _viewModel.InitializeAsync();
        _remote = new TelegramControl(store, windows);
        await _remote.LoadAsync();
        var window = new MainWindow(_viewModel, _remote);
        MainWindow = window;
        var tools = new PcToolsSettings();
        string? toolsNotice = null;
        try
        {
            tools = await store.ReadAsync("pc-tools.json", tools);
            tools = tools with
            {
                Games = tools.Games ?? new PcToolsSettings().Games,
                CloseForGames = tools.CloseForGames ?? "",
                Shortcut = Enum.IsDefined(tools.Shortcut) ? tools.Shortcut : new PcToolsSettings().Shortcut
            };
        }
        catch (Exception error) { toolsNotice = $"Не удалось прочитать настройки инструментов. {error.Message}"; }
        _games = new GameTracker(store);
        await _games.StartAsync(tools.Games);
        _quickHotkey = new QuickInputHotkey(Dispatcher, ShowQuickInput) { Shortcut = tools.Shortcut };
        try { _quickHotkey.Start(); }
        catch (System.ComponentModel.Win32Exception error)
        {
            toolsNotice = $"Глобальная комбинация недоступна. {error.Message}";
        }
        window.PcTools.Configure(store, _games, _quickHotkey, tools, _pcLifetime.Token, toolsNotice);
        CreateTray();
        window.Closing += (_, args) =>
        {
            if (_exiting || (!_remote.KeepRunning && !_games.GamingMode &&
                !(_quickHotkey.Active && _quickHotkey.Shortcut != QuickShortcut.Disabled))) return;
            args.Cancel = true;
            window.Hide();
        };
        window.Closed += (_, _) =>
        {
            if (!_exiting) Shutdown();
        };
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showWindow,
            (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
        if (!background) ShowWindow();
        _ = _remote.ResumeAsync();
    }

    private void CreateTray()
    {
        using var stream = GetResourceStream(new Uri("Assets/Nexus.ico", UriKind.Relative)).Stream;
        using var icon = new System.Drawing.Icon(stream);
        _trayIcon = (System.Drawing.Icon)icon.Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть NEXUS", null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
        menu.Items.Add("Быстрый ввод", null, (_, _) => Dispatcher.BeginInvoke(ShowQuickInput));
        menu.Items.Add("Выйти из NEXUS", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _exiting = true;
            Shutdown();
        }));
        _tray = new Forms.NotifyIcon { Text = "NEXUS Assistant", Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
    }

    private void ShowWindow()
    {
        if (_exiting || MainWindow is not { } window) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    internal void ShowQuickInput()
    {
        if (_exiting || _viewModel is null) return;
        (_quickWindow ??= new QuickInputWindow(_viewModel)).Open();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _showRegistration?.Unregister(null);
        _showWindow?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        _pcLifetime.Cancel();
        _quickHotkey?.Dispose();
        _games?.Dispose();
        _pcLifetime.Dispose();
        _viewModel?.Dispose();
        _remote?.Dispose();
        _http?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusAssistant;
using NexusAssistant.Services;
using Activity = NexusAssistant.Activity;

internal static class Program
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static int _passed;

    [STAThread]
    private static void Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task RunAsync(string[] args)
    {
        var directory = Path.GetFullPath(Path.Combine(".nexus-test", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var desktop = Directory.CreateDirectory(Path.Combine(directory, "desktop")).FullName;
        var store = new LocalStore(directory);
        var launch = new WindowsStub(new WindowsActions(desktop));
        var music = new MusicStub();
        var handler = new ChatHandler();
        using var client = new HttpClient(handler);
        var custom = new CustomCommands(store, launch);
        var chat = new LocalChat(client);
        var router = new CommandRouter(launch, music, chat, custom);
        Task<Reply> Send(string text) => router.RouteAsync(text, "", [], None);

        if (args.Contains("--render")) RenderLayout(router, store, custom, launch);

        Check(!(await Send("Нексус, можешь запустить Файрфокс?")).Error && launch.App == "firefox", "Russian aliases and invocation");
        await Send("Открой настройки Windows");
        Check(launch.App == "settings", "Windows settings route");
        await Send("Запусти Roblox Studio");
        Check(launch.App == "roblox", "Roblox Studio route");
        await Send("Открой приложение «Visual Studio Code»");
        Check(launch.App == "Visual Studio Code", "Installed shortcut name route");
        await Send("Открой настройки звука");
        Check(launch.App == "sound", "Specific Windows settings take priority");
        await Send("Открой загрузки");
        Check(launch.App == "downloads", "Downloads route");
        await Send("Найди в интернете, как открыть настройки");
        await Send("Найди в интернете фотографии Сатурна");
        Check(launch.Query == "фотографии Сатурна", "Firefox web search");
        Check(!(await Send("Открой ВК")).Error && launch.Website?.AbsoluteUri == "https://vk.com/",
            "VK uses the configured browser action");
        Check((await Send("Который час?")).Text.StartsWith("Сейчас "), "Local time command");

        await Send("Включи Яндекс Музыку");
        await Send("Следующая композиция");
        await Send("Верни предыдущий трек");
        await Send("Поставь лайк");
        await Send("Открой мои плейлисты");
        Check(music.Actions.SequenceEqual([MusicAction.Play, MusicAction.Next, MusicAction.Previous, MusicAction.Like, MusicAction.Playlists]), "Music intent routes");
        music.Actions.Clear();
        await Send("Открой яедекс музыка");
        await Send("я. музыка");
        await Send("Вкл янд муз");
        await Send("Аткрой яндекс муызку");
        Check(music.Actions.SequenceEqual([MusicAction.Open, MusicAction.Open, MusicAction.Play, MusicAction.Open]),
            "Yandex Music accepts abbreviations and single-character edits or transpositions");
        await Send("Почему Яндекс Музыка популярна?");
        await Send("Не аткрой яедекс музыку");
        Check(music.Actions.Count == 4, "Fuzzy recognition does not open music for questions or negated actions");
        var tabs = new List<Uri>();
        var firefox = new MusicBrowser(tabs.Add);
        Check(!(await firefox.RunAsync(MusicAction.Open, None)).Error && tabs[0].AbsoluteUri == "https://music.yandex.ru/",
            "Yandex Music opens via Firefox launcher");
        Check(!(await firefox.RunAsync(MusicAction.Playlists, None)).Error && tabs[1].AbsolutePath == "/collection/playlists",
            "Playlists open via Firefox launcher");

        var prompt = await Send("Создай папку на рабочем столе");
        Check(prompt.Text.StartsWith("Как назвать"), "Folder creation follow-up");
        Check(!(await Send("«Работа в архиве»")).Error && Directory.Exists(Path.Combine(desktop, "Работа в архиве")), "Real directory creation");
        Check(!(await Send("Переименуй папку «Работа в архиве» в «Черновик»")).Error, "Quoted rename with separator in source");
        Check(!Directory.Exists(Path.Combine(desktop, "Работа в архиве")) && Directory.Exists(Path.Combine(desktop, "Черновик")), "Real directory rename");
        await Send("Переименуй папку");
        await Send("Черновик");
        Check(!(await Send("Готово")).Error && Directory.Exists(Path.Combine(desktop, "Готово")), "Three-turn rename");
        Check((await Send("Создай папку «..\\Выход»")).Error && !Directory.Exists(Path.Combine(directory, "Выход")), "Traversal rejected");
        Check((await Send("Создай папку «NUL»")).Error, "Reserved Windows file names rejected");
        Check((await Send("Создай папку «Готово»")).Error, "Duplicate folder reported");
        await Send("Как создать папку Ошибка");
        Check(!Directory.Exists(Path.Combine(desktop, "Ошибка")), "Instructional question does not execute an action");
        await Send("Можешь объяснить, как создать папку Справка?");
        Check(!Directory.Exists(Path.Combine(desktop, "Справка")), "Indirect instructional question does not execute an action");
        await Send("Создай папку");
        await Send("Отмена");
        await Send("Отложено");
        Check(!Directory.Exists(Path.Combine(desktop, "Отложено")), "Cancel clears follow-up");

        var answer = await router.RouteAsync("Объясни гравитацию", "test-model", [new("Предыдущий вопрос", true, DateTimeOffset.Now)], None);
        Check(answer.Text == "Тестовый ответ" && handler.ValidRequest, "Ollama request, context, and response");
        handler.Status = HttpStatusCode.NotFound;
        Check((await router.RouteAsync("Что такое звезда?", "unknown", [], None)).Error, "Missing Ollama model reported");

        using (var model = new AssistantViewModel(router, new SpeechStub(), store))
        {
            await model.InitializeAsync();
            model.Intro = false;
            model.Browser = PreferredBrowser.Edge;
            model.ProfileName = "Анна";
            model.ClickSounds = false;
            await model.SaveSettingsAsync();
            model.Input = "Привет";
            await model.SendAsync();
            await model.ListenAsync();
            Check(model.Messages.Count == 4 && model.Messages[2].Text == "Спасибо" && model.Messages[3].Text == "Пожалуйста!",
                "Text and simulated voice through the view model");
        }
        var appliedBrowser = PreferredBrowser.Firefox;
        using (var restored = new AssistantViewModel(router, new SpeechStub(), store, applyBrowser: browser => appliedBrowser = browser))
        {
            await restored.InitializeAsync();
            Check(restored.Browser == PreferredBrowser.Edge && appliedBrowser == PreferredBrowser.Edge &&
                !restored.ClickSounds && restored.Greeting == "Анна, я на связи" && restored.ProfileInitial == "А",
                "Profile, click sound preference and browser survive restart");
            Check(!restored.Intro && restored.Messages.Count == 4 && restored.Messages[0].Time != default,
                "Reload settings and timestamped chat");
            await restored.ClearHistoryAsync();
            Check(restored.Empty && (await store.ReadAsync("history.json", Array.Empty<ChatMessage>())).Length == 0, "Clear history persisted");
            restored.Input = "Черновик";
            Check((await restored.SendQuickAsync("Открой ВК")).Contains("ВКонтакте") &&
                restored.Input == "Черновик" && restored.Messages.Count == 2, "Quick input preserves draft and records command");
        }

        using (var editor = new AssistantViewModel(router, new SpeechStub(), store, custom))
        {
            await editor.InitializeAsync();
            Check(editor.CanEditCommands && await editor.SaveCommandAsync(null, "Привет", CustomAction.Reply, "Рад тебя видеть!"),
                "Save custom reply from the command editor");
            editor.Input = "Нексус, ПРИВЕТ!";
            await editor.SendAsync();
            Check(editor.LatestReply == "Рад тебя видеть!", "Custom phrase runs through chat and overrides the greeting");
            Check(await editor.SaveCommandAsync(null, "Спасибо", CustomAction.Reply, "Мой голосовой ответ"), "Save custom voice phrase");
            await editor.ListenAsync();
            Check(editor.LatestReply == "Мой голосовой ответ", "Recognized speech uses custom commands");
            Check(!await editor.SaveCommandAsync(null, "привет?", CustomAction.Reply, "Дубликат") &&
                  !await editor.SaveCommandAsync(null, "Недоступный файл", CustomAction.OpenPath, Path.Combine(desktop, "missing.txt")) &&
                  !await editor.SaveCommandAsync(null, "Отмена", CustomAction.Reply, "Недоступно") &&
                  !await editor.SaveCommandAsync(null, "Запусти сайт", CustomAction.OpenWebsite, "file:///C:/test"),
                "Duplicate phrase, reserved phrase, missing path and unsafe URL rejected");
            var id = editor.UserCommands[0].Id;
            Check(await editor.SaveCommandAsync(id, "Открой проект", CustomAction.OpenPath, desktop) &&
                  !(await Send("Открой проект")).Error && launch.OpenedPath == desktop,
                "Edit custom command and launch a validated path");
            Check(await editor.SaveCommandAsync(null, "Мой сайт", CustomAction.OpenWebsite, "https://example.org/") &&
                  !(await Send("Мой сайт")).Error && launch.Website?.Host == "example.org",
                "Saved website action");
            Check(await editor.SaveCommandAsync(null, "Сатурн", CustomAction.Search, "снимки Сатурна") &&
                  !(await Send("Сатурн")).Error && launch.Query == "снимки Сатурна",
                "Saved search action");
            await editor.DeleteCommandAsync(id);
        }
        var reloaded = new CustomCommands(store, launch);
        await reloaded.LoadAsync();
        Check(reloaded.Items.Count == 3 && reloaded.Find("Открой проект") is null &&
              reloaded.Find("Нексус, Спасибо.")?.Target == "Мой голосовой ответ", "Saved commands and deletion survive reload");

        using (var voice = new AssistantViewModel(router, new SpeechStub(null), store))
        {
            await voice.InitializeAsync();
            var count = voice.Messages.Count;
            await voice.ListenAsync();
            Check(voice.Messages.Count == count && voice.Notice.Contains("Разборчивой речи") && voice.CanListen,
                "Empty voice transcript reports a retryable error");
        }
        using (var voice = new AssistantViewModel(router, new SpeechStub(null, waitForCancel: true), store))
        {
            await voice.InitializeAsync();
            var listening = voice.ListenAsync();
            Check(voice.State == Activity.Recognizing && voice.CanListen && voice.StatusDetail.Contains("Распознаю"),
                "Voice decoding has a distinct, cancellable state");
            await voice.ListenAsync();
            await listening;
            Check(voice.State == Activity.Idle && voice.CanListen && voice.Notice == "Голосовой ввод отменён.",
                "Cancel during decoding releases the microphone and reports cancellation");
        }
        using (var voice = new AssistantViewModel(router, new SpeechStub("яедекс музыка"), store))
        {
            await voice.InitializeAsync();
            var count = music.Actions.Count;
            await voice.ListenAsync();
            Check(music.Actions.Count == count + 1 && music.Actions[^1] == MusicAction.Open &&
                  voice.Messages[^2].Text == "яедекс музыка" && !voice.Messages[^1].IsUser,
                "Recognized speech with a typo executes the music action and shows a reply");
        }

        await CheckTelegramAsync(store, launch);
        if (args.Contains("--speech")) await CheckSpeechAsync();
        Console.WriteLine($"Passed {_passed} checks.");
    }

    private static void Check(bool result, string title)
    {
        if (!result) throw new InvalidOperationException($"FAIL {title}");
        _passed++;
        Console.WriteLine($"PASS {title}");
    }

    private static void RenderLayout(CommandRouter router, LocalStore store, CustomCommands custom, IWindowsActions windows)
    {
        var app = new App();
        app.InitializeComponent();
        NexusAssistant.Views.CosmicField.PrepareAsync().GetAwaiter().GetResult();
        using var model = new AssistantViewModel(router, new SpeechStub(), store, custom);
        model.InitializeAsync().GetAwaiter().GetResult();
        using var telegram = new TelegramControl(store, windows);
        var window = new MainWindow(model, telegram);
        var content = (FrameworkElement)window.Content;
        content.DataContext = model;
        foreach (var (page, width, height, name) in new[]
                 {
                     (Page.Home, 1120, 775, "layout-1120"),
                     (Page.Home, 1920, 1040, "layout-maximized"),
                     (Page.Home, 800, 650, "layout-800"),
                     (Page.Commands, 800, 650, "layout-commands"),
                     (Page.Remote, 800, 650, "layout-telegram"),
                     (Page.Settings, 800, 650, "layout-settings"),
                     (Page.Tools, 800, 650, "layout-pc-tools")
                 })
        {
            model.CurrentPage = page;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(".nexus-test", $"{name}.png"));
            encoder.Save(stream);
        }
        Check(content.ActualWidth == 800 && content.ActualHeight == 650, "WPF layout at maximized, standard and minimum dimensions");
    }

    private static async Task CheckTelegramAsync(LocalStore store, WindowsStub launch)
    {
        Check(TelegramControl.FindApp("Dota 2 .exe", ["dota2"]) == 0 &&
              TelegramControl.FindApp("dtoa2", ["Dota 2"]) == 0 &&
              TelegramControl.FindApp("dotx2", ["Dota 2", "Doti 2"]) == -2 &&
              TelegramControl.FindApp("d", ["Dota 2"]) == -1, "Telegram app matching tolerates typos and rejects ambiguous input");

        using var transport = new TelegramStub();
        using var bot = new TelegramControl(store, launch, transport);
        const string token = "123456:example_test_token_value";
        var executable = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        await bot.ConnectAsync(token, $"Dota 2 = {executable}");
        Check(bot.Running && bot.PairCode.Length == 8 && bot.BotName == "@nexus_test_bot", "Bot connects and generates an expiring pairing code");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        transport.Deliver(1, 100, $"/pair {bot.PairCode}", now, "group");
        transport.Deliver(2, 101, "/open dota2", now);
        transport.Deliver(3, 102, $"/pair {bot.PairCode}", now);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var paired = await transport.Responses.Reader.ReadAsync(timeout.Token);
        Check(paired.Chat == 102 && paired.Text.Contains("привязан") && bot.PairCode.Length == 0,
            "Only a valid private pairing message authorizes remote control");
        transport.Deliver(4, 101, "/open dota2", now);
        transport.Deliver(5, 102, "/open dota2", now - 300);
        transport.Deliver(6, 102, "/open dtoa2", now);
        var opened = await transport.Responses.Reader.ReadAsync(timeout.Token);
        Check(opened.Chat == 102 && opened.Text.Contains("Открываю") && launch.OpenedPath == executable &&
              !transport.Responses.Reader.TryRead(out _), "Only fresh commands from the owner can launch an allowed app");
        transport.Deliver(7, 102, "🚀 Приложения", now);
        var list = await transport.Responses.Reader.ReadAsync(timeout.Token);
        Check(list.Text.Contains("Dota 2") && list.Keyboard.Contains("▶ Dota 2") && list.Keyboard.Contains("❔ Помощь"),
            "Telegram application list includes named launch buttons and help");
        transport.Deliver(8, 102, "▶ Dota 2", now);
        Check((await transport.Responses.Reader.ReadAsync(timeout.Token)).Text.Contains("Открываю"), "Telegram named launch button executes the app");
        bot.Dispose();
        using var restartedTransport = new TelegramStub { FailFirstGetMe = true };
        using var restarted = new TelegramControl(store, launch, restartedTransport);
        await restarted.LoadAsync();
        await restarted.ResumeAsync();
        Check(restarted.Running && restarted.KeepRunning, "Enabled bot starts polling when Telegram is temporarily unavailable");
        restartedTransport.Deliver(9, 102, "/status", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var recovered = await restartedTransport.Responses.Reader.ReadAsync(recoveryTimeout.Token);
        Check(recovered.Chat == 102 && recovered.Text.Contains("на связи") && restarted.BotName == "@nexus_test_bot",
            "Restarted bot reconnects and retains the paired account");
        await restarted.StopAsync();
        var config = await File.ReadAllTextAsync(Path.Combine(store.DirectoryPath, "telegram.json"));
        Check(!restarted.KeepRunning && restarted.CanRevoke && !config.Contains(token), "Stopping retains the owner and stores the token encrypted");
        await restarted.RevokeAsync();
        Check(!restarted.CanRevoke, "Unpairing removes remote authorization");
    }

    private sealed class TelegramStub : HttpMessageHandler
    {
        public bool FailFirstGetMe { get; set; }
        private readonly System.Threading.Channels.Channel<object> _updates = System.Threading.Channels.Channel.CreateUnbounded<object>();
        public System.Threading.Channels.Channel<(long Chat, string Text, string Keyboard)> Responses { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(long, string, string)>();

        public void Deliver(int id, long chat, string text, long date, string type = "private") =>
            _updates.Writer.TryWrite(new { update_id = id, message = new { text, date, from = new { id = chat, is_bot = false }, chat = new { id = chat, type } } });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object result;
            switch (request.RequestUri!.Segments[^1])
            {
                case "getMe":
                    if (FailFirstGetMe)
                    {
                        FailFirstGetMe = false;
                        throw new HttpRequestException("Simulated network startup delay");
                    }
                    result = new { username = "nexus_test_bot" };
                    break;
                case "getUpdates": result = new[] { await _updates.Reader.ReadAsync(cancellationToken) }; break;
                case "sendMessage":
                    using (var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                        Responses.Writer.TryWrite((data.RootElement.GetProperty("chat_id").GetInt64(), data.RootElement.GetProperty("text").GetString()!,
                            string.Join(' ', data.RootElement.GetProperty("reply_markup").GetProperty("keyboard").EnumerateArray()
                                .SelectMany(row => row.EnumerateArray()).Select(button => button.GetString()))));
                    result = true;
                    break;
                default: throw new InvalidOperationException("Unexpected Telegram method.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result })) };
        }
    }

    private static async Task CheckSpeechAsync()
    {
        var host = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "Services", "SpeechHost.ps1");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", "speak" })
            start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync("Проверка синтеза речи.");
        process.StandardInput.Close();
        var result = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        if (result == "NO_VOICE") { Console.WriteLine("SKIP Russian synthesis: voice unavailable"); return; }
        var wave = Convert.FromBase64String(result);
        Check(process.ExitCode == 0 && wave.Length > 1000 && wave.AsSpan(0, 4).SequenceEqual("RIFF"u8), "Russian synthesis produced PCM wave");
    }

    private sealed class WindowsStub(WindowsActions actions) : IWindowsActions
    {
        public string App { get; private set; } = "";
        public string Query { get; private set; } = "";
        public string OpenedPath { get; private set; } = "";
        public Uri? Website { get; private set; }
        public void Open(string app) => App = app;
        public void Search(string query) => Query = query;
        public void OpenPath(string path) => OpenedPath = path;
        public void OpenWebsite(Uri address) => Website = address;
        public void CreateFolder(string name) => actions.CreateFolder(name);
        public void RenameFolder(string name, string replacement) => actions.RenameFolder(name, replacement);
    }

    private sealed class MusicStub : IMusicActions
    {
        public List<MusicAction> Actions { get; } = [];
        public Task<Reply> RunAsync(MusicAction action, CancellationToken token)
        {
            Actions.Add(action);
            return Task.FromResult(new Reply("OK"));
        }
    }

    private sealed class SpeechStub(string? transcript = "Спасибо", bool waitForCancel = false) : ISpeech
    {
        public async Task<string?> ListenAsync(Action ready, Action<double> level, Action decoding, CancellationToken token)
        {
            ready();
            level(.5);
            decoding();
            if (waitForCancel) await Task.Delay(Timeout.Infinite, token);
            return transcript;
        }
        public Task SpeakAsync(string text, Action<double> level, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class ChatHandler : HttpMessageHandler
    {
        public bool ValidRequest { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            ValidRequest = request.RequestUri?.AbsoluteUri == "http://127.0.0.1:11434/api/chat" &&
                           request.Method == HttpMethod.Post && root.GetProperty("model").GetString() == "test-model" &&
                           !root.GetProperty("stream").GetBoolean() && root.GetProperty("messages").GetArrayLength() == 3;
            return new HttpResponseMessage(Status) { Content = new StringContent("""{"message":{"content":"Тестовый ответ"}}""", Encoding.UTF8, "application/json") };
        }
    }
}

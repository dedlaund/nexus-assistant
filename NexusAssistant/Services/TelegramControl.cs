using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusAssistant.Services;

public sealed class TelegramControl : INotifyPropertyChanged, IDisposable
{
    private const string FileName = "telegram.json";
    private static readonly TimeSpan PairLifetime = TimeSpan.FromMinutes(5);
    private readonly LocalStore store;
    private readonly IWindowsActions windows;
    private readonly HttpClient _http;
    private Config _config = new();
    private CancellationTokenSource? _connection;
    private Task? _poll;
    private DateTimeOffset _pairExpires;
    private DateTimeOffset _lastPhoto;
    private RemoteDesktopActions.Target[] _windows = [], _tabs = [];
    private DateTimeOffset _windowListExpires, _tabListExpires;
    private bool _busy;

    public TelegramControl(LocalStore store, IWindowsActions windows)
        : this(store, windows, new HttpClientHandler { AllowAutoRedirect = false }) { }

    internal TelegramControl(LocalStore store, IWindowsActions windows, HttpMessageHandler handler)
    {
        this.store = store;
        this.windows = windows;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
    }

    private sealed record Config(string Secret = "", long Owner = 0, string Apps = "", bool Enabled = false);
    private sealed record AppEntry(string Label, string Path);
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Status { get; private set; } = "Бот выключен.";
    public string BotName { get; private set; } = "";
    public string PairCode { get; private set; } = "";
    public string PairInstruction => PairCode.Length == 0 ? "" : $"В личном чате отправьте /pair {PairCode} · код действует 5 минут";
    public string AppsText => _config.Apps;
    public bool Running => _connection is not null;
    public bool KeepRunning => _config.Enabled || Running;
    public bool CanConfigure => !_busy && !Running;
    public bool CanStop => !_busy && Running;
    public bool CanRevoke => CanConfigure && _config.Owner != 0;
    public bool CanPair => CanStop && _config.Owner == 0;
    public string PairStatus => _config.Owner == 0 ? "Телефон ещё не привязан." : "Telegram-аккаунт привязан.";

    public async Task LoadAsync()
    {
        try { _config = await store.ReadAsync(FileName, new Config()); }
        catch { Status = "Не удалось прочитать настройки Telegram."; }
        Changed();
    }

    public Task ResumeAsync()
    {
        if (!_config.Enabled || !CanConfigure) return Task.CompletedTask;
        try
        {
            var token = Unprotect(_config.Secret);
            StartPolling(token, ParseApps(_config.Apps, requireExisting: false));
        }
        catch (Exception error) when (error is IOException or CryptographicException or FormatException)
        {
            Status = "Не удалось восстановить подключение. Проверьте настройки бота и подключите его снова.";
            Changed();
        }
        return Task.CompletedTask;
    }

    public async Task ConnectAsync(string tokenInput, string apps)
    {
        if (!CanConfigure) return;
        _busy = true;
        Status = "Проверяю подключение к Telegram…";
        Changed();
        try
        {
            var appList = ParseApps(apps);
            var token = tokenInput.Trim();
            var oldToken = "";
            if (_config.Secret.Length != 0)
            {
                try { oldToken = Unprotect(_config.Secret); }
                catch (Exception error) when (token.Length > 0 && error is CryptographicException or FormatException) { }
            }
            if (token.Length == 0) token = oldToken;
            if (!Regex.IsMatch(token, @"\A\d{5,20}:[A-Za-z0-9_-]{20,100}\z"))
                throw new IOException("Вставьте токен Telegram-бота, полученный у BotFather.");

            var result = await ApiAsync(token, "getMe", new { }, CancellationToken.None);
            var username = result.GetProperty("username").GetString();
            var next = new Config(Protect(token), token == oldToken ? _config.Owner : 0, apps, true);
            await store.WriteAsync(FileName, next);
            _config = next;
            BotName = $"@{username}";
            StartPolling(token, appList);
        }
        catch (IOException error) { Status = error.Message; }
        catch (CryptographicException) { Status = "Windows не смогла защитить токен. Запустите NEXUS в своём обычном сеансе Windows."; }
        catch { Status = "Не удалось подключиться. Проверьте токен и доступ к Telegram."; }
        finally { _busy = false; Changed(); }
    }

    private void StartPolling(string token, AppEntry[] apps)
    {
        Status = BotName.Length == 0 ? "Восстанавливаю связь с Telegram…" : "Бот подключён.";
        _connection = new CancellationTokenSource();
        if (_config.Owner == 0) IssueCode();
        _poll = PollAsync(token, apps, _connection.Token);
        Changed();
    }

    public void NewPairCode()
    {
        if (CanPair) IssueCode();
    }

    public async Task StopAsync()
    {
        if (!CanStop) return;
        _busy = true;
        Changed();
        var connection = _connection!;
        connection.Cancel();
        try
        {
            if (_poll is not null) await _poll;
            var next = _config with { Enabled = false };
            await store.WriteAsync(FileName, next);
            _config = next;
            Status = "Бот выключен.";
        }
        catch { Status = "Бот остановлен. Не удалось сохранить настройку выключения."; }
        finally
        {
            _connection = null;
            _poll = null;
            PairCode = "";
            connection.Dispose();
            _busy = false;
            Changed();
        }
    }

    public async Task RevokeAsync()
    {
        if (!CanRevoke) return;
        _busy = true;
        Changed();
        try
        {
            var next = _config with { Owner = 0, Enabled = false };
            await store.WriteAsync(FileName, next);
            _config = next;
            Status = "Привязка удалена. При следующем подключении будет новый код.";
        }
        catch { Status = "Не удалось удалить привязку."; }
        finally { _busy = false; Changed(); }
    }

    private void IssueCode()
    {
        PairCode = RandomNumberGenerator.GetInt32(10000000, 100000000).ToString(CultureInfo.InvariantCulture);
        _pairExpires = DateTimeOffset.UtcNow + PairLifetime;
        Changed();
    }

    private async Task PollAsync(string token, AppEntry[] apps, CancellationToken cancel)
    {
        var connectedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long offset = 0;
        var failures = 0;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (BotName.Length == 0)
                {
                    var bot = await ApiAsync(token, "getMe", new { }, cancel);
                    BotName = $"@{bot.GetProperty("username").GetString()}";
                    Status = "Бот подключён.";
                    Changed();
                }
                var updates = await ApiAsync(token, "getUpdates", new
                {
                    offset, timeout = 25, limit = 30, allowed_updates = new[] { "message" }
                }, cancel);
                if (failures != 0) { failures = 0; Status = "Бот подключён."; Changed(); }
                foreach (var update in updates.EnumerateArray())
                {
                    cancel.ThrowIfCancellationRequested();
                    offset = update.GetProperty("update_id").GetInt64() + 1;
                    if (!TryReadPrivateMessage(update, connectedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var chat, out var text)) continue;
                    if (_config.Owner == 0)
                    {
                        if (PairCode.Length == 0 || DateTimeOffset.UtcNow > _pairExpires ||
                            !string.Equals(text, "/pair " + PairCode, StringComparison.Ordinal)) continue;
                        var next = _config with { Owner = chat };
                        await store.WriteAsync(FileName, next);
                        _config = next;
                        PairCode = "";
                        Changed();
                        await SendAsync(token, chat, "Телефон привязан. Теперь доступны команды управления ПК.", cancel);
                        continue;
                    }
                    if (chat != _config.Owner) continue;
                    await HandleAsync(token, chat, text, apps, cancel);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                Status = error is TelegramApiException { Code: 409 }
                    ? "Telegram сообщил о конфликте. У этого бота есть другой запущенный клиент или вебхук."
                    : "Связь с Telegram прервалась. Повторяю подключение…";
                Changed();
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 3 << Math.Min(failures++, 5))), cancel); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal static bool TryReadPrivateMessage(JsonElement update, long connectedAt, long now, out long chat, out string text)
    {
        chat = 0;
        text = "";
        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var body) ||
            !message.TryGetProperty("from", out var sender) || !message.TryGetProperty("chat", out var room) ||
            room.GetProperty("type").GetString() != "private" || !message.TryGetProperty("date", out var date)) return false;
        var sent = date.GetInt64();
        if (sent < connectedAt || now - sent > 90 || sent > now + 60) return false;
        chat = room.GetProperty("id").GetInt64();
        if (chat <= 0 || chat != sender.GetProperty("id").GetInt64() || sender.GetProperty("is_bot").GetBoolean()) return false;
        text = body.GetString()?.Trim() ?? "";
        return text.Length is > 0 and <= 200;
    }

    private async Task HandleAsync(string token, long chat, string text, AppEntry[] apps, CancellationToken cancel)
    {
        text = text.ToLowerInvariant() switch
        {
            "снимок экрана" or "📷 экран" => "/screen",
            "процессы цп" or "📊 процессы" => "/cpu",
            "статус" or "🟢 статус" => "/status",
            "приложения" or "🚀 приложения" => "/apps",
            "окна" or "🪟 окна" => "/windows",
            "вкладки" or "🌐 вкладки" => "/tabs",
            "❔ помощь" => "/help",
            _ => text
        };
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].Split('@', 2)[0].ToLowerInvariant();
        var value = parts.Length > 1 ? parts[1] : "";
        string result;
        string[] choices = [];
        try
        {
            switch (command)
            {
                case "/start" or "/help":
                    result = "Управление ПК\n\n📷 Экран — снимок основного монитора\n📊 Процессы — текущая нагрузка ЦП\n🚀 Приложения — запуск по кнопке или имени\n🪟 Окна и 🌐 Вкладки — выберите номер под списком\n🟢 Статус — проверка связи с ПК";
                    break;
                case "/screen":
                    if (DateTimeOffset.UtcNow - _lastPhoto < TimeSpan.FromSeconds(5))
                    {
                        result = "Подождите 5 секунд между снимками.";
                        break;
                    }
                    _lastPhoto = DateTimeOffset.UtcNow;
                    var photo = await Task.Run(RemoteDesktopActions.Screenshot, cancel);
                    await SendPhotoAsync(token, chat, photo, cancel);
                    return;
                case "/cpu":
                    result = await Task.Run(() => RemoteDesktopActions.CpuAsync(cancel), cancel);
                    break;
                case "/status":
                    result = $"{Environment.MachineName} на связи · {Environment.ProcessorCount} логических ЦП · {DateTime.Now:HH:mm}";
                    break;
                case "/apps":
                    choices = apps.Select(app => $"▶ {app.Label}").ToArray();
                    result = apps.Length == 0 ? "Добавьте разрешённые приложения в настройках NEXUS." :
                        "🚀 Приложения\nВыберите кнопку или отправьте название.\n\n" + string.Join('\n', apps.Select(app => $"• {app.Label}"));
                    break;
                case "/windows" or "/tabs":
                    var browser = command == "/tabs";
                    var targets = await Task.Run(() => RemoteDesktopActions.List(browser, cancel), cancel);
                    if (browser) { _tabs = targets; _tabListExpires = DateTimeOffset.UtcNow.AddMinutes(2); }
                    else { _windows = targets; _windowListExpires = DateTimeOffset.UtcNow.AddMinutes(2); }
                    var prefix = browser ? "/tab" : "/switch";
                    choices = Enumerable.Range(1, targets.Length).Select(index => $"{(browser ? "🌐" : "🪟")} {index}").ToArray();
                    result = targets.Length == 0 ? "Подходящие окна или вкладки не найдены." :
                        $"{(browser ? "🌐 Вкладки браузера" : "🪟 Открытые окна")}\nВыберите номер. Список действует 2 минуты.\n\n" +
                        string.Join('\n', targets.Select((target, index) => $"{index + 1}. {ShortTitle(target.Title)}")) +
                        (targets.Length > 12 ? $"\n\nДля номеров с 13 используйте {prefix} номер." : "");
                    break;
                case "/switch" or "/tab" or "🌐" or "🪟":
                    var isTab = command is "/tab" or "🌐";
                    var list = isTab ? _tabs : _windows;
                    var expires = isTab ? _tabListExpires : _windowListExpires;
                    result = DateTimeOffset.UtcNow > expires || !int.TryParse(value, out var number) || number < 1 || number > list.Length
                        ? "Сначала запросите свежий список и выберите номер."
                        : await Task.Run(() => RemoteDesktopActions.Switch(list[number - 1]), cancel);
                    break;
                default:
                    var query = command is "/open" or "▶" ? value : text.StartsWith("открой ", StringComparison.OrdinalIgnoreCase) ? text[7..] : text;
                    var index = FindApp(query, apps.Select(app => app.Label).ToArray());
                    if (index < 0) result = index == -2 ? "Подходят несколько приложений. Уточните название из /apps." : "Приложение не найдено. Проверьте /apps.";
                    else
                    {
                        var app = apps[index];
                        await Task.Run(() =>
                        {
                            if (!File.Exists(app.Path)) throw new IOException("Файл приложения перемещён.");
                            windows.OpenPath(app.Path);
                        }, cancel);
                        result = $"Открываю «{app.Label}».";
                    }
                    break;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (TelegramApiException) { throw; }
        catch (IOException error) { result = error.Message; }
        catch { result = "Windows не смогла выполнить действие. Проверьте, что сеанс разблокирован, а окно или приложение доступно."; }
        await SendAsync(token, chat, result, cancel, choices);
    }

    private static string ShortTitle(string title) =>
        title.Length <= 95 ? title.Replace('\n', ' ').Replace('\r', ' ') : title[..94].Replace('\n', ' ').Replace('\r', ' ') + "…";

    internal static int FindApp(string query, IReadOnlyList<string> labels)
    {
        var normalized = NormalizeApp(query);
        if (normalized.Length == 0) return -1;
        var names = labels.Select(NormalizeApp).ToArray();
        var exact = Array.IndexOf(names, normalized);
        if (exact >= 0) return exact;
        if (normalized.Length < 4) return -1;
        var matches = names.Select((name, index) => (name, index))
            .Where(item => item.name.Length >= 4 && CommandRouter.OneEditFrom(normalized, item.name)).Take(2).ToArray();
        return matches.Length switch { 0 => -1, 1 => matches[0].index, _ => -2 };
    }

    private static string NormalizeApp(string text) => string.Concat(text.Trim().Replace("ё", "е", StringComparison.OrdinalIgnoreCase)
        .Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant().Where(char.IsLetterOrDigit));

    private static AppEntry[] ParseApps(string value, bool requireExisting = true)
    {
        var apps = new List<AppEntry>();
        var names = new HashSet<string>();
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (fields.Length != 2 || fields[0].Length is < 2 or > 48 || NormalizeApp(fields[0]).Length < 2 ||
                fields[0].StartsWith('/') || !names.Add(NormalizeApp(fields[0])) || apps.Count >= 30)
                throw new IOException("Укажите до 30 уникальных приложений, по одному «название = путь» на строку.");
            var path = Environment.ExpandEnvironmentVariables(fields[1].Trim('"'));
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || (requireExisting && !File.Exists(path)))
                throw new IOException($"Для «{fields[0]}» укажите существующий локальный .exe.");
            apps.Add(new(fields[0], path));
        }
        return apps.ToArray();
    }

    private async Task SendAsync(string token, long chat, string text, CancellationToken cancel, string[]? choices = null)
    {
        string[][] menu = [["📷 Экран", "📊 Процессы", "🟢 Статус"], ["🚀 Приложения", "🪟 Окна", "🌐 Вкладки"], ["❔ Помощь"]];
        await ApiAsync(token, "sendMessage", new
        {
            chat_id = chat, text, protect_content = true,
            reply_markup = new { keyboard = (choices ?? []).Take(12).Chunk(3).Concat(menu).ToArray(), resize_keyboard = true }
        }, cancel);
    }

    private async Task SendPhotoAsync(string token, long chat, byte[] bytes, CancellationToken cancel)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chat.ToString(CultureInfo.InvariantCulture)), "chat_id");
        form.Add(new StringContent("true"), "protect_content");
        form.Add(new ByteArrayContent(bytes), "photo", "screen.jpg");
        await RequestAsync(token, "sendPhoto", form, cancel);
    }

    private async Task<JsonElement> ApiAsync(string token, string method, object body, CancellationToken cancel)
    {
        using var content = JsonContent.Create(body);
        return await RequestAsync(token, method, content, cancel);
    }

    private async Task<JsonElement> RequestAsync(string token, string method, HttpContent content, CancellationToken cancel)
    {
        using var response = await _http.PostAsync($"https://api.telegram.org/bot{token}/{method}", content, cancel).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode || !document.RootElement.GetProperty("ok").GetBoolean())
            throw new TelegramApiException((int)response.StatusCode);
        return document.RootElement.GetProperty("result").Clone();
    }

    private sealed class TelegramApiException(int code) : Exception { public int Code { get; } = code; }
    private static string Protect(string token) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    private static string Unprotect(string secret) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser));
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
    public void Dispose() { _connection?.Cancel(); _http.Dispose(); }
}

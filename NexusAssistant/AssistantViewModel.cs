using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NexusAssistant.Services;

namespace NexusAssistant;

public enum Page { Home, Chat, Commands, History, Settings, Remote, Tools }

public sealed class AssistantViewModel(CommandRouter router, ISpeech speech, LocalStore store, CustomCommands? custom = null,
    Action<PreferredBrowser>? applyBrowser = null) : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _listening;
    private bool _working;
    private bool _initialized;
    private bool _historyReadable = true;
    private string _input = "";
    private string _notice = "";
    private Activity _activity;
    private double _audioLevel;
    private Page _page;
    private AssistantSettings _settings = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? MessageAdded;
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public IReadOnlyList<CommandExample> Examples => CommandRouter.Examples;
    public IReadOnlyList<CustomCommand> UserCommands => custom?.Items ?? [];
    public bool CanEditCommands => CanEdit && custom?.Ready == true;
    public string DataLocation => store.DirectoryPath;
    public string Input
    {
        get => _input;
        set
        {
            var couldSend = CanSend;
            var hadInput = HasInput;
            if (!Set(ref _input, value)) return;
            if (HasInput != hadInput) Changed(nameof(HasInput));
            if (CanSend != couldSend) Changed(nameof(CanSend));
        }
    }
    public bool HasInput => !string.IsNullOrEmpty(Input);
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool Empty => Messages.Count == 0;
    public string LatestReply => Messages.LastOrDefault(message => !message.IsUser)?.Text ?? "";
    public bool CanSend => _initialized && !_working && !string.IsNullOrWhiteSpace(Input);
    public bool CanListen => _initialized && (!_working || _listening is not null);
    public bool CanEdit => _initialized && !_working;
    public bool CanClear => CanEdit && (!Empty || !_historyReadable);
    public double AudioLevel { get => _audioLevel; private set => Set(ref _audioLevel, value); }
    public Activity State
    {
        get => _activity;
        private set
        {
            if (!Set(ref _activity, value)) return;
            Changed(nameof(Status));
            Changed(nameof(StatusDetail));
            Changed(nameof(Listening));
        }
    }
    public bool Listening => State is Activity.Listening or Activity.Recognizing;
    public string Status => State switch
    {
        Activity.Listening => "СЛУШАЮ",
        Activity.Recognizing => "РАСПОЗНАВАНИЕ",
        Activity.Processing => "ОБРАБОТКА",
        Activity.Speaking => "ГОВОРЮ",
        Activity.Error => "ВНИМАНИЕ",
        _ => "ГОТОВ"
    };
    public string StatusDetail => State switch
    {
        Activity.Listening => "Произнесите команду",
        Activity.Recognizing => "Распознаю запись…",
        Activity.Processing => "Обрабатываю запрос",
        Activity.Speaking => "Озвучиваю ответ",
        Activity.Error => "Подробности в сообщении",
        _ => "Ожидаю команду"
    };
    public Page CurrentPage
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Changed(nameof(ShowHome));
            Changed(nameof(ShowConversation));
            Changed(nameof(ShowCommands));
            Changed(nameof(ShowSettings));
            Changed(nameof(ShowRemote));
            Changed(nameof(ShowTools));
            Changed(nameof(ShowComposer));
            Changed(nameof(ShowHistory));
            Changed(nameof(PageTitle));
        }
    }
    public bool ShowHome => CurrentPage == Page.Home;
    public bool ShowConversation => CurrentPage is Page.Chat or Page.History;
    public bool ShowComposer => CurrentPage is Page.Home or Page.Chat;
    public bool ShowCommands => CurrentPage == Page.Commands;
    public bool ShowSettings => CurrentPage == Page.Settings;
    public bool ShowRemote => CurrentPage == Page.Remote;
    public bool ShowTools => CurrentPage == Page.Tools;
    public bool ShowHistory => CurrentPage == Page.History;
    public string PageTitle => CurrentPage switch
    {
        Page.History => "История",
        Page.Commands => "Команды",
        Page.Settings => "Настройки",
        _ => "Диалог"
    };
    public bool Intro { get => _settings.Intro; set { _settings = _settings with { Intro = value }; Changed(); } }
    public bool Speak { get => _settings.Speak; set { _settings = _settings with { Speak = value }; Changed(); } }
    public bool ClickSounds { get => _settings.ClickSounds; set { _settings = _settings with { ClickSounds = value }; Changed(); } }
    public PreferredBrowser Browser { get => _settings.Browser; set { _settings = _settings with { Browser = value }; Changed(); } }
    public sealed record BrowserOption(PreferredBrowser Value, string Label);
    public IReadOnlyList<BrowserOption> BrowserOptions { get; } =
    [
        new(PreferredBrowser.Firefox, "Firefox"), new(PreferredBrowser.Chrome, "Google Chrome"),
        new(PreferredBrowser.Edge, "Microsoft Edge"), new(PreferredBrowser.Brave, "Brave"),
        new(PreferredBrowser.SystemDefault, "По умолчанию в Windows")
    ];
    public string ProfileName { get => _settings.ProfileName; set { _settings = _settings with { ProfileName = value }; Changed(); Changed(nameof(Greeting)); Changed(nameof(ProfileInitial)); } }
    public string Greeting => string.IsNullOrWhiteSpace(ProfileName) ? "Я на связи" : $"{ProfileName.Trim()}, я на связи";
    public string ProfileInitial => string.IsNullOrWhiteSpace(ProfileName) ? "N" :
        System.Globalization.StringInfo.GetNextTextElement(ProfileName.Trim()).ToUpperInvariant();
    public string Model { get => _settings.Model; set { _settings = _settings with { Model = value }; Changed(); } }

    public async Task InitializeAsync()
    {
        try
        {
            _settings = await store.ReadAsync("settings.json", new AssistantSettings());
            if (_settings.Model is null) _settings = _settings with { Model = "" };
            if (!Enum.IsDefined(_settings.Browser)) _settings = _settings with { Browser = PreferredBrowser.Firefox };
            applyBrowser?.Invoke(_settings.Browser);
        }
        catch (Exception error) { Notice = $"Не удалось прочитать настройки. {error.Message}"; }

        try
        {
            foreach (var message in await store.ReadAsync("history.json", Array.Empty<ChatMessage>()))
                if (message is { Text: not null }) Messages.Add(message);
        }
        catch (Exception error)
        {
            _historyReadable = false;
            Notice = $"Не удалось прочитать историю. {error.Message}";
        }
        if (custom is not null)
        {
            try { await custom.LoadAsync(); }
            catch (Exception error) { Notice = $"Не удалось прочитать свои команды. {error.Message}"; }
            Changed(nameof(UserCommands));
        }
        Changed(nameof(Intro));
        Changed(nameof(Speak));
        Changed(nameof(Browser));
        Changed(nameof(ClickSounds));
        Changed(nameof(ProfileName));
        Changed(nameof(ProfileInitial));
        Changed(nameof(Greeting));
        Changed(nameof(Model));
        Changed(nameof(Empty));
        Changed(nameof(LatestReply));
        _initialized = true;
        RefreshAvailability();
    }

    public async Task SendAsync()
    {
        if (!CanSend) return;
        var text = Input.Trim();
        await RunAsync(async () =>
        {
            Input = "";
            await AnswerAsync(text);
        });
    }

    internal async Task<string> SendQuickAsync(string text)
    {
        if (!CanEdit) return "NEXUS занят. Повторите запрос через несколько секунд.";
        if (string.IsNullOrWhiteSpace(text)) return "";
        var previous = Messages.Count;
        await RunAsync(() => AnswerAsync(text.Trim()));
        return Messages.Count > previous + 1 ? LatestReply : Notice;
    }

    public async Task ListenAsync()
    {
        if (_listening is not null)
        {
            _listening.Cancel();
            return;
        }
        if (!CanListen) return;
        await RunAsync(async () =>
        {
            using var listening = new CancellationTokenSource();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, listening.Token);
            _listening = listening;
            State = Activity.Listening;
            Notice = "Подключаю микрофон…";
            RefreshAvailability();
            string? text;
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(22));
                text = await speech.ListenAsync(() => Notice = "", level => AudioLevel = level, () =>
                {
                    State = Activity.Recognizing;
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                }, deadline.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                Notice = listening.IsCancellationRequested ? "Голосовой ввод отменён."
                    : State == Activity.Recognizing ? "Whisper не успел распознать запись. Попробуйте более короткую фразу."
                    : "Запись с микрофона не завершилась. Проверьте устройство ввода Windows.";
                return;
            }
            finally
            {
                _listening = null;
                AudioLevel = 0;
                RefreshAvailability();
            }
            if (!string.IsNullOrWhiteSpace(text)) await AnswerAsync(text);
            else Notice = "Разборчивой речи не обнаружено. Проверьте микрофон Windows и повторите фразу.";
        });
    }

    public Task SaveSettingsAsync() => RunAsync(async () =>
    {
        await store.WriteAsync("settings.json", _settings);
        applyBrowser?.Invoke(_settings.Browser);
        Notice = "Настройки сохранены.";
    });

    public async Task<bool> SaveCommandAsync(Guid? id, string phrase, CustomAction action, string target)
    {
        if (!CanEditCommands || custom is null) return false;
        var saved = false;
        await RunAsync(async () =>
        {
            await custom.SaveAsync(id, phrase, action, target);
            Changed(nameof(UserCommands));
            Notice = id is null ? "Команда добавлена." : "Изменения сохранены.";
            saved = true;
        });
        return saved;
    }

    public Task DeleteCommandAsync(Guid id)
    {
        if (!CanEditCommands || custom is null) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            await custom.RemoveAsync(id);
            Changed(nameof(UserCommands));
            Notice = "Команда удалена.";
        });
    }

    public Task ClearHistoryAsync() => RunAsync(async () =>
    {
        await store.WriteAsync("history.json", Array.Empty<ChatMessage>());
        _historyReadable = true;
        Messages.Clear();
        router.Reset();
        Changed(nameof(Empty));
        Changed(nameof(LatestReply));
        Changed(nameof(CanClear));
        Notice = "История очищена.";
    });

    private async Task AnswerAsync(string text)
    {
        State = Activity.Processing;
        var history = Messages.ToArray();
        Add(new(text, true, DateTimeOffset.Now));
        Reply reply;
        try { reply = await router.RouteAsync(text, Model, history, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { reply = new("Локальный сервис не ответил вовремя.", true); }
        catch (Exception error) { reply = new($"Не удалось обработать запрос. {error.Message}", true); }
        Add(new(reply.Text, false, DateTimeOffset.Now));
        if (_historyReadable)
        {
            try { await store.WriteAsync("history.json", Messages.ToArray()); }
            catch (Exception error) { Notice = $"Не удалось сохранить историю. {error.Message}"; }
        }
        else
        {
            Notice = "Сохранение истории приостановлено. Раздел «История» позволяет очистить повреждённый файл.";
        }

        if (reply.Error) await ShowErrorAsync();
        else if (Speak)
        {
            State = Activity.Speaking;
            await speech.SpeakAsync(reply.Text, level => AudioLevel = level, _lifetime.Token);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!CanEdit) return;
        _working = true;
        Notice = "";
        RefreshAvailability();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Notice = error.Message;
            await ShowErrorAsync();
        }
        finally
        {
            AudioLevel = 0;
            State = Activity.Idle;
            _working = false;
            RefreshAvailability();
        }
    }

    private async Task ShowErrorAsync()
    {
        State = Activity.Error;
        try { await Task.Delay(1100, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private void Add(ChatMessage message)
    {
        Messages.Add(message);
        Changed(nameof(Empty));
        if (!message.IsUser) Changed(nameof(LatestReply));
        Changed(nameof(CanClear));
        MessageAdded?.Invoke();
    }

    private void RefreshAvailability()
    {
        Changed(nameof(CanSend));
        Changed(nameof(CanListen));
        Changed(nameof(CanEdit));
        Changed(nameof(CanEditCommands));
        Changed(nameof(CanClear));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

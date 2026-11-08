using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace NexusAssistant.Services;

public enum CustomAction { OpenPath, OpenWebsite, Reply, Search }

public sealed record CustomCommand(Guid Id, string Phrase, CustomAction Action, string Target)
{
    [JsonIgnore] public string ActionLabel => CustomCommands.Options.First(option => option.Action == Action).Label;
}

public sealed record CustomActionOption(CustomAction Action, string Label, string TargetLabel, string Hint);

public sealed class CustomCommands(LocalStore store, IWindowsActions windows)
{
    private const string FileName = "commands.json";
    private static readonly StringComparer PhraseComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex WakeWord = new(@"^(?:(?:нексус|nexus)\b[\s,!:—-]*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<CustomActionOption> Options { get; } =
    [
        new(CustomAction.OpenPath, "Открыть файл, папку или приложение", "Путь на компьютере", "Например, C:\\Работа\\Проект или путь к файлу .exe."),
        new(CustomAction.OpenWebsite, "Открыть сайт в браузере", "Адрес сайта", "Введите полный адрес, например https://example.org."),
        new(CustomAction.Reply, "Ответить заданным текстом", "Текст ответа", "Например, «Привет! Рад тебя видеть»."),
        new(CustomAction.Search, "Искать в интернете", "Поисковый запрос", "Например, «свежие новости космонавтики».")
    ];

    public IReadOnlyList<CustomCommand> Items { get; private set; } = [];
    public bool Ready { get; private set; }

    public async Task LoadAsync()
    {
        var loaded = await store.ReadAsync(FileName, Array.Empty<CustomCommand>());
        var phrases = new HashSet<string>(PhraseComparer);
        var ids = new HashSet<Guid>();
        for (var i = 0; i < loaded.Length; i++)
        {
            var item = loaded[i];
            var phrase = item?.Phrase is { } text ? Normalize(text) : "";
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) ||
                phrase.Length is 0 or > 100 || IsCancel(phrase) || !phrases.Add(phrase) ||
                string.IsNullOrWhiteSpace(item.Target) || !Enum.IsDefined(item.Action))
                throw new IOException("В файле своих команд есть повреждённые или повторяющиеся записи.");
            loaded[i] = item with { Phrase = phrase };
        }
        Items = loaded;
        Ready = true;
    }

    public async Task SaveAsync(Guid? id, string phrase, CustomAction action, string target)
    {
        EnsureReady();
        phrase = Normalize(phrase);
        if (phrase.Length is 0 or > 100) throw new IOException("Введите фразу длиной от 1 до 100 символов.");
        if (IsCancel(phrase)) throw new IOException("Эта фраза зарезервирована для отмены команды.");
        if (id is not null && !Items.Any(item => item.Id == id))
            throw new IOException("Эта команда уже удалена.");
        if (Items.Any(item => item.Id != id && PhraseComparer.Equals(item.Phrase, phrase)))
            throw new IOException("Такая фраза уже есть. Выберите её для редактирования или введите другую.");

        var command = new CustomCommand(id ?? Guid.NewGuid(), phrase, action, ValidateTarget(action, target));
        var updated = id is null ? Items.Append(command).ToArray()
            : Items.Select(item => item.Id == id ? command : item).ToArray();
        await store.WriteAsync(FileName, updated);
        Items = updated;
    }

    public async Task RemoveAsync(Guid id)
    {
        EnsureReady();
        var updated = Items.Where(item => item.Id != id).ToArray();
        await store.WriteAsync(FileName, updated);
        Items = updated;
    }

    public CustomCommand? Find(string phrase)
    {
        var normalized = Normalize(phrase);
        return Items.FirstOrDefault(item => PhraseComparer.Equals(item.Phrase, normalized));
    }

    public Reply Execute(CustomCommand command)
    {
        var target = ValidateTarget(command.Action, command.Target);
        switch (command.Action)
        {
            case CustomAction.OpenPath:
                windows.OpenPath(target);
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
                return new($"Открываю «{(name.Length == 0 ? target : name)}».");
            case CustomAction.OpenWebsite:
                var address = new Uri(target);
                windows.OpenWebsite(address);
                return new($"Открываю {address.IdnHost}.");
            case CustomAction.Search:
                windows.Search(target);
                return new($"Ищу «{target}».");
            default:
                return new(target);
        }
    }

    public static string StripInvocation(string text) =>
        WakeWord.Replace(text.Trim(), "").Trim().TrimEnd('.', '!', '?').Trim();

    private static string Normalize(string text) => Whitespace.Replace(StripInvocation(text), " ");

    public static bool IsCancel(string text) =>
        PhraseComparer.Equals(text, "отмена") || PhraseComparer.Equals(text, "отмени") || PhraseComparer.Equals(text, "не надо");

    private void EnsureReady()
    {
        if (!Ready) throw new IOException("Свои команды недоступны. Проверьте файл commands.json в каталоге данных.");
    }

    private static string ValidateTarget(CustomAction action, string value)
    {
        value = value.Trim();
        if (value.Length is 0 or > 2000) throw new IOException("Заполните поле действия. Допустимо до 2000 символов.");
        switch (action)
        {
            case CustomAction.OpenPath:
                value = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                if (!Path.IsPathFullyQualified(value) ||
                    value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal))
                    throw new IOException("Укажите полный путь к файлу или папке.");
                if (!File.Exists(value) && !Directory.Exists(value))
                    throw new IOException("Файл или папка по этому пути не найдены.");
                return Path.GetFullPath(value);
            case CustomAction.OpenWebsite:
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0)
                    throw new IOException("Введите адрес сайта, который начинается с https:// или http://.");
                return uri.AbsoluteUri;
            case CustomAction.Reply:
            case CustomAction.Search:
                return value;
            default:
                throw new InvalidEnumArgumentException("Выберите действие из списка.");
        }
    }
}

using System.ComponentModel;
using System.Text.RegularExpressions;

namespace NexusAssistant.Services;

public sealed class CommandRouter(IWindowsActions windows, IMusicActions music, ILocalChat chat, CustomCommands? custom = null)
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Open = @"\b(открой|открыть|запусти|запустить|включи|включить|покажи)\b";
    private static readonly (string Pattern, string Id, string Name)[] Apps =
    [
        (@"\b(firefox|фа[йе]рфокс|файр фокс|фокс|мозилл\w*)\b", "firefox", "Firefox"),
        (@"\b(chrome|хром)\b", "chrome", "Chrome"),
        (@"\b(edge|эдж)\b", "edge", "Microsoft Edge"),
        (@"\b(brave|брейв)\b", "brave", "Brave"),
        (@"\bбраузер\b", "browser", "браузер"),
        (@"\b(roblox\s+studio|роблокс\s+студи[ояю])\b", "roblox", "Roblox Studio"),
        (@"\b(настройк\w+|параметр\w+)\s+звук\w*\b", "sound", "настройки звука"),
        (@"\b(настройк\w+|параметр\w+)\s+(экран\w*|диспле\w*)\b", "display", "настройки экрана"),
        (@"\b(настройк[аиу]|параметр[ыов]+)\b", "settings", "параметры Windows"),
        (@"\bпроводник\b", "explorer", "Проводник"),
        (@"\bблокнот\b", "notepad", "Блокнот"),
        (@"\bкалькулятор\b", "calculator", "Калькулятор"),
        (@"\b(paint|пэйнт|пейнт)\b", "paint", "Paint"),
        (@"\bдиспетчер\s+задач\b", "tasks", "Диспетчер задач"),
        (@"\b(папк[уаи]\s+)?загрузки\b", "downloads", "«Загрузки»"),
        (@"\b(мои\s+)?документы\b", "documents", "«Документы»"),
        (@"\bрабочий\s+стол\b", "desktop", "рабочий стол"),
        (@"\b(этот|мой)\s+компьютер\b", "computer", "«Этот компьютер»"),
        (@"\b(steam|стим)\b", "Steam", "Steam")
    ];

    public static IReadOnlyList<CommandExample> Examples { get; } =
    [
        new("Запуск приложения", "Открой Firefox", "WINDOWS"),
        new("Студия Roblox", "Запусти Roblox Studio", "WINDOWS"),
        new("Параметры системы", "Открой настройки Windows", "WINDOWS"),
        new("Диспетчер задач", "Открой диспетчер задач", "WINDOWS"),
        new("Настройки звука", "Покажи настройки звука", "WINDOWS"),
        new("Настройки экрана", "Открой параметры дисплея", "WINDOWS"),
        new("Paint", "Запусти Paint", "WINDOWS"),
        new("Загрузки", "Открой загрузки", "ФАЙЛЫ"),
        new("Документы", "Открой мои документы", "ФАЙЛЫ"),
        new("Новая папка", "Создай папку «Проект» на рабочем столе", "ФАЙЛЫ"),
        new("Переименование", "Переименуй папку «Проект» в «Архив»", "ФАЙЛЫ"),
        new("Яндекс Музыка", "Включи Яндекс Музыку", "МУЗЫКА"),
        new("Следующий трек", "Переключи песню", "МУЗЫКА"),
        new("Избранное", "Поставь лайк", "МУЗЫКА"),
        new("Коллекция", "Открой мои плейлисты", "МУЗЫКА"),
        new("Предыдущий трек", "Верни предыдущий трек", "МУЗЫКА"),
        new("Поиск в браузере", "Найди в интернете фотографии Сатурна", "ИНТЕРНЕТ"),
        new("ВКонтакте", "Открой ВК", "ИНТЕРНЕТ"),
        new("Дата и время", "Который час?", "СПРАВКА")
    ];

    private enum FollowUp { None, Create, RenameSource, RenameTarget }
    private FollowUp _pending;
    private string _source = "";

    public void Reset()
    {
        _pending = FollowUp.None;
        _source = "";
    }

    public async Task<Reply> RouteAsync(string input, string model, IReadOnlyList<ChatMessage> history, CancellationToken token)
    {
        var text = CustomCommands.StripInvocation(input);
        if (text.Length == 0) return new("Я слушаю. Что нужно сделать?");
        if (CustomCommands.IsCancel(text))
        {
            Reset();
            return new("Текущая команда отменена.");
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (custom?.Find(input) is { } command)
            {
                Reset();
                return await Task.Run(() => custom.Execute(command), token);
            }
            if (Is(text, @"^(как|зачем|почему|что будет|расскажи|объясни|не нужно|не надо)\b") ||
                Is(text, @"\bкак\s+(?:мне\s+)?(?:создать|переименовать|открыть|запустить|включить)\b|\bне\s+(?:открывай|открой|включай|включи|создавай|переименовывай|запускай|запусти)\b"))
                return await chat.AnswerAsync(text, model, history, token);

            var search = Regex.Match(text, @"^(?:пожалуйста,?\s+)?(?:найди\s+в\s+интернете|поищи(?:\s+в\s+интернете)?),?\s+(?<query>.+)$", MatchOptions);
            if (!search.Success)
                search = Regex.Match(text, @"^найди\s+(?<query>.+?)\s+в\s+интернете$", MatchOptions);
            if (search.Success)
            {
                Reset();
                var query = search.Groups["query"].Value;
                await Task.Run(() => windows.Search(query), token);
                return new($"Ищу «{query}».");
            }

            if (Is(text, @"^(который час|сколько (сейчас )?времени|скажи время|какое сегодня число|какая сегодня дата|какой сегодня день|скажи дату)$"))
            {
                var now = DateTimeOffset.Now;
                return new(now.ToString("'Сейчас' HH:mm. 'Сегодня' d MMMM yyyy 'года,' dddd.", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")));
            }
            if (Is(text, @"\b(переименуй|переименовать|измени имя)\b.*\bпапк\w*\b"))
            {
                Reset();
                var names = Regex.Match(AfterFolder(text), """^\s*(?<old>«[^»]+»|"[^"]+"|[^«"]+?)\s+(?:в|на)\s+(?!рабочем столе(?:\s|$))(?<new>.+)$""", MatchOptions);
                if (names.Success)
                    return Rename(Name(names.Groups["old"].Value), Name(names.Groups["new"].Value));
                var source = Name(AfterFolder(text));
                if (source.Length > 0) return AskTarget(source);
                _pending = FollowUp.RenameSource;
                return new("Как называется папка на рабочем столе, которую нужно переименовать?");
            }

            if (Is(text, @"\b(создай|создать|сделай|добавь)\b.*\bпапк\w*\b"))
            {
                Reset();
                var name = Name(AfterFolder(text));
                if (name.Length > 0) return Create(name);
                _pending = FollowUp.Create;
                return new("Как назвать новую папку на рабочем столе?");
            }

            if (Is(text, @"\b(плейлист\w*|коллекци\w*)\b") && Is(text, Open))
                return await MusicAsync(MusicAction.Playlists, token);
            if (Is(text, @"\b(переключи|переключить|пропусти)\b.*\b(песн\w*|трек\w*|композици\w*)\b|\bследующ\w*\s+(трек|песн\w*|композици\w*)\b"))
                return await MusicAsync(MusicAction.Next, token);
            if (Is(text, @"\bпредыдущ\w*\s+(трек|песн\w*|композици\w*)\b"))
                return await MusicAsync(MusicAction.Previous, token);
            if (Is(text, @"^(лайк|нравится)$|\b(поставь|поставить|добавь)\s+(лайк|в избранное)\b|\bлайкни\b"))
                return await MusicAsync(MusicAction.Like, token);
            if (Is(text, @"\b(пауза|приостанови музыку|поставь на паузу)\b"))
                return await MusicAsync(MusicAction.Pause, token);
            if (Is(text, @"\b(продолжи|возобнови)\s+(музык\w*|воспроизведени\w*)\b"))
                return await MusicAsync(MusicAction.Play, token);
            if (YandexMusicRequest(text) is { } musicAction)
                return await MusicAsync(musicAction, token);

            if (Is(text, Open))
            {
                if (Is(text, @"^(?:пожалуйста,?\s+|можешь\s+)?" + Open + @"\s+(?:сайт\s+)?(?:вк|вконтакте|vk)(?:[.!?])?$"))
                {
                    Reset();
                    await Task.Run(() => windows.OpenWebsite(new Uri("https://vk.com/")), token);
                    return new("Открываю ВКонтакте.");
                }
                foreach (var app in Apps)
                {
                    if (!Is(text, app.Pattern)) continue;
                    Reset();
                    await Task.Run(() => windows.Open(app.Id), token);
                    return new($"Открываю {app.Name}.");
                }
                var installed = Regex.Match(text, @"^(?:пожалуйста,?\s+|можешь\s+)?(?:открой|открыть|запусти|запустить)\s+(?:приложение\s+)?(?<app>[\p{L}\p{N} «»""().+_-]{1,70})$", MatchOptions);
                if (installed.Success)
                {
                    Reset();
                    var app = installed.Groups["app"].Value.Trim().Trim('«', '»', '"');
                    await Task.Run(() => windows.Open(app), token);
                    return new($"Открываю «{app}».");
                }
            }

            if (_pending != FollowUp.None)
            {
                var name = Name(text);
                if (name.Length == 0) return new("Укажите имя папки или напишите «отмена».");
                return _pending switch
                {
                    FollowUp.Create => Create(name),
                    FollowUp.RenameSource => AskTarget(name),
                    FollowUp.RenameTarget => Rename(_source, name),
                    _ => throw new InvalidOperationException()
                };
            }

            if (Is(text, @"^(помощь|что ты умеешь|список команд)$"))
                return new("Я открываю приложения, создаю и переименовываю папки на рабочем столе и управляю Яндекс Музыкой. Примеры есть в разделе «Команды».");
            return await chat.AnswerAsync(text, model, history, token);
        }
        catch (HttpRequestException error) when (error.StatusCode is null)
        {
            return new("Не удалось подключиться к локальному чату. Проверьте, запущена ли Ollama.", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return new(error.Message, true);
        }
    }

    private async Task<Reply> MusicAsync(MusicAction action, CancellationToken token)
    {
        Reset();
        return await music.RunAsync(action, token);
    }

    private static MusicAction? YandexMusicRequest(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value).ToArray();
        for (var index = 0; index < words.Length; index++)
        {
            var name = words[index];
            var length = 0;
            if (name is "ям" or "ямузыка" or "яндексмузыка" or "яндексмузыку")
                length = 1;
            else if (index + 1 < words.Length)
            {
                var service = words[index + 1];
                if ((name is "я" or "янд" || OneEditFrom(name, "яндекс")) &&
                    (service is "муз" or "музыки" or "музыке" || OneEditFrom(service, "музыка") || OneEditFrom(service, "музыку")) ||
                    name == "yandex" && service == "music")
                    length = 2;
            }

            if (length == 0) continue;
            if (words.Length == length) return MusicAction.Open;
            if (index > 0 && words[index - 1] == "не") continue;
            if (Is(text, Open)) return Is(text, @"\bвключ(и|ить)\b") ? MusicAction.Play : MusicAction.Open;
            // Tolerate a single typo in an action directly before the known service name.
            if (index > 0 && (index < 2 || words[index - 2] != "не"))
            {
                var verb = words[index - 1];
                if (verb == "вкл" || OneEditFrom(verb, "включи")) return MusicAction.Play;
                if (OneEditFrom(verb, "открой") || OneEditFrom(verb, "запусти")) return MusicAction.Open;
            }
        }
        return null;
    }

    internal static bool OneEditFrom(string word, string expected)
    {
        if (Math.Abs(word.Length - expected.Length) > 1) return false;
        if (word.Length > expected.Length) (word, expected) = (expected, word);
        var index = 0;
        while (index < word.Length && word[index] == expected[index]) index++;
        if (index == word.Length) return true;
        if (word.Length < expected.Length)
            return word.AsSpan(index).SequenceEqual(expected.AsSpan(index + 1));
        if (word.AsSpan(index + 1).SequenceEqual(expected.AsSpan(index + 1))) return true;
        return index + 1 < word.Length && word[index] == expected[index + 1] &&
               word[index + 1] == expected[index] && word.AsSpan(index + 2).SequenceEqual(expected.AsSpan(index + 2));
    }

    private Reply Create(string name)
    {
        windows.CreateFolder(name);
        Reset();
        return new($"Папка «{name}» создана на рабочем столе.");
    }

    private Reply AskTarget(string source)
    {
        _source = source;
        _pending = FollowUp.RenameTarget;
        return new($"Как назвать папку «{source}»?");
    }

    private Reply Rename(string source, string target)
    {
        windows.RenameFolder(source, target);
        Reset();
        return new($"Папка «{source}» переименована в «{target}».");
    }

    private static string AfterFolder(string text) =>
        Regex.Replace(text, @"^.*?\bпапк\w*\b", "", MatchOptions);

    private static string Name(string text)
    {
        text = Regex.Replace(text.Trim(), @"^(с именем|с названием|под названием|назови|по имени)\s+", "", MatchOptions);
        var quoted = Regex.Match(text, """^(?:«(?<name>[^»]+)»|"(?<name>[^"]+)")(?:\s+на рабочем столе)?$""", MatchOptions);
        if (quoted.Success) return quoted.Groups["name"].Value;
        return Regex.Replace(text, @"(?:^|\s)на рабочем столе$", "", MatchOptions).Trim();
    }

    private static bool Is(string text, string pattern) => Regex.IsMatch(text, pattern, MatchOptions);
}

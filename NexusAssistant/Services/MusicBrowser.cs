using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Diagnostics;

namespace NexusAssistant.Services;

public enum MusicAction { Open, Play, Pause, Next, Previous, Like, Playlists }

public interface IMusicActions
{
    Task<Reply> RunAsync(MusicAction action, CancellationToken token);
}

public sealed class MusicBrowser(Action<Uri> openWebsite, Func<PreferredBrowser>? preferredBrowser = null) : IMusicActions
{
    private static readonly Uri Home = new("https://music.yandex.ru/");
    private static readonly Uri Playlists = new(Home, "collection/playlists");
    private static readonly OrCondition BrowserWindows = new(
        new PropertyCondition(AutomationElement.ClassNameProperty, "MozillaWindowClass"),
        new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));
    private static readonly PropertyCondition TabItem = new(AutomationElement.ControlTypeProperty, ControlType.TabItem);
    private static readonly PropertyCondition Document = new(AutomationElement.ControlTypeProperty, ControlType.Document);
    private static readonly PropertyCondition Address = new(AutomationElement.AutomationIdProperty, "urlbar-input");
    private static readonly Condition Controls = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));

    public Task<Reply> RunAsync(MusicAction action, CancellationToken token) =>
        Task.Run(() => Run(action, token), token);

    private Reply Run(MusicAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (action == MusicAction.Open)
        {
            openWebsite(Home);
            return new("Открываю Яндекс Музыку.");
        }

        if (action == MusicAction.Playlists)
        {
            openWebsite(Playlists);
            return new("Открываю плейлисты Яндекс Музыки.");
        }

        try
        {
            var page = FindMusicPage(token);
            if (page is not null) return ControlPlayer(page, action, token);
        }
        catch (ElementNotAvailableException)
        {
            return new("Вкладка Яндекс Музыки изменилась. Повторите команду.", true);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return new("Браузер не предоставил доступ к элементам плеера. Откройте вкладку Яндекс Музыки и попробуйте ещё раз.", true);
        }

        if (action == MusicAction.Play)
        {
            openWebsite(Home);
            for (var attempt = 0; attempt < 14; attempt++)
            {
                if (token.WaitHandle.WaitOne(360)) token.ThrowIfCancellationRequested();
                try
                {
                    if (FindMusicPage(token) is not { } page) continue;
                    var result = ControlPlayer(page, action, token);
                    if (!result.Error) return result;
                }
                catch (ElementNotAvailableException) { }
            }
            return new("Яндекс Музыка открыта. Если плеер ещё недоступен, войдите в аккаунт и запустите трек.", true);
        }
        return new("Откройте вкладку Яндекс Музыки и запустите трек, затем повторите команду.", true);
    }

    private AutomationElement? FindMusicPage(CancellationToken token)
    {
        var browser = preferredBrowser?.Invoke() ?? PreferredBrowser.Firefox;
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, BrowserWindows);
        foreach (AutomationElement window in windows)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(window.Current.ProcessId);
                var name = process.ProcessName.ToLowerInvariant();
                if (browser == PreferredBrowser.SystemDefault
                    ? name is not ("firefox" or "chrome" or "msedge" or "brave")
                    : name != (browser == PreferredBrowser.Edge ? "msedge" : browser.ToString().ToLowerInvariant())) continue;
            }
            catch (ArgumentException) { continue; }
            if (ReadPage(window) is { } active) return active;
            foreach (AutomationElement tab in window.FindAll(TreeScope.Descendants, TabItem))
            {
                token.ThrowIfCancellationRequested();
                if (!IsMusicTitle(tab.Current.Name) || !IsBrowserChrome(tab) ||
                    !tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) continue;
                var selection = (SelectionItemPattern)pattern;
                if (!selection.Current.IsSelected) selection.Select();
                if (token.WaitHandle.WaitOne(180)) token.ThrowIfCancellationRequested();
                if (ReadPage(window) is { } selected) return selected;
            }
        }
        return null;
    }

    private static AutomationElement? ReadPage(AutomationElement window)
    {
        if (!IsMusicTitle(window.Current.Name)) return null;
        var address = window.FindAll(TreeScope.Descendants, Address).Cast<AutomationElement>().FirstOrDefault(IsBrowserChrome);
        if (address is null)
        {
            var edits = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            address = edits.Cast<AutomationElement>().FirstOrDefault(edit =>
                Matches(edit.Current.Name, @"адресн\w+\s+строк|address.*(?:bar|search)|search with|поиск или ввод адреса") && IsBrowserChrome(edit));
        }
        var addressValue = address is not null && address.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
            ? ((ValuePattern)pattern).Current.Value : null;
        if (!IsMusicAddress(addressValue)) return null;
        foreach (AutomationElement page in window.FindAll(TreeScope.Descendants, Document))
        {
            if (page.Current.IsOffscreen || !IsMusicTitle(page.Current.Name)) continue;
            var documentValue = page.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : null;
            if (!IsBrowserChrome(page)) continue;
            if (string.IsNullOrWhiteSpace(documentValue) || IsMusicAddress(documentValue)) return page;
        }
        return null;
    }

    private static bool IsBrowserChrome(AutomationElement element)
    {
        for (var parent = TreeWalker.RawViewWalker.GetParent(element); parent is not null;
             parent = TreeWalker.RawViewWalker.GetParent(parent))
        {
            if (parent.Current.ControlType == ControlType.Document) return false;
            if (parent.Current.ClassName is "MozillaWindowClass" or "Chrome_WidgetWin_1") return true;
        }
        return false;
    }

    private static bool IsMusicAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
               uri.IdnHost == Home.Host && uri.UserInfo.Length == 0 && uri.IsDefaultPort;
    }

    private static bool IsMusicTitle(string title) =>
        Matches(title, @"Яндекс[\s.\u00a0]*Музыка|Yandex\s+Music");

    private static Reply ControlPlayer(AutomationElement page, MusicAction action, CancellationToken token)
    {
        var controls = page.FindAll(TreeScope.Descendants, Controls).Cast<AutomationElement>()
            .Where(control => control.Current.IsEnabled && !control.Current.IsOffscreen).ToArray();
        AutomationElement? Find(string expression) =>
            controls.FirstOrDefault(control => Matches(control.Current.Name, expression));
        var pause = Find(@"^(пауза|приостановить|pause)(?:$|[\s(])");
        var resume = Find(@"^(продолжить|воспроизвести|play)(?:$|[\s(])");

        if (action == MusicAction.Play && pause is not null) return new("Музыка уже играет.");
        if (action == MusicAction.Pause && pause is null && resume is not null) return new("Воспроизведение уже на паузе.");
        if (action == MusicAction.Like &&
            Find(@"^(убрать отметку|убрать из избранного|удалить из (?:избранного|коллекции)|unlike|remove from favorites)") is not null)
            return new("Этот трек уже в избранном.");

        var target = action switch
        {
            MusicAction.Play => resume ?? Find(@"^(запустить |включить )?мо[яю] волн[ау]|^my wave"),
            MusicAction.Pause => pause,
            MusicAction.Next => Find(@"^(следующий (?:трек|композиция)|next(?: track)?)(?:$|[\s(])"),
            MusicAction.Previous => Find(@"^(предыдущий (?:трек|композиция)|previous(?: track)?)(?:$|[\s(])"),
            MusicAction.Like => Find(@"^(нравится|добавить в (?:избранное|коллекцию)|like|add to favorites)(?:$|[\s(])"),
            _ => null
        };
        if (target is null) return new("Элемент плеера не найден. Проверьте, что трек доступен во вкладке Яндекс Музыки.", true);
        token.ThrowIfCancellationRequested();
        if (target.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            var control = (TogglePattern)toggle;
            if (action == MusicAction.Like && control.Current.ToggleState == ToggleState.On)
                return new("Этот трек уже в избранном.");
            control.Toggle();
        }
        else if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            ((InvokePattern)invoke).Invoke();
        else
            return new("Браузер показал кнопку плеера, но она недоступна для управления.", true);

        return new(action switch
        {
            MusicAction.Play => "Отправляю команду воспроизведения.",
            MusicAction.Pause => "Ставлю воспроизведение на паузу.",
            MusicAction.Next => "Переключаю на следующий трек.",
            MusicAction.Previous => "Переключаю на предыдущий трек.",
            _ => "Отправляю команду добавления в избранное."
        });
    }

    private static bool Matches(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

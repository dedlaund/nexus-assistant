using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NexusAssistant.Services;

public interface IWindowsActions
{
    void Open(string app);
    void Search(string query);
    void OpenPath(string path);
    void OpenWebsite(Uri address);
    void CreateFolder(string name);
    void RenameFolder(string name, string replacement);
}

public sealed class WindowsActions(string desktop) : IWindowsActions
{
    public PreferredBrowser Browser { get; set; } = PreferredBrowser.Firefox;
    public void Open(string app)
    {
        switch (app)
        {
            case "settings": Launch("ms-settings:"); break;
            case "explorer": Launch("explorer.exe"); break;
            case "notepad": Launch("notepad.exe"); break;
            case "calculator": Launch("calc.exe"); break;
            case "paint": Launch("mspaint.exe"); break;
            case "tasks": Launch("taskmgr.exe"); break;
            case "sound": Launch("ms-settings:sound"); break;
            case "display": Launch("ms-settings:display"); break;
            case "documents": LaunchFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)); break;
            case "desktop": LaunchFolder(desktop); break;
            case "downloads": Launch("shell:Downloads"); break;
            case "computer": Launch("shell:MyComputerFolder"); break;
            case "browser": OpenBrowser(Browser, null); break;
            case "firefox": OpenBrowser(PreferredBrowser.Firefox, null); break;
            case "chrome": OpenBrowser(PreferredBrowser.Chrome, null); break;
            case "edge": OpenBrowser(PreferredBrowser.Edge, null); break;
            case "brave": OpenBrowser(PreferredBrowser.Brave, null); break;
            case "roblox": Launch(FindStudio()); break;
            default:
                Launch(FindShortcut(app) ?? throw new FileNotFoundException($"«{app}» не найдено среди ярлыков меню «Пуск»."));
                break;
        }
    }

    public void Search(string query) => OpenWebsite(new Uri("https://yandex.ru/search/?text=" + Uri.EscapeDataString(query)));
    public void OpenPath(string path) => Launch(path);
    public void OpenWebsite(Uri address) => OpenBrowser(Browser, address);

    private static void OpenBrowser(PreferredBrowser browser, Uri? address)
    {
        if (browser == PreferredBrowser.SystemDefault)
        {
            Launch((address ?? new Uri("https://ya.ru/")).AbsoluteUri);
            return;
        }
        var start = new ProcessStartInfo(FindBrowser(browser)) { UseShellExecute = false };
        if (address is not null)
        {
            if (browser == PreferredBrowser.Firefox) start.ArgumentList.Add("-new-tab");
            start.ArgumentList.Add(address.AbsoluteUri);
        }
        Process.Start(start)?.Dispose();
    }

    private static void LaunchFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Windows не вернула доступный путь к папке.");
        Launch(folder);
    }

    public void CreateFolder(string name)
    {
        var path = DesktopPath(name);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("На рабочем столе уже есть объект с таким именем.");
        Directory.CreateDirectory(path);
    }

    public void RenameFolder(string name, string replacement)
    {
        var source = DesktopPath(name);
        var target = DesktopPath(replacement);
        if (!Directory.Exists(source)) throw new IOException("Исходная папка не найдена на рабочем столе.");
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Эта папка является ссылкой. Выберите обычную папку.");
        Directory.Move(source, target);
    }

    private string DesktopPath(string name)
    {
        if (string.IsNullOrWhiteSpace(desktop))
            throw new IOException("Windows не вернула путь к рабочему столу.");
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 ||
            name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]) *(\.|$)", RegexOptions.IgnoreCase))
            throw new IOException("Укажите допустимое имя папки без пути и специальных символов.");
        return Path.Combine(desktop, name);
    }

    private static void Launch(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    private static string FindBrowser(PreferredBrowser browser)
    {
        var (file, folder) = browser switch
        {
            PreferredBrowser.Chrome => ("chrome.exe", @"Google\Chrome\Application"),
            PreferredBrowser.Edge => ("msedge.exe", @"Microsoft\Edge\Application"),
            PreferredBrowser.Brave => ("brave.exe", @"BraveSoftware\Brave-Browser\Application"),
            _ => ("firefox.exe", "Mozilla Firefox")
        };
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{file}");
            if (key?.GetValue(null) is string registered && File.Exists(registered)) return registered;
        }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Path.Combine(Environment.GetFolderPath(root), folder, file);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"Браузер {browser} не найден. Установите его или выберите другой в настройках.");
    }

    private static string FindStudio()
    {
        if (FindShortcut("Roblox Studio") is { } shortcut) return shortcut;
        var versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
        if (Directory.Exists(versions))
        {
            var executable = Directory.EnumerateDirectories(versions)
                .Select(folder => Path.Combine(folder, "RobloxStudioBeta.exe"))
                .Where(File.Exists).MaxBy(File.GetLastWriteTimeUtc);
            if (executable is not null) return executable;
        }
        throw new FileNotFoundException("Roblox Studio не найдена. Установите её, чтобы открывать этой командой.");
    }

    private static string? FindShortcut(string name)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root)) continue;
            var shortcut = Directory.EnumerateFiles(root, "*.lnk",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase));
            if (shortcut is not null) return shortcut;
        }
        return null;
    }
}

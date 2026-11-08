using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace NexusAssistant.Services;

internal static class RemoteDesktopActions
{
    internal sealed record Target(nint Window, string Title, int[]? Tab = null);
    private static readonly PropertyCondition TabType = new(AutomationElement.ControlTypeProperty, ControlType.TabItem);

    public static byte[] Screenshot()
    {
        RequireDesktop();
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new IOException("Основной экран недоступен.");
        using var source = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        var scale = Math.Min(1, 1600d / Math.Max(bounds.Width, bounds.Height));
        using var image = new Bitmap((int)(bounds.Width * scale), (int)(bounds.Height * scale));
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, 0, 0, image.Width, image.Height);
        }
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Jpeg);
        return stream.ToArray();
    }

    public static async Task<string> CpuAsync(CancellationToken token)
    {
        var first = SampleProcesses();
        var start = Stopwatch.GetTimestamp();
        await Task.Delay(650, token).ConfigureAwait(false);
        var second = SampleProcesses();
        var total = Stopwatch.GetElapsedTime(start).TotalMilliseconds * Environment.ProcessorCount;
        var busiest = second.Values.Where(row => first.TryGetValue(row.Id, out var previous) && previous.Start == row.Start)
            .Select(row => (row.Name, row.Id, Percent: 100 * (row.Cpu - first[row.Id].Cpu) / total))
            .Where(row => row.Percent > 0).OrderByDescending(row => row.Percent).Take(10).ToArray();
        return busiest.Length == 0 ? "За время замера заметной нагрузки не было."
            : "Топ процессов · доля общей мощности ЦП\n" +
              string.Join('\n', busiest.Select(row => $"{row.Name} (PID {row.Id}) · {row.Percent:0.0}%"));
    }

    private sealed record ProcessSample(int Id, string Name, DateTime Start, double Cpu);
    private static Dictionary<int, ProcessSample> SampleProcesses()
    {
        var result = new Dictionary<int, ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { result[process.Id] = new(process.Id, process.ProcessName, process.StartTime, process.TotalProcessorTime.TotalMilliseconds); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result;
    }

    public static Target[] List(bool tabs, CancellationToken token)
    {
        RequireDesktop();
        var windows = new List<Target>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var title = new StringBuilder(512);
            if (GetWindowText(handle, title, title.Capacity) > 0) windows.Add(new(handle, title.ToString()));
            return windows.Count < 60;
        }, 0);
        if (!tabs) return windows.Take(30).ToArray();

        var result = new List<Target>();
        foreach (var window in windows)
        {
            token.ThrowIfCancellationRequested();
            var name = new StringBuilder(128);
            GetClassName(window.Window, name, name.Capacity);
            if (name.ToString() is not ("MozillaWindowClass" or "Chrome_WidgetWin_1")) continue;
            try
            {
                var root = AutomationElement.FromHandle(window.Window);
                var processId = root.Current.ProcessId;
                using var process = Process.GetProcessById(processId);
                if (process.ProcessName.ToLowerInvariant() is not ("firefox" or "chrome" or "msedge" or "brave" or "opera" or "vivaldi")) continue;
                foreach (AutomationElement tab in BrowserTabs(root))
                {
                    if (tab.Current.Name.Length == 0) continue;
                    result.Add(new(window.Window, tab.Current.Name, tab.GetRuntimeId()));
                    if (result.Count == 30) return result.ToArray();
                }
            }
            catch (Exception error) when (error is ElementNotAvailableException or ArgumentException or COMException or InvalidOperationException) { }
        }
        return result.ToArray();
    }

    public static string Switch(Target target)
    {
        RequireDesktop();
        if (!IsWindowVisible(target.Window)) return "Окно закрыто. Обновите список.";
        if (target.Tab is { } id)
        {
            foreach (AutomationElement tab in BrowserTabs(AutomationElement.FromHandle(target.Window)))
            {
                if (!tab.GetRuntimeId().SequenceEqual(id) || tab.Current.Name != target.Title) continue;
                if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) break;
                ((SelectionItemPattern)pattern).Select();
                return Activate(target.Window) ? "Вкладка выбрана." : "Вкладка выбрана. Windows не вывела браузер на передний план.";
            }
            return "Вкладка изменилась или закрылась. Запросите список снова.";
        }
        var title = new StringBuilder(512);
        if (GetWindowText(target.Window, title, title.Capacity) == 0 || title.ToString() != target.Title)
            return "Окно изменилось. Запросите список снова.";
        return Activate(target.Window) ? "Окно открыто на переднем плане." : "Windows не разрешила перевести окно на передний план.";
    }

    private static IEnumerable<AutomationElement> BrowserTabs(AutomationElement root) =>
        root.FindAll(TreeScope.Descendants, TabType).Cast<AutomationElement>().Where(tab =>
        {
            // Exclude tabs drawn by the website itself.
            for (var parent = TreeWalker.RawViewWalker.GetParent(tab); parent is not null && !parent.Equals(root);
                 parent = TreeWalker.RawViewWalker.GetParent(parent))
                if (parent.Current.ControlType == ControlType.Document) return false;
            return true;
        });

    private static bool Activate(nint window)
    {
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        return SetForegroundWindow(window) || GetForegroundWindow() == window;
    }

    private static void RequireDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == 0) throw new IOException("Разблокируйте рабочий стол Windows.");
        try
        {
            var name = new StringBuilder(128);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) ||
                !name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Разблокируйте рабочий стол Windows.");
        }
        finally { CloseDesktop(desktop); }
    }

    private delegate bool WindowEnumerator(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumerator callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);
}

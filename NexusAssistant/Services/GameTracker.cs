using System.Diagnostics;
using System.Windows.Threading;

namespace NexusAssistant.Services;

internal sealed record PcToolsSettings
{
    public QuickShortcut Shortcut { get; init; } = QuickShortcut.Ctrl_Alt_N;
    public string Games { get; init; } = "dota2, cs2, RobloxPlayerBeta, GTA5, FortniteClient-Win64-Shipping";
    public string CloseForGames { get; init; } = "";
}

internal sealed class GameTracker(LocalStore store) : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<int, (DateTime Start, ProcessPriorityClass Original)> _priorities = [];
    private Dictionary<string, double> _total = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _games = new(StringComparer.OrdinalIgnoreCase);
    private Task _write = Task.CompletedTask;
    private long _last;
    private bool _dirty;
    private readonly int _session = Process.GetCurrentProcess().SessionId;

    public event Action? Updated;
    public bool GamingMode { get; private set; }
    public string? StorageError { get; private set; }

    public async Task StartAsync(string games)
    {
        try
        {
            var stored = await store.ReadAsync("game-time.json", new Dictionary<string, double>());
            foreach (var (name, seconds) in stored)
                if (double.IsFinite(seconds) && seconds >= 0)
                    _total[name] = Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2);
        }
        catch (Exception error) { StorageError = error.Message; }

        _games = Names(games);
        _timer.Tick += Tick;
        Observe();
        _timer.Start();
    }

    public void SetGames(string games)
    {
        Accrue();
        _games = Names(games);
        foreach (var id in _priorities.Keys.ToArray())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!_games.Contains(process.ProcessName)) RestorePriority(id);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                RestorePriority(id);
            }
        }
        Observe();
    }

    public string Describe()
    {
        var rows = _total.Keys.Union(_games, StringComparer.OrdinalIgnoreCase).OrderByDescending(_active.Contains)
            .ThenByDescending(name => _total.GetValueOrDefault(name))
            .Select(name =>
            {
                var duration = TimeSpan.FromSeconds(_total.GetValueOrDefault(name));
                return $"{name}  ·  {(long)duration.TotalHours} ч {duration.Minutes:00} мин{(_active.Contains(name) ? "  ·  запущена" : "")}";
            });
        return string.Join(Environment.NewLine, rows);
    }

    public string EnableMode(string closeList)
    {
        GamingMode = true;
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names(closeList).Except(_games, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId && process.SessionId == _session &&
                            process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                            closed.Add(name);
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
        Observe();
        var result = closed.Count == 0 ? "Запросы на закрытие не отправлены." : $"Запрошено закрытие: {string.Join(", ", closed)}.";
        return $"Игровой режим включён. Повышен приоритет доступных процессов игр: {_priorities.Count}. {result}";
    }

    public void DisableMode()
    {
        GamingMode = false;
        foreach (var id in _priorities.Keys.ToArray()) RestorePriority(id);
        Updated?.Invoke();
    }

    private void RestorePriority(int id)
    {
        if (!_priorities.Remove(id, out var prior)) return;
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.StartTime == prior.Start && process.PriorityClass == ProcessPriorityClass.AboveNormal)
                process.PriorityClass = prior.Original;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static HashSet<string> Names(string input) => input
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(name => name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name)
        .Where(name => name.Length is > 0 and <= 120)
        .Take(64).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void Accrue()
    {
        var now = Stopwatch.GetTimestamp();
        if (_last != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
            if (elapsed <= 30)
            {
                foreach (var name in _active)
                {
                    _total[name] = _total.GetValueOrDefault(name) + elapsed;
                    _dirty = true;
                }
            }
        }
        _last = now;
    }

    private void Observe()
    {
        Accrue();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!_games.Contains(name) || process.SessionId != _session) continue;
                    running.Add(name);
                    if (!GamingMode) continue;
                    seen.Add(process.Id);
                    var start = process.StartTime;
                    if (_priorities.TryGetValue(process.Id, out var old))
                    {
                        if (old.Start == start) continue;
                        _priorities.Remove(process.Id);
                    }
                    var original = process.PriorityClass;
                    if (original is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
                    {
                        process.PriorityClass = ProcessPriorityClass.AboveNormal;
                        _priorities[process.Id] = (start, original);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        foreach (var id in _priorities.Keys.Where(id => !seen.Contains(id)).ToArray())
            RestorePriority(id);
        _active = running;
        Updated?.Invoke();
    }

    private async void Tick(object? sender, EventArgs e)
    {
        Observe();
        try { await Save(); }
        catch (Exception error) { StorageError = error.Message; _dirty = true; Updated?.Invoke(); }
    }

    private Task Save()
    {
        if (!_dirty) return _write;
        _dirty = false;
        var snapshot = new Dictionary<string, double>(_total);
        var previous = _write;
        _write = Task.Run(async () =>
        {
            try { await previous; } catch { /* A new snapshot can retry the last write. */ }
            await store.WriteAsync("game-time.json", snapshot);
        });
        return _write;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Tick;
        DisableMode();
        Accrue();
        try { Save().GetAwaiter().GetResult(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

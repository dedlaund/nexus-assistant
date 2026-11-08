using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusAssistant.Services;

internal static class PcDiagnostics
{
    private static readonly HttpClient Network = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    internal sealed record Disk(string Root, string Label);

    public static Disk[] Disks() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
        .Select(drive => new Disk(drive.Name,
            $"{drive.Name}   ·   Занято {Size(drive.TotalSize - drive.TotalFreeSpace)} из {Size(drive.TotalSize)}   ·   Свободно {Size(drive.AvailableFreeSpace)}"))
        .ToArray();

    public static string ScanDisk(string root, CancellationToken token)
    {
        var time = Stopwatch.StartNew();
        var sizes = new List<(string Name, long Bytes)>();
        var unreadable = 0;
        var scanned = 0;
        bool Limited() => scanned >= 500_000 || time.Elapsed >= TimeSpan.FromSeconds(30);
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", TopLevel))
        {
            token.ThrowIfCancellationRequested();
            if (Limited()) break;
            long bytes = 0;
            var pending = new Stack<DirectoryInfo>();
            if (entry is DirectoryInfo directory) { scanned++; pending.Push(directory); }
            else if (entry is FileInfo file) Count(file);
            while (pending.TryPop(out var current) && !Limited())
            {
                try
                {
                    foreach (var child in current.EnumerateFileSystemInfos("*", TopLevel))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Limited()) break;
                        if (child is DirectoryInfo subfolder) { scanned++; pending.Push(subfolder); }
                        else if (child is FileInfo nested) Count(nested);
                    }
                }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException) { unreadable++; }
            }
            sizes.Add((entry.Name, bytes));

            void Count(FileInfo item)
            {
                scanned++;
                try { bytes += item.Length; }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException) { unreadable++; }
            }
        }

        var lines = sizes.OrderByDescending(row => row.Bytes).Where(row => row.Bytes > 0).Take(14)
            .Select(row => $"{Size(row.Bytes)}   ·   {row.Name}");
        var heading = Limited() ? "Частичный результат. Достигнут лимит времени или числа записей." : "Крупнейшие элементы в корне диска.";
        return $"{heading}\n{string.Join('\n', lines)}\nПроверено записей: {scanned:N0}. Ссылки и недоступные каталоги пропущены." +
            (unreadable == 0 ? "" : $" Ошибок чтения: {unreadable:N0}.");
    }

    public static string EmptyRecycleBin(IntPtr owner)
    {
        var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBin(null, ref info) == 0 && info.Items == 0) return "Корзина уже пуста.";
        var status = SHEmptyRecycleBin(owner, null, 4);
        return status switch
        {
            0 => "Корзина очищена.",
            1 or unchecked((int)0x800704C7) => "Очистка отменена.",
            _ => throw new InvalidOperationException($"Не удалось очистить корзину (0x{status:X8}).")
        };
    }

    public static Task<string> TemperaturesAsync(CancellationToken token) => PowerShellAsync("""
        $rows = @()
        $rows += Get-CimInstance -Namespace root/LibreHardwareMonitor -ClassName Sensor -ErrorAction SilentlyContinue |
            Where-Object SensorType -eq Temperature |
            ForEach-Object { '{0}  ·  {1:0.#} °C  ·  {2}' -f $_.Name, $_.Value, $_.Identifier }
        if (-not ($rows -match '/gpu-nvidia/')) {
            $nvidia = @("$env:SystemRoot\System32\nvidia-smi.exe", "$env:ProgramFiles\NVIDIA Corporation\NVSMI\nvidia-smi.exe") |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            if ($nvidia) {
                try {
                    $rows += & $nvidia --query-gpu=name,temperature.gpu --format=csv,noheader,nounits 2>$null |
                        ForEach-Object { if ($_ -match '^(.+),\s*(\d+)$') { '{0}  ·  {1} °C' -f $Matches[1], $Matches[2] } }
                } catch { }
            }
        }
        $rows += Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
            $disk = $_
            $sensor = $disk | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
            if ($sensor.Temperature -gt 0) { '{0}  ·  {1} °C  ·  накопитель' -f $disk.FriendlyName, $sensor.Temperature }
        }
        if (-not ($rows -match '/intelcpu/|/amdcpu/')) {
            $rows += Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue |
                Where-Object CurrentTemperature -gt 0 |
                ForEach-Object { 'Зона ACPI {0}  ·  {1:0.#} °C' -f $_.InstanceName, ($_.CurrentTemperature / 10 - 273.15) }
        }
        if ($rows) { $rows | Select-Object -First 36 }
        if (-not ($rows -match '/intelcpu/|/amdcpu/')) { 'Для температуры ЦП запустите LibreHardwareMonitor с поддержкой WMI.' }
        if (-not $rows) { 'Доступных датчиков Windows не найдено.' }
        """, token);

    public static async Task<string> LoadAsync(CancellationToken token)
    {
        var cpu = Task.Run(() => RemoteDesktopActions.CpuAsync(token), token);
        var gpu = PowerShellAsync("""
            $engines = Get-CimInstance -ClassName Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction SilentlyContinue
            if (-not $engines) { 'Счётчики GPU недоступны.'; return }
            $rows = $engines | Where-Object { $_.UtilizationPercentage -gt 0 -and $_.Name -match '^pid_(\d+)_' } |
                Group-Object { ($_.Name -split '_')[1] } |
                ForEach-Object {
                    $process = Get-Process -Id ([int]$_.Name) -ErrorAction SilentlyContinue
                    if ($process) {
                        [PSCustomObject]@{ Name = $process.ProcessName; Id = $process.Id; Percent = ($_.Group | Measure-Object UtilizationPercentage -Maximum).Maximum }
                    }
                } | Sort-Object Percent -Descending | Select-Object -First 6
            if ($rows) {
                'GPU · наиболее загруженный движок каждого процесса'
                $rows | ForEach-Object { '{0} (PID {1}) · {2}%' -f $_.Name, $_.Id, $_.Percent }
            } else { 'Во время замера заметной нагрузки GPU не было.' }
            """, token);
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var ram = GlobalMemoryStatusEx(ref memory)
            ? $"ОЗУ · {memory.Load}%  ({Size((long)(memory.TotalPhysical - memory.AvailablePhysical))} из {Size((long)memory.TotalPhysical)})\n\n" : "";
        await Task.WhenAll(cpu, gpu);
        return $"{ram}{await cpu}\n\n{await gpu}\n\nВысокая нагрузка — повод проверить процесс. Для проверки угроз запустите Защитник Windows.";
    }

    public static Task<string> DefenderAsync(bool scan, CancellationToken token) => PowerShellAsync((scan ? """
        Start-MpScan -ScanType QuickScan -ErrorAction Stop
        """ + "\n" : "") + """
        $state = Get-MpComputerStatus -ErrorAction Stop
        'Защитник Windows · мониторинг в реальном времени: ' + $(if ($state.RealTimeProtectionEnabled) { 'включён' } else { 'выключен' })
        $items = @(Get-MpThreat -ErrorAction Stop | Select-Object -First 12)
        if (-not $items) { 'В журнале Защитника нет записей об угрозах.' }
        $items | ForEach-Object {
            '{0} · {1}' -f $_.ThreatName, $(if ($_.IsActive) { 'активна' } else { 'неактивна / устранена' })
        }
        """, token, scan ? 600 : 20);

    public static async Task<string> PingAsync(string host, CancellationToken token)
    {
        host = host.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Укажите имя хоста или IP-адрес без URL.");
        using var ping = new Ping();
        var samples = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(1500), cancellationToken: token);
            if (reply.Status == IPStatus.Success) samples.Add(reply.RoundtripTime);
        }
        return samples.Count == 0 ? $"{host} · нет ответа на 4 ICMP-запроса."
            : $"{host} · средний пинг {samples.Average():0.#} мс · мин. {samples.Min()} мс · потери {(4 - samples.Count) * 25}%.";
    }

    public static async Task<string> SpeedAsync(CancellationToken token)
    {
        const string endpoint = "https://speed.cloudflare.com/";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(45));
        var request = limit.Token;
        var watch = Stopwatch.StartNew();
        using var response = await Network.GetAsync(endpoint + "__down?bytes=8000000", HttpCompletionOption.ResponseHeadersRead, request);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(request);
        var buffer = new byte[65536];
        var received = 0;
        while (received < 8_000_000)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 8_000_000 - received)), request);
            if (read == 0) break;
            received += read;
        }
        var down = received * 8 / watch.Elapsed.TotalSeconds / 1_000_000;
        using var payload = new ByteArrayContent(new byte[2_000_000]);
        watch.Restart();
        using var uploaded = await Network.PostAsync(endpoint + "__up", payload, request);
        uploaded.EnsureSuccessStatusCode();
        var up = 16 / watch.Elapsed.TotalSeconds;
        return $"Загрузка ↓ {down:0.#} Мбит/с   ·   Отдача ↑ {up:0.#} Мбит/с\nОриентировочный замер до Cloudflare, до 10 МБ трафика.";
    }

    private static async Task<string> PowerShellAsync(string script, CancellationToken token, int timeoutSeconds = 25)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false);\ntry {\n" + script +
            "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"));
        var start = new ProcessStartInfo(powershell, $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell не запустился.");
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try { await Task.WhenAll(process.WaitForExitAsync(limit.Token), output, errors); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Windows не завершила проверку за отведённое время.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"Windows не выполнила проверку. {(await errors).Trim()}");
        return (await output).Trim();
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} ТиБ",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} ГиБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} МиБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} КиБ",
        _ => $"{bytes} Б"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public uint Size;
        public long Bytes, Items;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr owner, string? root, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref RecycleBinInfo info);
}

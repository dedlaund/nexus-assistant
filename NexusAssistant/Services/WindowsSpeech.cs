using System.Buffers.Binary;
using System.Diagnostics;
using System.Media;
using System.Text;

namespace NexusAssistant.Services;

public interface ISpeech
{
    Task<string?> ListenAsync(Action ready, Action<double> level, Action decoding, CancellationToken token);
    Task SpeakAsync(string text, Action<double> level, CancellationToken token);
}

public sealed class WindowsSpeech(string dataDirectory) : ISpeech
{
    private readonly WhisperSpeech _whisper = new(Path.Combine(dataDirectory, "Speech"));

    public async Task<string?> ListenAsync(Action ready, Action<double> level, Action decoding, CancellationToken token)
    {
        if (_whisper.Available) return await _whisper.ListenAsync(ready, level, decoding, token);
        using var process = Start("listen");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            string? result = null;
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (line == "READY") ready();
                if (line == "NO_RECOGNIZER")
                    throw new InvalidOperationException("Русский распознаватель Windows не найден. Подключите Whisper по инструкции в README или введите команду текстом.");
                if (line.StartsWith("LEVEL ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), out var amplitude))
                    level(amplitude / 100.0);
                if (line.StartsWith("TEXT ", StringComparison.Ordinal)) result = line[5..];
            }
            await CheckExitAsync(process, errors, token);
            return result;
        }
        finally
        {
            Stop(process);
        }
    }

    public async Task SpeakAsync(string text, Action<double> level, CancellationToken token)
    {
        using var process = Start("speak");
        var errors = process.StandardError.ReadToEndAsync();
        byte[] wave;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var limited = text.Length > 1800 ? text[..1800] + "…" : text;
            await process.StandardInput.WriteAsync(limited.AsMemory(), token);
            process.StandardInput.Close();
            await CheckExitAsync(process, errors, token);
            var encoded = (await output).Trim();
            if (encoded == "NO_VOICE")
                throw new InvalidOperationException("В Windows не установлен русский голос для озвучивания.");
            wave = Convert.FromBase64String(encoded);
        }
        finally
        {
            Stop(process);
        }

        var samples = FindSamples(wave);
        using var stream = new MemoryStream(wave);
        using var player = new SoundPlayer(stream);
        player.Load();
        token.ThrowIfCancellationRequested();
        player.Play();
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed.TotalSeconds * 16000 < samples.Count / 2)
            {
                var start = (int)(clock.Elapsed.TotalSeconds * 16000) * 2;
                level(Amplitude(wave, samples.Offset + start, samples.Offset + Math.Min(start + 960, samples.Count)));
                await Task.Delay(30, token);
            }
        }
        finally
        {
            player.Stop();
            level(0);
        }
    }

    private static (int Offset, int Count) FindSamples(byte[] wave)
    {
        for (var offset = 12; offset <= wave.Length - 8;)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(offset + 4, 4));
            if (length < 0 || length > wave.Length - offset - 8) break;
            if (wave.AsSpan(offset, 4).SequenceEqual("data"u8)) return (offset + 8, length & ~1);
            offset += 8 + length + (length & 1);
        }
        throw new IOException("Windows вернула неизвестный формат звука.");
    }

    internal static double Amplitude(byte[] wave, int start, int end)
    {
        double squared = 0;
        for (var index = start; index < end; index += 2)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(index, 2)) / 32768.0;
            squared += value * value;
        }
        return Math.Clamp(Math.Sqrt(squared / Math.Max(1, (end - start) / 2)) * 4, 0, 1);
    }

    private static Process Start(string mode)
    {
        var path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "Services", "SpeechHost.ps1");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = mode == "speak",
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (mode == "speak") start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", mode })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить подсистему речи Windows.");
    }

    private static async Task CheckExitAsync(Process process, Task<string> errors, CancellationToken token)
    {
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new IOException($"Подсистема речи Windows: {(await errors).Trim()}");
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
    }
}

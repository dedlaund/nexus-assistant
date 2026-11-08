using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusAssistant.Services;

internal sealed class WhisperSpeech(string folder)
{
    private string Executable => Path.Combine(folder, "whisper-cli.exe");
    private string Model => Path.Combine(folder, "ggml-base.bin");
    public bool Available => File.Exists(Executable) && File.Exists(Model);

    public async Task<string?> ListenAsync(Action ready, Action<double> level, Action decoding, CancellationToken token)
    {
        var audio = await Microphone.CaptureAsync(ready, level, token);
        if (audio is null) return null;
        decoding();
        return await TranscribeAsync(audio, token);
    }

    private async Task<string?> TranscribeAsync(byte[] audio, CancellationToken token)
    {
        var stem = Path.Combine(folder, $".nexus-{Guid.NewGuid():N}");
        var input = stem + ".wav";
        var transcript = stem + ".txt";
        try
        {
            await File.WriteAllBytesAsync(input, audio, token);
            var start = new ProcessStartInfo(Executable)
            {
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            // The encoder counts one context step per 20 ms. Leave headroom around the clip.
            var frames = (audio.Length - Microphone.WaveHeaderBytes) / (Microphone.SampleRate * 2 / 50);
            var context = Math.Clamp((frames + 95) / 32 * 32, 256, 1500);
            foreach (var argument in new[] { "-m", Model, "-f", input, "-l", "ru", "-nt", "-otxt", "-of", stem,
                         "-ac", context.ToString(CultureInfo.InvariantCulture), "-bs", "2", "-bo", "2" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить Whisper.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(token);
                var errors = process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                await Task.WhenAll(output, errors);
                if (process.ExitCode == unchecked((int)0xC000001D))
                    throw new IOException("Эта сборка Whisper использует инструкции, которых нет у процессора. Нужна совместимая сборка.");
                if (process.ExitCode != 0 || !File.Exists(transcript))
                    throw new IOException($"Whisper не вернул результат (код {process.ExitCode}). Проверьте движок и модель в папке Speech.");
                var text = (await File.ReadAllTextAsync(transcript, Encoding.UTF8, token)).Trim();
                text = Regex.Replace(text, @"\[[^\]\r\n]*\]|\([^)\r\n]*\)", "").Trim();
                return text.Any(char.IsLetterOrDigit) ? text : null;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            File.Delete(input);
            File.Delete(transcript);
        }
    }
}

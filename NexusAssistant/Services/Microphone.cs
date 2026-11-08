using System.Runtime.InteropServices;
using System.Text;

namespace NexusAssistant.Services;

internal static class Microphone
{
    internal const int SampleRate = 16000;
    internal const int WaveHeaderBytes = 44;
    private const int BytesPerFrame = SampleRate * 2 / 10;
    private const uint Done = 1;

    public static async Task<byte[]?> CaptureAsync(Action ready, Action<double> level, CancellationToken token)
    {
        var format = new WaveFormat { Format = 1, Channels = 1, Rate = SampleRate, BytesPerSecond = SampleRate * 2, Alignment = 2, Bits = 16 };
        Check(waveInOpen(out var device, uint.MaxValue, ref format, 0, 0, 0));
        var buffers = new List<Buffer>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var buffer = new Buffer();
                buffers.Add(buffer);
                buffer.Prepare(device);
                Check(waveInAddBuffer(device, buffer.Header, Buffer.HeaderSize));
            }
            Check(waveInStart(device));
            ready();

            using var pcm = new MemoryStream();
            var chunk = new byte[BytesPerFrame];
            var speechFrames = 0;
            var silenceFrames = 0;
            var firstVoiceByte = -1;
            var lastVoiceEnd = 0;
            var noiseFloor = .009;
            for (var frame = 0; frame < 120; frame++)
            {
                token.ThrowIfCancellationRequested();
                var buffer = buffers[frame % buffers.Count];
                while ((Marshal.PtrToStructure<WaveHeader>(buffer.Header).Flags & Done) == 0)
                    await Task.Delay(20, token);
                var length = (int)Marshal.PtrToStructure<WaveHeader>(buffer.Header).Recorded;
                Marshal.Copy(buffer.Data, chunk, 0, length);
                Check(waveInAddBuffer(device, buffer.Header, Buffer.HeaderSize));
                var frameStart = (int)pcm.Length;
                pcm.Write(chunk, 0, length);
                var amplitude = WindowsSpeech.Amplitude(chunk, 0, length);
                level(amplitude);
                var threshold = Math.Clamp(noiseFloor * (speechFrames >= 2 ? 1.8 : 2.7), .018, .055);
                if (amplitude >= threshold)
                {
                    if (firstVoiceByte < 0) firstVoiceByte = frameStart;
                    lastVoiceEnd = (int)pcm.Length;
                    speechFrames++;
                    silenceFrames = 0;
                }
                else
                {
                    silenceFrames++;
                    if (speechFrames < 2) noiseFloor += (amplitude - noiseFloor) * .1;
                }

                if (speechFrames >= 2 && silenceFrames >= 8) break;
                if (speechFrames < 2 && frame >= 64) return null;
            }
            if (speechFrames < 2) return null;
            var start = Math.Max(0, firstVoiceByte - 3 * BytesPerFrame);
            var end = Math.Min((int)pcm.Length, lastVoiceEnd + 3 * BytesPerFrame);
            return WrapWave(pcm.GetBuffer().AsSpan(start, end - start));
        }
        finally
        {
            waveInReset(device);
            foreach (var buffer in buffers) buffer.Release(device);
            waveInClose(device);
            level(0);
        }
    }

    private static byte[] WrapWave(ReadOnlySpan<byte> samples)
    {
        using var wave = new MemoryStream();
        using var writer = new BinaryWriter(wave, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(samples.Length + 36);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
        return wave.ToArray();
    }

    private static void Check(uint result)
    {
        if (result != 0)
            throw new IOException($"Windows не смогла записать звук с микрофона (код {result}). Проверьте устройство ввода и доступ к микрофону.");
    }

    private sealed class Buffer
    {
        public static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
        public nint Data { get; } = Marshal.AllocHGlobal(BytesPerFrame);
        public nint Header { get; } = Marshal.AllocHGlobal((int)HeaderSize);
        private bool _prepared;

        public void Prepare(nint device)
        {
            Marshal.StructureToPtr(new WaveHeader { Data = Data, Length = BytesPerFrame }, Header, false);
            Check(waveInPrepareHeader(device, Header, HeaderSize));
            _prepared = true;
        }

        public void Release(nint device)
        {
            if (_prepared) waveInUnprepareHeader(device, Header, HeaderSize);
            Marshal.FreeHGlobal(Header);
            Marshal.FreeHGlobal(Data);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Format, Channels;
        public uint Rate, BytesPerSecond;
        public ushort Alignment, Bits, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint Length, Recorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }

    [DllImport("winmm.dll")] private static extern uint waveInOpen(out nint device, uint id, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(nint device);
    [DllImport("winmm.dll")] private static extern uint waveInReset(nint device);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInClose(nint device);
}

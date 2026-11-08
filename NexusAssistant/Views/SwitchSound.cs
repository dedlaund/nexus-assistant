using System.Media;

namespace NexusAssistant.Views;

internal static class SwitchSound
{
    private static readonly Lazy<SoundPlayer> On = new(() => Create(315));
    private static readonly Lazy<SoundPlayer> Off = new(() => Create(240));

    public static void Play(bool enabled)
    {
        try { (enabled ? On : Off).Value.Play(); }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException) { }
    }

    private static SoundPlayer Create(double pitch)
    {
        const int rate = 22050;
        const int samples = 1102;
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (var i = 0; i < samples; i++)
            {
                var t = (double)i / rate;
                var attack = Math.Min(t / .005, 1);
                var release = 1d - (double)i / (samples - 1);
                var envelope = attack * attack * release * release * Math.Exp(-45 * t);
                writer.Write((short)(1050 * envelope * Math.Sin(Math.Tau * pitch * t)));
            }
        }
        stream.Position = 0;
        return new SoundPlayer(stream);
    }
}

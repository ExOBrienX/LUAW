using System.Runtime.InteropServices;

namespace SimATC.App.Audio;

/// <summary>
/// Aviso sonoro corto cuando el ATC le habla al piloto (prueba de Marco, 2026-10-31): dos tonos suaves, como el "bip"
/// de una radio, generados en memoria (un WAV de ~0,2 s) y tocados con <c>PlaySound</c> de Windows, sin paquetes. En
/// otros sistemas no suena. La voz (etapa 5) tendrá su propio audio.
/// </summary>
public static class MessageChime
{
    private const int SampleRate = 22050;

    // PlaySound con SND_ASYNC necesita que el WAV siga vivo mientras suena: se guarda aquí.
    private static readonly byte[] Wav = Build((880.0, 70), (1320.0, 90));

    public static void Play() => Play(Wav);

    /// <summary>Toca un WAV en memoria sin esperar a que termine (el arreglo tiene que seguir vivo mientras suena).</summary>
    internal static void Play(byte[] wav)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            PlaySound(wav, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sin winmm: sin sonido.
        }
    }

    /// <summary>Tonos seguidos (frecuencia en Hz y duración en ms) con fundido, en un WAV PCM de 16 bits mono.</summary>
    internal static byte[] Build(params (double Freq, int Ms)[] tones)
    {
        var samples = new List<short>();
        foreach (var (freq, ms) in tones)
        {
            var n = SampleRate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var fade = Math.Min(1, Math.Min(i, n - i) / (SampleRate * 0.008)); // 8 ms de entrada y salida
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * i / SampleRate) * 0.25 * fade * short.MaxValue));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write(s);
        return ms2.ToArray();
    }

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
}

/// <summary>
/// Pitidos del micrófono (ADR-126): dos tonos que suben cuando se abre ("ya te escucho, habla") y dos que bajan cuando se
/// cierra. Cortos y más graves que el aviso del ATC, para no confundirlos.
/// </summary>
public static class MicBeep
{
    private static readonly byte[] OnWav = MessageChime.Build((620.0, 55), (930.0, 70));
    private static readonly byte[] OffWav = MessageChime.Build((930.0, 55), (620.0, 70));

    public static void On() => MessageChime.Play(OnWav);

    public static void Off() => MessageChime.Play(OffWav);
}

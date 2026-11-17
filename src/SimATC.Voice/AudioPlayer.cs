using NAudio.Wave;

namespace SimATC.Voice;

/// <summary>
/// Reproduce audio por el dispositivo de salida de Windows (RF-38b), con NAudio (WinMM). Cada reproducción tiene su
/// propia salida: dos radios en frecuencias distintas pueden sonar a la vez, como en la cabina. El volumen va de 0 a 1.
/// </summary>
public sealed class AudioPlayer
{
    /// <summary>Suena <paramref name="clip"/> y termina cuando se oyó entero (o se cancela).</summary>
    public async Task PlayAsync(AudioClip clip, float volume, CancellationToken ct)
    {
        if (clip.Samples.Length == 0 || volume <= 0)
            return;
        var pcm = new byte[clip.Samples.Length * 2];
        var v = Math.Clamp(volume, 0f, 1f);
        for (var i = 0; i < clip.Samples.Length; i++)
        {
            var s = (short)Math.Clamp(clip.Samples[i] * v * 32767f, short.MinValue, short.MaxValue);
            pcm[2 * i] = (byte)s;
            pcm[2 * i + 1] = (byte)(s >> 8);
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(clip.SampleRate, 16, 1));
        using var output = new WaveOutEvent { DesiredLatency = 120 };
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        output.Init(stream);
        output.Play();
        using (ct.Register(() => output.Stop()))
            await done.Task.ConfigureAwait(false);
    }
}

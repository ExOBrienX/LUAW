namespace SimATC.Voice;

/// <summary>Audio mono en memoria: muestras de -1 a 1 y su frecuencia de muestreo.</summary>
public sealed record AudioClip(float[] Samples, int SampleRate)
{
    public double DurationS => SampleRate > 0 ? Samples.Length / (double)SampleRate : 0;

    /// <summary>Otro tono (<paramref name="factor"/> &gt; 1, más agudo): remuestreo lineal, que también acorta un poco la frase.</summary>
    public AudioClip WithPitch(double factor)
    {
        if (Math.Abs(factor - 1) < 0.001 || Samples.Length < 2)
            return this;
        var n = (int)(Samples.Length / factor);
        var output = new float[n];
        for (var i = 0; i < n; i++)
        {
            var x = i * factor;
            var k = (int)x;
            var t = (float)(x - k);
            output[i] = k + 1 < Samples.Length ? Samples[k] * (1 - t) + Samples[k + 1] * t : Samples[^1];
        }
        return new AudioClip(output, SampleRate);
    }

    /// <summary>PCM de 16 bits con signo (lo que entrega Piper con <c>--output_raw</c>) a muestras de -1 a 1.</summary>
    public static AudioClip FromPcm16(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
        return new AudioClip(samples, sampleRate);
    }
}

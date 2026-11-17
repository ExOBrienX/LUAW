namespace SimATC.Voice;

/// <summary>Cómo suena la radio (RF-36, RF-38b).</summary>
/// <param name="LowHz">Corte inferior del pasa-banda (~300 Hz).</param>
/// <param name="HighHz">Corte superior (~3000 Hz).</param>
/// <param name="Drive">Compresión suave: cuánto se empuja la señal antes de saturar (1 = nada).</param>
/// <param name="NoiseLevel">Ruido de fondo (0 a 1), bajo.</param>
/// <param name="ClickS">Clic del micrófono al apretar y al soltar (s).</param>
/// <param name="SquelchS">Cola de ruido al soltar el micrófono (squelch, s).</param>
public sealed record RadioFilterOptions(double LowHz = 300, double HighHz = 3000, double Drive = 2.0, double NoiseLevel = 0.012,
    double ClickS = 0.012, double SquelchS = 0.12)
{
    public static RadioFilterOptions Default { get; } = new();
}

/// <summary>
/// El efecto de radio de AM aeronáutica sobre una voz (RF-36): pasa-banda de dos polos por lado (biquads RBJ), compresión
/// suave (tanh), ruido de fondo bajo, clic del micrófono al empezar y al terminar, y una cola de squelch. Función pura:
/// la misma entrada y la misma semilla dan el mismo audio (se puede probar y guardar en caché).
/// </summary>
public static class RadioFilter
{
    public static AudioClip Apply(AudioClip input, RadioFilterOptions? options = null, int seed = 1)
    {
        var o = options ?? RadioFilterOptions.Default;
        var rate = input.SampleRate;
        var click = (int)(o.ClickS * rate);
        var tail = (int)(o.SquelchS * rate);
        var output = new float[click + input.Samples.Length + click + tail];
        var random = new Random(seed);

        var hp1 = Biquad.HighPass(o.LowHz, rate);
        var hp2 = Biquad.HighPass(o.LowHz, rate);
        var lp1 = Biquad.LowPass(o.HighHz, rate);
        var lp2 = Biquad.LowPass(o.HighHz, rate);
        var norm = (float)Math.Tanh(o.Drive);

        // Clic al apretar: un golpe corto de ruido que decae.
        for (var i = 0; i < click; i++)
            output[i] = Noise(random) * 0.35f * (1 - i / (float)click);
        for (var i = 0; i < input.Samples.Length; i++)
        {
            var x = input.Samples[i];
            x = lp2.Process(lp1.Process(hp2.Process(hp1.Process(x))));
            x = (float)Math.Tanh(x * o.Drive) / norm; // compresión suave
            output[click + i] = x * 0.9f + Noise(random) * (float)o.NoiseLevel;
        }
        // Clic al soltar y la cola de squelch (ruido que se apaga).
        var end = click + input.Samples.Length;
        for (var i = 0; i < click; i++)
            output[end + i] = Noise(random) * 0.3f * (1 - i / (float)click);
        for (var i = 0; i < tail; i++)
            output[end + click + i] = Noise(random) * 0.08f * (1 - i / (float)tail);
        return new AudioClip(output, rate);
    }

    private static float Noise(Random r) => (float)(r.NextDouble() * 2 - 1);

    /// <summary>Filtro de segundo orden (fórmulas de R. Bristow-Johnson, "Audio EQ Cookbook"), Q = 1/√2.</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;

        public static Biquad LowPass(double hz, int rate) => Make(hz, rate, low: true);

        public static Biquad HighPass(double hz, int rate) => Make(hz, rate, low: false);

        private static Biquad Make(double hz, int rate, bool low)
        {
            var w0 = 2 * Math.PI * Math.Min(hz, rate * 0.45) / rate;
            var alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha;
            return low
                ? new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0)
                : new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public float Process(float x)
        {
            var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return (float)y;
        }
    }
}

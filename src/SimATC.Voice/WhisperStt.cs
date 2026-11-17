using System.Diagnostics;
using System.Text;
using Whisper.net;

namespace SimATC.Voice;

/// <summary>Lo que entendió el reconocedor: el texto, qué tan seguro está (0 a 1) y cuánto tardó.</summary>
public sealed record SttResult(string Text, double Probability, double Seconds);

/// <summary>
/// Reconocimiento de voz local (etapa 5c, ADR-126): whisper.cpp por Whisper.net, en la CPU (RNF-02b: sin VRAM). El modelo
/// se carga la primera vez que se usa y se queda en memoria; cada transcripción corre en un hilo de trabajo, con el idioma
/// del ATC y las frases esperadas como contexto.
/// </summary>
public sealed class WhisperStt(string modelPath) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    /// <summary>Hilos por transcripción: la mitad de los núcleos, entre 2 y 4 (una ráfaga corta, para no robarle al simulador).</summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    public bool Loaded => _factory is not null;

    /// <summary>
    /// Transcribe <paramref name="samples"/> (16 kHz mono, de -1 a 1). <paramref name="language"/>: "en" o "es".
    /// <paramref name="prompt"/>: las frases esperadas, para que acierte con los nombres y la fraseología.
    /// <paramref name="quick"/>: vista previa mientras se habla (ADR-127): el codificador mira solo el largo del audio en vez
    /// de los 30 s de siempre, varias veces más rápido y algo menos preciso; la transcripción final va sin esto.
    /// </summary>
    public async Task<SttResult> TranscribeAsync(float[] samples, string language, string? prompt, CancellationToken ct, bool quick = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var watch = Stopwatch.StartNew();
                _factory ??= WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
                var builder = _factory.CreateBuilder().WithLanguage(language).WithThreads(Threads).WithNoContext().WithSingleSegment()
                    .WithProbabilities();
                if (!string.IsNullOrWhiteSpace(prompt))
                    builder = builder.WithPrompt(prompt);
                if (quick)
                    builder = builder.WithAudioContextSize(QuickAudioContext(samples.Length));
                await using var processor = builder.Build();
                // whisper.cpp necesita al menos un segundo de audio: lo corto se completa con silencio.
                var audio = samples;
                if (audio.Length < MicCapture.SampleRate * 12 / 10)
                {
                    audio = new float[MicCapture.SampleRate * 12 / 10];
                    samples.CopyTo(audio, 0);
                }
                var text = new StringBuilder();
                double probability = 0;
                var segments = 0;
                await foreach (var segment in processor.ProcessAsync(audio, ct).ConfigureAwait(false))
                {
                    text.Append(segment.Text);
                    probability += segment.Probability;
                    segments++;
                }
                return new SttResult(Clean(text.ToString()), segments > 0 ? probability / segments : 0, watch.Elapsed.TotalSeconds);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Contexto de audio para la vista previa: whisper usa 1500 posiciones para 30 s (50 por segundo); aquí las del audio
    /// más un margen, nunca menos de 384 (con menos alucina) ni más de 1500.
    /// </summary>
    public static int QuickAudioContext(int sampleCount) =>
        Math.Clamp((int)Math.Ceiling(sampleCount / (double)MicCapture.SampleRate * 50) + 128, 384, 1500);

    /// <summary>Sin las marcas que whisper pone cuando no oye voz ("[BLANK_AUDIO]", "(música)") ni espacios de sobra.</summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var c in text)
        {
            if (c is '[' or '(')
                depth++;
            else if (c is ']' or ')')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0)
                sb.Append(c);
        }
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }
}

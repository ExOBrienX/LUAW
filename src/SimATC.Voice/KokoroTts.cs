using System.Diagnostics;
using System.Globalization;
using System.Text;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.Voice;

/// <summary>
/// Kokoro-82M para la voz del ATC (bloque C, ADR-103), opcional: un proceso persistente (<c>SimATC.KokoroHost.exe</c>)
/// que carga el modelo una vez (~550 MB de RAM mientras está activo) y genera cada frase en 2 a 4 s con 2 hilos y prioridad
/// baja (medido en el PC de Marco, ADR-103). Las frases van de a una; las repetidas salen de la caché. Si el proceso falla
/// se vuelve a abrir en la frase siguiente; mientras tanto, quien lo usa sigue con Piper.
/// </summary>
public sealed class KokoroTts(string hostExe, string kokoroDir, string piperDir, Action<string>? log = null, int cacheSize = 100)
    : IDisposable
{
    public const int SampleRate = 24000;

    /// <summary>Más que esto (s) por frase es un error; la primera incluye cargar el modelo.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Hilos de inferencia (ADR-103: 2 = ~12 % del PC durante la frase).</summary>
    public const int Threads = 2;

    /// <summary>Voces del ATC por idioma (kokoro-onnx v1.0): masculinas y femeninas, se reparten por dependencia.</summary>
    public static readonly IReadOnlyList<string> EnglishVoices = ["am_michael", "bm_george", "bf_emma", "am_adam", "bm_lewis", "af_sarah"];

    public static readonly IReadOnlyList<string> SpanishVoices = ["em_alex", "ef_dora", "em_santa"];

    private readonly AudioCache _cache = new(cacheSize);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _in;
    private BinaryReader? _out;

    public bool Available => File.Exists(hostExe) && KokoroInstaller.IsInstalled(Path.GetDirectoryName(kokoroDir)!)
                             && File.Exists(Path.Combine(piperDir, "espeak-ng.dll"));

    /// <summary>La voz de Kokoro para una dependencia (siempre la misma para el mismo nombre).</summary>
    public static string VoiceFor(string station, AtcLanguage language)
    {
        var list = language == AtcLanguage.Spanish ? SpanishVoices : EnglishVoices;
        var h = 2166136261u;
        foreach (var c in station)
        {
            h ^= c;
            h *= 16777619u;
        }
        return list[(int)(h % (uint)list.Count)];
    }

    /// <summary>espeak-ng: inglés de EE. UU. y español latinoamericano (es-419, con seseo; ADR-053).</summary>
    public static string EspeakVoice(AtcLanguage language) => language == AtcLanguage.Spanish ? "es-419" : "en-us";

    public async Task<AudioClip?> SynthesizeAsync(string text, string voice, AtcLanguage language, double speed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || !Available)
            return null;
        var key = $"k|{voice}|{speed:0.00}|{text}";
        if (_cache.TryGet(key, out var cached))
            return cached;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var clip = await Task.Run(() => Request(text, voice, language, speed), ct).WaitAsync(Timeout, ct).ConfigureAwait(false);
            if (clip is not null)
                _cache.Add(key, clip);
            return clip;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or EndOfStreamException)
        {
            log?.Invoke(Tr.F("Voz Kokoro: {0}; se reinicia en la frase siguiente.", ex.Message));
            Stop();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private AudioClip? Request(string text, string voice, AtcLanguage language, double speed)
    {
        Start();
        var line = string.Join('\t', voice, EspeakVoice(language), speed.ToString("0.00", CultureInfo.InvariantCulture),
            text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
        _in!.WriteLine(line);
        _in.Flush();
        var n = _out!.ReadInt32();
        if (n < 0)
            return null;
        var samples = new float[n];
        var bytes = _out.ReadBytes(n * 2);
        if (bytes.Length < n * 2)
            throw new EndOfStreamException(Tr.T("el proceso de Kokoro se cerró"));
        for (var i = 0; i < n; i++)
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return new AudioClip(samples, SampleRate);
    }

    private void Start()
    {
        if (_process is { HasExited: false })
            return;
        Stop();
        var psi = new ProcessStartInfo(hostExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(kokoroDir);
        psi.ArgumentList.Add(piperDir);
        psi.ArgumentList.Add(Threads.ToString(CultureInfo.InvariantCulture));
        _process = Process.Start(psi) ?? throw new InvalidOperationException(Tr.T("no se pudo abrir el proceso de Kokoro"));
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log?.Invoke(e.Data);
        };
        _process.BeginErrorReadLine();
        _in = _process.StandardInput;
        _out = new BinaryReader(_process.StandardOutput.BaseStream);
        log?.Invoke(Tr.T("Voz Kokoro: proceso abierto (carga el modelo en la primera frase)."));
    }

    /// <summary>Cierra el proceso (al desactivar Kokoro o al salir): libera su RAM.</summary>
    public void Stop()
    {
        try
        {
            _in?.Close();
            if (_process is { HasExited: false } p && !p.WaitForExit(2000))
                p.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // ya estaba cerrado
        }
        _process?.Dispose();
        _process = null;
        _in = null;
        _out = null;
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}

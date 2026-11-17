using System.Diagnostics;

using SimATC.Core.Text;

namespace SimATC.Voice;

/// <summary>Convierte texto en voz.</summary>
public interface ITextToSpeech
{
    /// <summary>La voz de <paramref name="text"/> (ya en su forma hablada, §8.2), o null si no se pudo.</summary>
    Task<AudioClip?> SynthesizeAsync(string text, VoiceChoice voice, double speed, CancellationToken ct);
}

/// <summary>
/// Piper (TTS neuronal local en CPU, ADR-038) como <b>proceso aparte</b> (<c>piper.exe</c>; su licencia GPL no se enlaza
/// con la app): un proceso por frase, con el texto por la entrada estándar y el audio crudo (PCM de 16 bits) por la
/// salida (<c>--output_raw</c>). Las frases repetidas salen de una caché en memoria (por texto + voz + velocidad).
/// </summary>
public sealed class PiperTts(string piperExe, Action<string>? log = null, int cacheSize = 200) : ITextToSpeech
{
    /// <summary>Más que esto (s) generando una frase es un error (Piper cuelga o la frase es enorme).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly AudioCache _cache = new(cacheSize);

    public bool Available => File.Exists(piperExe);

    public async Task<AudioClip?> SynthesizeAsync(string text, VoiceChoice voice, double speed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || !Available)
            return null;
        var key = $"{voice.Key}|{speed:0.00}|{text}";
        if (_cache.TryGet(key, out var cached))
            return cached;

        var psi = new ProcessStartInfo(piperExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(piperExe)!,
        };
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(voice.Voice.ModelPath);
        psi.ArgumentList.Add("--output_raw");
        psi.ArgumentList.Add("--length_scale");
        psi.ArgumentList.Add((1 / Math.Clamp(speed, 0.5, 2.0)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (voice.Voice.Speakers > 1)
        {
            psi.ArgumentList.Add("--speaker");
            psi.ArgumentList.Add(voice.Speaker.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar Piper.");
            p.PriorityClass = ProcessPriorityClass.BelowNormal; // que no le quite CPU al simulador
            await p.StandardInput.WriteLineAsync(text.AsMemory(), timeout.Token);
            p.StandardInput.Close();
            using var audio = new MemoryStream();
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.StandardOutput.BaseStream.CopyToAsync(audio, timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0 || audio.Length == 0)
            {
                log?.Invoke(Tr.F("Piper terminó con código {0}: {1}", p.ExitCode, (await stderr).Trim()));
                return null;
            }
            var clip = AudioClip.FromPcm16(audio.GetBuffer().AsSpan(0, (int)audio.Length), voice.Voice.SampleRate);
            _cache.Add(key, clip);
            return clip;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log?.Invoke(Tr.F("Piper tardó más de {0:0} s con «{1}»; se omite la voz.", Timeout.TotalSeconds, text));
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke(Tr.F("Piper falló ({0}); se omite la voz.", ex.Message));
            return null;
        }
    }
}

/// <summary>Caché LRU de audio generado: las frases repetidas ("roger", "standby") no se vuelven a generar.</summary>
public sealed class AudioCache(int capacity)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, AudioClip Clip)>> _map = [];
    private readonly LinkedList<(string Key, AudioClip Clip)> _order = new();
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
                return _map.Count;
        }
    }

    public bool TryGet(string key, out AudioClip clip)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                clip = node.Value.Clip;
                return true;
            }
        }
        clip = null!;
        return false;
    }

    public void Add(string key, AudioClip clip)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
                _order.Remove(existing);
            var node = _order.AddFirst((key, clip));
            _map[key] = node;
            while (_map.Count > capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}

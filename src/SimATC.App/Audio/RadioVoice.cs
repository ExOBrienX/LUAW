using SimATC.Core.Comms;
using SimATC.Voice;

using SimATC.Core.Text;

namespace SimATC.App.Audio;

/// <summary>
/// La voz de la radio en la app (etapa 5b, RF-35 a RF-38b): genera la frase con Piper, le pone el efecto de radio y la
/// reproduce. Hay una cola por frecuencia (y una aparte para el interfono del personal de tierra): en una misma
/// frecuencia nunca suenan dos voces a la vez; en frecuencias distintas sí, como en la cabina. La frase se genera
/// mientras espera su turno, para que la siguiente empiece apenas termina la anterior.
/// </summary>
public sealed class RadioVoice(Action<string> log)
{
    /// <summary>"Frecuencia" del interfono con el personal de tierra (no es radio).</summary>
    public const int Interphone = 0;

    /// <summary>Pausa (s) entre una transmisión y la siguiente en la misma frecuencia.</summary>
    public const double GapS = 0.4;

    private readonly AudioPlayer _player = new();
    private readonly Dictionary<int, Task> _queues = [];
    private readonly Lock _lock = new();
    private PiperTts? _tts;
    private KokoroTts? _kokoro;

    /// <summary>Voz Kokoro para el ATC (ADR-103); si no está instalada o falla una frase, esa frase sale con Piper.</summary>
    public bool UseKokoro
    {
        get => _useKokoro;
        set
        {
            _useKokoro = value;
            if (!value)
                _kokoro?.Stop(); // libera su RAM (~550 MB)
        }
    }

    private bool _useKokoro;

    /// <summary>Kokoro está instalado (modelo, voces y el espeak-ng de Piper).</summary>
    public bool KokoroInstalled => _kokoro?.Available == true;
    private VoiceAssigner? _assigner;
    private string _root = "";

    public VoiceCatalog Catalog { get; private set; } = new([]);

    /// <summary>Las voces elegidas en Configuración, por rol (null: automático).</summary>
    public Dictionary<VoiceRole, string?> Preferred { get; } = [];

    /// <summary>Para que la voz no deletree las telefonías (la pone la app con la base de aerolíneas).</summary>
    public Func<string, bool>? IsTelephonyWord { get; set; }

    /// <summary>Velocidad al hablar (1 = normal).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Multiplicador por la tasa de simulación (1 a 1,5; ADR-077): con el tiempo acelerado se habla más rápido.</summary>
    public double RateFactor { get; set; } = 1.0;

    /// <summary>Más que esto esperando en una frecuencia: lo siguiente va solo como texto, para no quedar atrasado.</summary>
    public const int MaxBacklog = 2;

    private readonly Dictionary<int, int> _pending = [];

    /// <summary>Piper y al menos una voz están instalados.</summary>
    public bool Installed => _tts?.Available == true && Catalog.Voices.Count > 0;

    /// <summary>Hay voces del idioma del ATC.</summary>
    public bool ReadyFor(AtcLanguage language) => Installed && Catalog.For(language).Count > 0;

    /// <summary>Lee lo instalado en <paramref name="root"/> (al abrir la app y después de instalar).</summary>
    public void Load(string root)
    {
        _root = root;
        _tts = new PiperTts(VoiceInstaller.PiperExe(root), log);
        _kokoro?.Dispose();
        _kokoro = new KokoroTts(Path.Combine(AppContext.BaseDirectory, "SimATC.KokoroHost.exe"), KokoroInstaller.Folder(root),
            Path.Combine(root, "piper"), log);
        Catalog = VoiceCatalog.Scan(VoiceInstaller.VoicesFolder(root), log);
        _assigner = new VoiceAssigner(Catalog);
        log(Installed ? Tr.F("Voz: Piper y {0} voces instaladas.", Catalog.Voices.Count) : Tr.T("Voz: no instalada (Configuración → Voz)."));
    }

    public string Root => _root;

    /// <summary>
    /// Dice <paramref name="spoken"/> (texto ya en su forma hablada) en la frecuencia <paramref name="khz"/>, con la voz
    /// de <paramref name="who"/>. <paramref name="onStart"/> se llama (en el hilo de la interfaz) cuando empieza a sonar,
    /// con la duración real: ahí se muestra el mensaje en el chat y se ocupa la frecuencia. Devuelve cuando terminó de
    /// sonar (o enseguida, sin voz: entonces <paramref name="onStart"/> se llama igual, sin duración).
    /// </summary>
    public Task SpeakAsync(int khz, VoiceRole role, string who, string spoken, AtcLanguage language, Func<float> volume,
        Action<double?> onStart, bool radioEffect = true)
    {
        if (_assigner is null || _tts is null || !ReadyFor(language))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onStart(null));
            return Task.CompletedTask;
        }
        foreach (var (r, id) in Preferred)
        {
            if (!_assigner.Preferred.TryGetValue(r, out var old) || old != id)
            {
                _assigner.Preferred[r] = id;
                _assigner.Reset();
            }
        }
        var choice = _assigner.For(role, who, language);
        lock (_lock)
        {
            // Con la frecuencia atrasada (simrate alto), solo texto y al instante: el chat no se queda atrás (ADR-077).
            if (_pending.GetValueOrDefault(khz) > MaxBacklog)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => onStart(null));
                return Task.CompletedTask;
            }
            _pending[khz] = _pending.GetValueOrDefault(khz) + 1;
        }
        // Se genera ya (en paralelo con lo que suena), y se reproduce en su turno.
        var text = TtsText.Prepare(spoken, language, IsTelephonyWord);
        var speed = Speed * Math.Clamp(RateFactor, 1, 1.5);
        var kokoro = role == VoiceRole.Atc && _useKokoro && _kokoro is { Available: true } k ? k : null;
        var piper = _tts;
        var synth = Task.Run(async () =>
        {
            // ATC con Kokoro (ADR-103); si no se pudo, la misma frase con Piper.
            if (kokoro is not null && await kokoro.SynthesizeAsync(text, KokoroTts.VoiceFor(who, language), language, speed,
                    CancellationToken.None) is { } natural)
                return natural;
            return choice is { } c ? (await piper.SynthesizeAsync(text, c, speed, CancellationToken.None))?.WithPitch(c.Pitch) : null;
        });
        lock (_lock)
        {
            var previous = _queues.TryGetValue(khz, out var p) ? p : Task.CompletedTask;
            var next = PlayInTurnAsync(previous, synth, who, volume, onStart, radioEffect)
                .ContinueWith(_ =>
                {
                    lock (_lock)
                        _pending[khz] = Math.Max(0, _pending.GetValueOrDefault(khz) - 1);
                }, TaskScheduler.Default);
            _queues[khz] = next;
            return next;
        }
    }

    private async Task PlayInTurnAsync(Task previous, Task<AudioClip?> synth, string who, Func<float> volume, Action<double?> onStart,
        bool radioEffect)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log(Tr.F("Voz: error en la transmisión anterior ({0}).", ex.Message));
        }
        AudioClip? clip = null;
        try
        {
            clip = await synth.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log(Tr.F("Voz: no se pudo generar ({0}).", ex.Message));
        }
        if (clip is not null && radioEffect)
            clip = RadioFilter.Apply(clip, seed: (int)VoiceAssignerHash(who));
        var duration = clip?.DurationS;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => onStart(duration));
        if (clip is null)
            return;
        try
        {
            await _player.PlayAsync(clip, volume(), CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(GapS)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log(Tr.F("Voz: no se pudo reproducir ({0}).", ex.Message));
        }
    }

    private static uint VoiceAssignerHash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s)
        {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }

    /// <summary>Demo de Kokoro (Configuración): una frase con la voz que le tocaría a esa dependencia.</summary>
    public async Task<bool> DemoKokoroAsync(string station, string spoken, AtcLanguage language, float volume)
    {
        if (_kokoro is not { Available: true } k)
            return false;
        var clip = await k.SynthesizeAsync(TtsText.Prepare(spoken, language, IsTelephonyWord), KokoroTts.VoiceFor(station, language),
            language, Speed, CancellationToken.None);
        if (clip is null)
            return false;
        await _player.PlayAsync(RadioFilter.Apply(clip), volume, CancellationToken.None);
        return true;
    }

    /// <summary>Para la demo de Configuración: una frase con una voz concreta (sin cola), con efecto de radio.</summary>
    public async Task DemoAsync(string voiceId, string spoken, float volume)
    {
        if (_tts is null || Catalog.Find(voiceId) is not { } v)
            return;
        var clip = await _tts.SynthesizeAsync(TtsText.Prepare(spoken, v.Language, IsTelephonyWord), new VoiceChoice(v, 0), Speed, CancellationToken.None);
        if (clip is not null)
            await _player.PlayAsync(RadioFilter.Apply(clip), volume, CancellationToken.None);
    }
}

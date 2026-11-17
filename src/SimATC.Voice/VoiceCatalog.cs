using System.Text.Json;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.Voice;

/// <summary>Una voz de Piper instalada: el modelo (.onnx), su idioma ("es_ES"), frecuencia y cuántos hablantes trae.</summary>
public sealed record PiperVoice(string Id, string ModelPath, string LanguageCode, int SampleRate, int Speakers)
{
    /// <summary>El idioma del ATC al que sirve ("es_MX" → español).</summary>
    public AtcLanguage Language => LanguageCode.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? AtcLanguage.Spanish : AtcLanguage.English;
}

/// <summary>
/// Una voz concreta: el modelo, el hablante (si trae varios) y un tono (1 = el natural; 0,9 a 1,1 cuando una voz se repite,
/// para que no suene igual que otra).
/// </summary>
public readonly record struct VoiceChoice(PiperVoice Voice, int Speaker, double Pitch = 1.0)
{
    /// <summary>La voz sin el tono (lo que genera Piper; el tono se aplica después).</summary>
    public string Key => $"{Voice.Id}#{Speaker}";
}

/// <summary>
/// Las voces instaladas en una carpeta (cada una, <c>nombre.onnx</c> con su <c>nombre.onnx.json</c> de Piper, de donde se
/// leen el idioma, la frecuencia de muestreo y los hablantes). Las que no se pueden leer se saltan.
/// </summary>
public sealed class VoiceCatalog
{
    public IReadOnlyList<PiperVoice> Voices { get; }

    public VoiceCatalog(IReadOnlyList<PiperVoice> voices) => Voices = voices;

    public static VoiceCatalog Scan(string folder, Action<string>? log = null)
    {
        var voices = new List<PiperVoice>();
        if (!Directory.Exists(folder))
            return new VoiceCatalog(voices);
        foreach (var model in Directory.EnumerateFiles(folder, "*.onnx", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var config = model + ".json";
            if (!File.Exists(config))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(config));
                var root = doc.RootElement;
                var rate = root.GetProperty("audio").GetProperty("sample_rate").GetInt32();
                var lang = root.TryGetProperty("language", out var l) && l.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var speakers = root.TryGetProperty("num_speakers", out var n) ? Math.Max(1, n.GetInt32()) : 1;
                voices.Add(new PiperVoice(Path.GetFileNameWithoutExtension(model), model, lang, rate, speakers));
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                log?.Invoke(Tr.F("Voz {0}: no se pudo leer su configuración ({1}).", Path.GetFileName(model), ex.Message));
            }
        }
        return new VoiceCatalog(voices);
    }

    public IReadOnlyList<PiperVoice> For(AtcLanguage language) => [.. Voices.Where(v => v.Language == language)];

    public PiperVoice? Find(string id) => Voices.FirstOrDefault(v => v.Id == id);
}

/// <summary>Quién habla, para asignarle una voz.</summary>
public enum VoiceRole
{
    /// <summary>Una dependencia del ATC (Delivery, Ground, Tower…).</summary>
    Atc,
    /// <summary>Un piloto IA.</summary>
    Pilot,
    /// <summary>El copiloto del piloto.</summary>
    Copilot,
    /// <summary>El personal de tierra del pushback.</summary>
    GroundCrew,
}

/// <summary>
/// Una voz para cada quien (RF-35), estable y sin repetirse (prueba de Marco, 2026-11-01: el ATC, los pilotos y el
/// copiloto a veces sonaban igual):
/// <list type="bullet">
/// <item>cada dependencia del ATC y el copiloto tienen una voz propia, que nadie más usa (primero las de un solo hablante,
///   que son las más claras);</item>
/// <item>los pilotos IA usan las voces que quedan (en los modelos de muchos hablantes, cada uno es una voz);</item>
/// <item>si no alcanzan (en español hay 5), un piloto repite una voz pero con otro tono (±6 a 10 %), así nunca suena
///   igual que el ATC ni que el copiloto.</item>
/// </list>
/// La misma dependencia o el mismo avión suenan siempre igual: el reparto se hace en orden de aparición, con un hash estable
/// (FNV-1a) para elegir. La voz elegida en Configuración para un rol manda.
/// </summary>
public sealed class VoiceAssigner(VoiceCatalog catalog)
{
    /// <summary>Hablantes de un modelo de muchos que se usan como voces distintas (de sobra para un aeropuerto).</summary>
    public const int MaxSpeakersPerModel = 60;

    private static readonly double[] PitchVariants = [0.9, 0.94, 1.06, 1.1];

    /// <summary>Voces elegidas en Configuración, por rol (id del modelo, o null para automático).</summary>
    public Dictionary<VoiceRole, string?> Preferred { get; } = [];

    private readonly Dictionary<(VoiceRole, string, AtcLanguage), VoiceChoice> _assigned = [];
    private readonly HashSet<(string, AtcLanguage)> _reserved = []; // voces del ATC y del copiloto

    public VoiceChoice? For(VoiceRole role, string who, AtcLanguage language)
    {
        if (_assigned.TryGetValue((role, who, language), out var known))
            return known;
        var slots = Slots(language);
        if (slots.Count == 0)
            return null;
        var h = StableHash($"{role}:{who}");
        VoiceChoice choice;
        if (Preferred.TryGetValue(role, out var id) && id is not null && catalog.Find(id) is { } fixedVoice && fixedVoice.Language == language)
        {
            choice = new VoiceChoice(fixedVoice, (int)(h % (uint)Math.Min(fixedVoice.Speakers, MaxSpeakersPerModel)));
        }
        else if (role is VoiceRole.Atc or VoiceRole.Copilot or VoiceRole.GroundCrew)
        {
            // Una voz propia: la primera libre. El personal de tierra usa la del copiloto con otro tono (por el interfono).
            if (role == VoiceRole.GroundCrew && _assigned.TryGetValue((VoiceRole.Copilot, "copilot", language), out var cp))
                choice = cp with { Pitch = 0.92 };
            else
            {
                var free = slots.FirstOrDefault(s => !_reserved.Contains((s.Key, language)));
                choice = free.Voice is not null ? free : slots[(int)(h % (uint)slots.Count)] with { Pitch = PitchVariants[(int)(h % 4)] };
                _reserved.Add((choice.Key, language));
            }
        }
        else
        {
            // Pilotos: las voces libres; si no quedan, una repetida con otro tono.
            var free = slots.Where(s => !_reserved.Contains((s.Key, language))).ToList();
            choice = free.Count > 0
                ? free[(int)(h % (uint)free.Count)]
                : slots[(int)(h % (uint)slots.Count)] with { Pitch = PitchVariants[(int)(StableHash(who) % 4)] };
        }
        _assigned[(role, who, language)] = choice;
        return choice;
    }

    /// <summary>Todas las voces del idioma como lista: primero los modelos de un hablante, después los hablantes de los demás.</summary>
    private List<VoiceChoice> Slots(AtcLanguage language)
    {
        var voices = catalog.For(language);
        var list = new List<VoiceChoice>();
        foreach (var v in voices.Where(v => v.Speakers == 1))
            list.Add(new VoiceChoice(v, 0));
        foreach (var v in voices.Where(v => v.Speakers > 1))
        {
            for (var k = 0; k < Math.Min(v.Speakers, MaxSpeakersPerModel); k++)
                list.Add(new VoiceChoice(v, k));
        }
        return list;
    }

    /// <summary>Cambió una preferencia de Configuración: se vuelve a repartir.</summary>
    public void Reset()
    {
        _assigned.Clear();
        _reserved.Clear();
    }

    /// <summary>FNV-1a de 32 bits: estable entre ejecuciones (string.GetHashCode no lo es).</summary>
    internal static uint StableHash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s)
        {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }
}

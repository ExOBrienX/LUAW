using System.Text.Json;
using System.Text.Json.Serialization;
using SimATC.Core.Comms;
using SimATC.Core.Traffic;

using SimATC.Core.Text;

namespace SimATC.Core;

/// <summary>Modo de comunicación del piloto (RF-30).</summary>
public enum CommsMode
{
    Text,
    Voice,
    Copilot,
}

/// <summary>Nivel de exigencia de las colaciones (RF-33b).</summary>
public enum Strictness
{
    Permissive,
    Realistic,
}

/// <summary>
/// Preferencias del usuario (RF-41), guardadas en <c>%LOCALAPPDATA%\LUAW\settings.json</c>. Si el archivo no existe o
/// está dañado se usan los valores por defecto (y se avisa por el log).
/// </summary>
public sealed record AppSettings
{
    public CommsMode Mode { get; init; } = CommsMode.Text;
    public Strictness Strictness { get; init; } = Strictness.Realistic;
    /// <summary>El copiloto sintoniza COM1 cuando el ATC ordena un cambio (RF-34b, V-17).</summary>
    public bool CopilotTunesCom1 { get; init; } = true;
    /// <summary>En modo Texto o Voz, qué colaciona el copiloto (RF-30: los modos se mezclan). En modo Copiloto, todo.</summary>
    public CopilotReadbacks CopilotReadbacks { get; init; } = CopilotReadbacks.Never;
    /// <summary>En modo Texto o Voz, el copiloto hace los llamados de rutina (autorización, pushback, rodaje, listo, Approach).</summary>
    public bool CopilotRoutineCalls { get; init; }
    /// <summary>En modo Texto o Voz, el copiloto contesta si el piloto no colacionó al "how do you read" (RF-33c).</summary>
    public bool CopilotRescue { get; init; }
    /// <summary>El copiloto pregunta antes de pedir cada cosa (PLAN-PULIDO bloque 1): por defecto solo autorización y pushback.</summary>
    public bool CopilotAskClearance { get; init; } = true;
    public bool CopilotAskPushback { get; init; } = true;
    public bool CopilotAskTaxi { get; init; }
    public bool CopilotAskReady { get; init; }

    /// <summary>Lo que hace el copiloto con estas preferencias: en modo Copiloto, todo; si no, lo elegido (por defecto, nada).</summary>
    [JsonIgnore]
    public CopilotSettings Copilot => Mode == CommsMode.Copilot
        ? new CopilotSettings(Comms.CopilotReadbacks.Always, true, true, CopilotTunesCom1, CopilotAskClearance, CopilotAskPushback, CopilotAskTaxi, CopilotAskReady)
        : new CopilotSettings(CopilotReadbacks, CopilotRoutineCalls, CopilotRescue, CopilotTunesCom1, CopilotAskClearance, CopilotAskPushback, CopilotAskTaxi,
            CopilotAskReady);
    /// <summary>Los botones "Sintonizar" ponen la frecuencia en la standby de COM1 (el piloto la cambia) en vez de la activa.</summary>
    public bool TuneToStandby { get; init; }
    /// <summary>A qué radio van los botones "Sintonizar" (1 o 2). El ATIS va a COM2 si existe.</summary>
    public int TuneRadio { get; init; } = 1;
    /// <summary>Segundos para colacionar antes de que el ATC llame (RF-33c), en modo texto; en voz, la mitad.</summary>
    public int ReadbackTimeS { get; init; } = 45;
    /// <summary>
    /// El ATC le da al piloto la pista de salida de su OFP (SimBrief ya consideró NOTAMs, performance y viento real) si
    /// existe y el viento de cola no pasa el límite; si no, la pista en uso (ADR-081).
    /// </summary>
    public bool UsePlanRunways { get; init; } = true;
    /// <summary>Nivel de tráfico (ADR-109): cuántos estacionados por tipo de puesto, cuántos en el aire y la frecuencia.</summary>
    public Traffic.TrafficLevel TrafficLevel { get; init; } = Traffic.TrafficLevel.Average;
    /// <summary>Nivel personalizado: cuántos aviones de aerolínea estacionar.</summary>
    public int CustomParked { get; init; } = 40;
    /// <summary>Vigilancia de la aproximación del piloto (ADR-108): completa, solo avisos o apagada.</summary>
    public Comms.ApproachWatchLevel ApproachWatch { get; init; } = Comms.ApproachWatchLevel.Full;
    public int MaxGround { get; init; } = TrafficLimits.Default.MaxGround;
    public int MaxAir { get; init; } = TrafficLimits.Default.MaxAir;
    public int MaxTotal { get; init; } = TrafficLimits.Default.MaxTotal;
    /// <summary>FPS máximos del radar (RF-42b).</summary>
    public int RadarFps { get; init; } = 20;
    public string SimBriefUser { get; init; } = "";
    /// <summary>Carpetas con paquetes de tráfico (FSLTL, AIG) además de las Community que se detectan solas.</summary>
    public IReadOnlyList<string> ExtraModelFolders { get; init; } = [];
    /// <summary>Último aeropuerto cargado: se usa al abrir la app si el piloto no está en ninguno (ADR-030).</summary>
    public string? LastAirport { get; init; }
    /// <summary>Idioma del ATC (§8.6): inglés OACI o español. El ATC, los IA y el copiloto hablan en ese idioma.</summary>
    public AtcLanguage AtcLanguage { get; init; } = AtcLanguage.English;
    /// <summary>Idioma de la interfaz: "es", "en" o null (el de Windows). Se aplica al abrir la app.</summary>
    public string? UiLanguage { get; init; }
    /// <summary>Fraseología en inglés (ADR-099): automática (FAA en EE. UU.), OACI o FAA.</summary>
    public PhraseologyStyle Phraseology { get; init; } = PhraseologyStyle.Auto;
    /// <summary>Voz Kokoro para el ATC (ADR-103): más natural, más CPU y RAM; apagada por defecto. Los IA siguen con Piper.</summary>
    public bool VoiceKokoroAtc { get; init; }
    /// <summary>Voz (etapa 5b): el ATC, los IA, el copiloto y el personal de tierra hablan (si está instalada).</summary>
    public bool VoiceEnabled { get; init; }
    /// <summary>Volumen de la voz (0 a 100).</summary>
    public int VoiceVolume { get; init; } = 80;
    /// <summary>La voz sigue la perilla de volumen de la radio del avión (COM VOLUME).</summary>
    public bool VoiceFollowComVolume { get; init; } = true;
    /// <summary>Velocidad al hablar (1 = normal).</summary>
    public double VoiceSpeed { get; init; } = 1.0;
    /// <summary>Voz elegida por rol (id del modelo de Piper), o null para automática.</summary>
    public string? VoiceAtc { get; init; }
    public string? VoicePilots { get; init; }
    public string? VoiceCopilot { get; init; }
    /// <summary>Hablarle al ATC por el micrófono (etapa 5c, ADR-126): tecla para hablar y reconocimiento local.</summary>
    public bool MicEnabled { get; init; }
    /// <summary>Micrófono elegido (nombre del dispositivo de Windows), o null para el predeterminado.</summary>
    public string? MicDevice { get; init; }
    /// <summary>Ganancia del micrófono (%, 100 = sin cambio; hasta 300).</summary>
    public int MicGain { get; init; } = 100;
    /// <summary>Umbral de voz (0 a 100, en la escala de la barra de nivel): lo que lo supera cuenta como que estás hablando.</summary>
    public int MicThreshold { get; init; } = 35;
    /// <summary>Tecla para hablar (código de tecla virtual de Windows), o 0 si no se eligió.</summary>
    public int MicPttKey { get; init; }
    /// <summary>El micrófono se abre solo cuando hay que colacionar (con un pitido) y se cierra al dejar de hablar.</summary>
    public bool MicAutoListen { get; init; } = true;
    /// <summary>Pitido al abrir y al cerrar el micrófono.</summary>
    public bool MicBeep { get; init; } = true;
    /// <summary>Lo reconocido se envía solo si calza con una frase esperada; si no, queda en el campo para corregirlo.</summary>
    public bool MicAutoSend { get; init; } = true;
    /// <summary>Mientras hablas, lo que se va entendiendo aparece en el campo de texto (reconoce cada ~1 s; usa algo de CPU).</summary>
    public bool MicLivePreview { get; init; } = true;
    /// <summary>Un sonido corto cuando el ATC te habla (los mensajes para ti también se resaltan en el chat).</summary>
    public bool MessageSound { get; init; } = true;
    /// <summary>Panel compacto siempre visible (RF-42c).</summary>
    public bool CompactAlwaysOnTop { get; init; } = true;

    /// <summary>Tráfico automático (etapas 6 y 7): salidas, llegadas y vuelos de la zona sin tocar nada.</summary>
    public bool AutoTraffic { get; init; } = true;
    /// <summary>Densidad del tráfico (% del horario generado, RF-12).</summary>
    public int TrafficDensity { get; init; } = 100;
    /// <summary>Radio (NM) dentro del cual los vuelos se cargan en el simulador (ADR-089, de 20 a 100).</summary>
    public int InjectRadiusNm { get; init; } = 60;

    /// <summary>Igualdad por valor, incluida la lista de carpetas (un record compararía la lista por referencia).</summary>
    public bool Equals(AppSettings? other) =>
        other is not null && Mode == other.Mode && Strictness == other.Strictness && CopilotTunesCom1 == other.CopilotTunesCom1
        && TuneToStandby == other.TuneToStandby && TuneRadio == other.TuneRadio && ReadbackTimeS == other.ReadbackTimeS
        && CopilotReadbacks == other.CopilotReadbacks && CopilotRoutineCalls == other.CopilotRoutineCalls && CopilotRescue == other.CopilotRescue
        && CopilotAskClearance == other.CopilotAskClearance && CopilotAskPushback == other.CopilotAskPushback
        && CopilotAskTaxi == other.CopilotAskTaxi && CopilotAskReady == other.CopilotAskReady
        && MaxGround == other.MaxGround && MaxAir == other.MaxAir && MaxTotal == other.MaxTotal && RadarFps == other.RadarFps
        && SimBriefUser == other.SimBriefUser && ExtraModelFolders.SequenceEqual(other.ExtraModelFolders)
        && LastAirport == other.LastAirport && CompactAlwaysOnTop == other.CompactAlwaysOnTop && MessageSound == other.MessageSound && AtcLanguage == other.AtcLanguage && Phraseology == other.Phraseology && VoiceKokoroAtc == other.VoiceKokoroAtc
        && VoiceEnabled == other.VoiceEnabled && VoiceVolume == other.VoiceVolume && VoiceFollowComVolume == other.VoiceFollowComVolume
        && VoiceSpeed.Equals(other.VoiceSpeed) && VoiceAtc == other.VoiceAtc && VoicePilots == other.VoicePilots && VoiceCopilot == other.VoiceCopilot
        && AutoTraffic == other.AutoTraffic && TrafficDensity == other.TrafficDensity && UsePlanRunways == other.UsePlanRunways
        && ApproachWatch == other.ApproachWatch && TrafficLevel == other.TrafficLevel && CustomParked == other.CustomParked
        && InjectRadiusNm == other.InjectRadiusNm
        && MicEnabled == other.MicEnabled && MicDevice == other.MicDevice && MicGain == other.MicGain && MicThreshold == other.MicThreshold
        && MicPttKey == other.MicPttKey && MicAutoListen == other.MicAutoListen && MicBeep == other.MicBeep && MicAutoSend == other.MicAutoSend
        && MicLivePreview == other.MicLivePreview && UiLanguage == other.UiLanguage;

    public override int GetHashCode() => HashCode.Combine(Mode, Strictness, MaxGround, RadarFps, SimBriefUser, LastAirport, ExtraModelFolders.Count);

    [JsonIgnore]
    public TrafficLimits Limits => new(MaxGround, MaxAir, MaxTotal);

    public static string DefaultPath { get; } = Path.Combine(AppPaths.Root, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path, Action<string>? log = null)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke(Tr.F("Configuración ilegible ({0}); se usan los valores por defecto.", ex.Message));
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}

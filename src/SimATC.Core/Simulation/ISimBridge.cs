using SimATC.Core.Text;
using SimATC.Core.Airports;

namespace SimATC.Core.Simulation;

/// <summary>
/// Contrato entre el motor y el simulador (REQUIREMENTS §5.1). Crece por etapas: la 0 trajo conexión y avión del
/// piloto, la 1 la carga de aeropuertos, la 2 la lista de modelos y los aviones estacionados (ADR-010, ADR-016).
/// </summary>
/// <remarks>Los eventos pueden dispararse desde un hilo propio del puente, no desde el hilo que llamó a Start.</remarks>
public interface ISimBridge : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Versión del simulador conectado (para invalidar cachés), o null si no hay conexión.</summary>
    string? SimVersion { get; }

    /// <summary>Aviones creados por este cliente que siguen en el simulador.</summary>
    int InjectedCount { get; }

    /// <summary>Cambió el estado de la conexión (true = conectado).</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>Nuevo estado del avión del piloto (~5 Hz).</summary>
    event Action<UserAircraftState>? UserStateUpdated;

    /// <summary>Las radios COM del piloto (con la posición, ~5 veces por segundo).</summary>
    event Action<UserRadios>? UserRadiosUpdated;

    /// <summary>Carga e identidad del avión del piloto (cada ~2 s): para comparar con el OFP.</summary>
    event Action<UserAircraftLoad>? UserLoadUpdated;

    /// <summary>Clima en la posición del piloto (cada ~1 s): viento medio, pista activa y ATIS del aeropuerto donde está (V-18).</summary>
    event Action<Atc.AmbientWeather>? AmbientWeatherUpdated;

    /// <summary>Empieza a conectar (y reconectar) en segundo plano. No bloquea.</summary>
    void Start();

    /// <summary>Aeropuertos del caché del simulador cerca del piloto (para saber en cuál está).</summary>
    Task<IReadOnlyList<NearbyAirport>> RequestNearbyAirportsAsync(CancellationToken ct);

    /// <summary>Engancha el remolque del simulador al avión del piloto (pushback, RF-21c).</summary>
    void AttachPushbackTug();

    /// <summary>
    /// Gira el avión del piloto durante el pushback (°/s; positivo = la nariz en sentido horario). El remolque empuja a
    /// velocidad fija; hay que llamarlo seguido (≈10 veces por segundo) mientras dure el giro.
    /// </summary>
    void SetPushbackYawRate(double degPerSecond);

    /// <summary>Suelta el remolque del avión del piloto.</summary>
    void ReleasePushbackTug();

    /// <summary>Sintoniza la radio COM <paramref name="radio"/> (1-3) del piloto, activa o standby. Sin conexión no hace nada.</summary>
    void TuneCom(int radio, int khz, bool standby);

    /// <summary>Pone el código del transponder del piloto (squawk de 4 dígitos). Sin conexión no hace nada.</summary>
    void SetTransponder(int code);

    /// <summary>Corta la conexión y vuelve a intentar al instante (botón "Reconectar"). Los aviones creados se borran.</summary>
    void Reconnect();

    /// <summary>
    /// Lee un aeropuerto completo desde el simulador (RF-07). Falla con <see cref="InvalidOperationException"/> si no
    /// hay conexión, y con <see cref="AirportNotFoundException"/> si el simulador no lo conoce.
    /// </summary>
    Task<AirportData> LoadAirportAsync(string icao, CancellationToken ct);

    /// <summary>Títulos de los aviones que el simulador puede crear (RF-15).</summary>
    Task<IReadOnlyList<string>> EnumerateAircraftTitlesAsync(CancellationToken ct);

    /// <summary>Crea un avión estacionado sin ATC del simulador (RF-16) y devuelve su ObjectId.</summary>
    Task<uint> SpawnParkedAsync(ParkedSpawnRequest request, CancellationToken ct);

    /// <summary>
    /// Un fijo (waypoint, VOR o NDB) con sus aerovías, por ICAO y región (ADR-058); null si el simulador no lo conoce o no
    /// hay conexión.
    /// </summary>
    Task<World.AirwayFix?> RequestWaypointAsync(string icao, string region, CancellationToken ct);

    /// <summary>Como el anterior, con el tipo ('V' VOR, 'N' NDB, 'W' waypoint) para no recibir un homónimo.</summary>
    Task<World.AirwayFix?> RequestWaypointAsync(string icao, string region, char type, CancellationToken ct);

    /// <summary>Todos los fijos del mundo del simulador (waypoints, VOR y NDB; ADR-062). Lento: una vez por semana.</summary>
    Task<IReadOnlyList<World.NavFix>> RequestAllNavaidsAsync(CancellationToken ct);

    /// <summary>Crea un avión en vuelo sin ATC del simulador (etapa 6, §5.3) y devuelve su ObjectId.</summary>
    Task<uint> SpawnAirborneAsync(AirborneSpawnRequest request, CancellationToken ct);

    /// <summary>
    /// Crea un avión "Parked ATC" (V-15): el simulador elige y reserva el puesto y conecta la pasarela. Sin plan de
    /// vuelo nunca lo mueve. Devuelve dónde quedó.
    /// </summary>
    Task<ParkedAtcResult> SpawnParkedAtcAsync(ParkedAtcRequest request, CancellationToken ct);

    /// <summary>
    /// Relevo para salir (V-15): crea un avión sin ATC igual a <paramref name="replacement"/> y, cuando existe, borra el
    /// Parked ATC <paramref name="parkedAtcId"/> (la pasarela se retira sola). Devuelve el ObjectId nuevo.
    /// </summary>
    Task<uint> HandOverAsync(uint parkedAtcId, ParkedSpawnRequest replacement, CancellationToken ct);

    /// <summary>
    /// Remolcador del simulador enganchado al avión: lo crea la primera vez, lo mueve en cada llamada y lo retira con
    /// null (llamar desde <see cref="Frame"/>). Si el simulador no tiene el modelo, no hace nada.
    /// </summary>
    void SetTug(uint aircraftId, TugPose? pose);

    /// <summary>
    /// Un cuadro del simulador (evento Frame), desde el hilo del puente. El motor se ejecuta en este evento: las
    /// llamadas a <see cref="SetPose"/>, <see cref="SetEngines"/> y <see cref="SetLights"/> hechas dentro de él se
    /// aplican en el mismo cuadro (ADR-017).
    /// </summary>
    event Action<FrameInfo>? Frame;

    /// <summary>Mueve un avión creado por este cliente (llamar desde <see cref="Frame"/>).</summary>
    void SetPose(uint objectId, in AircraftPose pose);

    /// <summary>
    /// Escribir también las velocidades (VELOCITY BODY Z, VERTICAL SPEED) de los IA en el aire. Por defecto no (ADR-077):
    /// hipótesis de los tirones adelante-atrás (el simulador los movería con esa velocidad entre escrituras). VERIFICAR V-43.
    /// </summary>
    bool AirborneVelocities { get; set; }

    /// <summary>"Aplicar OFP al avión" (ADR-079): escribe combustible, carga y datos de ATC del avión del piloto; devuelve el resumen.</summary>
    Task<string> ApplyLoadAsync(AircraftLoadRequest request, CancellationToken ct);

    /// <summary>Pide la altura real de un avión (respuesta en <see cref="ObjectAltitude"/>, ADR-017).</summary>
    void RequestAltitude(uint objectId);

    /// <summary>Altura (ft MSL) y AGL (ft) de un avión, desde el hilo del puente.</summary>
    event Action<uint, double, double>? ObjectAltitude;

    /// <summary>
    /// Arranca o apaga los motores (V-09). Arranque y apagado graduales (ENGINE_AUTO_START / ENGINE_AUTO_SHUTDOWN:
    /// N1 sube a ralentí en ~8 s).
    /// </summary>
    void SetEngines(uint objectId, bool running, int engineCount);

    /// <summary>Palanca de gases de todos los motores, en % (0 = ralentí). Cambia el empuje y el sonido.</summary>
    void SetThrottle(uint objectId, double percent, int engineCount);

    /// <summary>Fija las luces exteriores (V-09).</summary>
    void SetLights(uint objectId, LightFlags lights);

    /// <summary>Elimina un avión creado por este cliente.</summary>
    void Remove(uint objectId);

    /// <summary>Elimina todos los aviones creados por este cliente (RF-03, RF-44).</summary>
    void RemoveAll();
}

public sealed class AirportNotFoundException(string icao)
    : Exception(Tr.F("El simulador no devolvió datos para el aeropuerto '{0}'.", icao))
{
    public string Icao { get; } = icao;
}

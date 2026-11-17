using SimATC.Core.Geo;
using SimATC.Core.Airports;

namespace SimATC.Core.Simulation;

/// <summary>
/// Puente falso para correr el motor sin MSFS (Headless y tests). Publica un estado fijo del piloto,
/// que se puede cambiar con <see cref="State"/>, y entrega los aeropuertos que se le pasen.
/// </summary>
public sealed class FakeSimBridge : ISimBridge
{
    public const string FakeSimVersion = "fake";

    private readonly TimeSpan? _interval;
    private readonly IReadOnlyDictionary<string, AirportData> _airports;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _stateLock = new();
    private UserAircraftState _state;
    private Task? _loop;

    /// <param name="initialState">Estado inicial del piloto.</param>
    /// <param name="interval">Cada cuánto publicar el estado; null = solo con <see cref="Emit"/> (tests).</param>
    /// <param name="airports">Aeropuertos disponibles, por ICAO.</param>
    public FakeSimBridge(UserAircraftState initialState, TimeSpan? interval = null,
        IReadOnlyDictionary<string, AirportData>? airports = null)
    {
        _state = initialState;
        _interval = interval;
        _airports = airports ?? new Dictionary<string, AirportData>();
    }

    public bool IsConnected { get; private set; }

    public string? SimVersion => IsConnected ? FakeSimVersion : null;

    /// <summary>Cuántas veces se pidió un aeropuerto (para verificar la caché en tests).</summary>
    public int AirportLoads { get; private set; }

    /// <summary>Títulos que el puente falso dice poder crear.</summary>
    public List<string> AircraftTitles { get; } = [];

    /// <summary>Aviones "creados", por ObjectId.</summary>
    public Dictionary<uint, ParkedSpawnRequest> Spawned { get; } = [];

    private uint _nextObjectId = 1000;

    public int InjectedCount => Spawned.Count;

    public Task<IReadOnlyList<string>> EnumerateAircraftTitlesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(AircraftTitles.ToList());

    public Task<uint> SpawnParkedAsync(ParkedSpawnRequest request, CancellationToken ct)
    {
        if (!IsConnected)
            throw new InvalidOperationException("El puente falso no está iniciado.");
        var id = _nextObjectId++;
        Spawned[id] = request;
        return Task.FromResult(id);
    }

    /// <summary>Fijos con aerovías que conoce el "simulador" (tests); vacío = no hay aerovías.</summary>
    public Dictionary<(string Icao, string Region), World.AirwayFix> Waypoints { get; } = [];

    public Task<World.AirwayFix?> RequestWaypointAsync(string icao, string region, CancellationToken ct) =>
        Task.FromResult(Waypoints.GetValueOrDefault((icao, region)));

    public Task<World.AirwayFix?> RequestWaypointAsync(string icao, string region, char type, CancellationToken ct) =>
        RequestWaypointAsync(icao, region, ct);

    public Task<IReadOnlyList<World.NavFix>> RequestAllNavaidsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<World.NavFix>>([.. Waypoints.Values.Select(w => new World.NavFix(w.Icao, w.Region, w.Position, World.NavKind.Waypoint))]);

    /// <summary>Aviones creados en vuelo (etapa 6).</summary>
    public Dictionary<uint, AirborneSpawnRequest> SpawnedAirborne { get; } = [];

    public Task<uint> SpawnAirborneAsync(AirborneSpawnRequest request, CancellationToken ct)
    {
        if (!IsConnected)
            throw new InvalidOperationException("El puente falso no está iniciado.");
        var id = _nextObjectId++;
        SpawnedAirborne[id] = request;
        return Task.FromResult(id);
    }

    /// <summary>
    /// Dónde deja el "simulador" a un avión Parked ATC (puesto que elige él). null = no se puede crear (como un
    /// simulador que lo rechaza): quien llama debe usar el camino sin ATC.
    /// </summary>
    public Func<ParkedAtcRequest, (GeoPoint Position, double HeadingTrueDeg)?>? ParkedAtcPlacement { get; set; }

    /// <summary>Aviones creados como Parked ATC (ObjectId).</summary>
    public HashSet<uint> ParkedAtc { get; } = [];

    public Task<ParkedAtcResult> SpawnParkedAtcAsync(ParkedAtcRequest request, CancellationToken ct)
    {
        if (ParkedAtcPlacement?.Invoke(request) is not { } place)
            return Task.FromException<ParkedAtcResult>(new InvalidOperationException("Sin puesto Parked ATC."));
        var id = _nextObjectId++;
        Spawned[id] = new ParkedSpawnRequest(request.Title, request.TailNumber, place.Position, 0, place.HeadingTrueDeg, request.EngineCount);
        ParkedAtc.Add(id);
        return Task.FromResult(new ParkedAtcResult(id, place.Position, place.HeadingTrueDeg));
    }

    public async Task<uint> HandOverAsync(uint parkedAtcId, ParkedSpawnRequest replacement, CancellationToken ct)
    {
        var id = await SpawnParkedAsync(replacement, ct);
        ParkedAtc.Remove(parkedAtcId);
        Remove(parkedAtcId);
        return id;
    }

    /// <summary>Remolcador de cada avión (para tests).</summary>
    public Dictionary<uint, TugPose> Tugs { get; } = [];

    public void SetTug(uint aircraftId, TugPose? pose)
    {
        if (pose is { } p)
            Tugs[aircraftId] = p;
        else
            Tugs.Remove(aircraftId);
    }

    public event Action<FrameInfo>? Frame;

    /// <summary>Última pose de cada avión (para tests y Headless).</summary>
    public Dictionary<uint, AircraftPose> Poses { get; } = [];

    public Dictionary<uint, (bool Running, LightFlags Lights)> Visual { get; } = [];

    /// <summary>Simula un cuadro del simulador.</summary>
    public void RaiseFrame(FrameInfo frame) => Frame?.Invoke(frame);

    public void SetPose(uint objectId, in AircraftPose pose) => Poses[objectId] = pose;

    public bool AirborneVelocities { get; set; }

    public AircraftLoadRequest? LastLoad { get; private set; }

    public Task<string> ApplyLoadAsync(AircraftLoadRequest request, CancellationToken ct)
    {
        LastLoad = request;
        return Task.FromResult("aplicado (falso)");
    }

    public event Action<uint, double, double>? ObjectAltitude;

    /// <summary>
    /// Elevación del terreno (ft) para el AGL de <see cref="RequestAltitude"/>. El Headless pone la del aeropuerto: con 0,
    /// en un aeropuerto a nivel del mar el avión aprendía la pista a 0 ft y flotaba hasta el motor y al aire (ADR-093).
    /// </summary>
    public double GroundElevationFt { get; set; }

    /// <summary>Responde al instante con la última pose (en tierra, AGL 0).</summary>
    public void RequestAltitude(uint objectId)
    {
        if (Poses.TryGetValue(objectId, out var p))
            ObjectAltitude?.Invoke(objectId, p.AltitudeFt, p.OnGround ? 0 : Math.Max(0, p.AltitudeFt - GroundElevationFt));
    }

    public void SetEngines(uint objectId, bool running, int engineCount) =>
        Visual[objectId] = (running, Visual.GetValueOrDefault(objectId).Lights);

    /// <summary>Último % de gases de cada avión.</summary>
    public Dictionary<uint, double> Throttle { get; } = [];

    public void SetThrottle(uint objectId, double percent, int engineCount) => Throttle[objectId] = percent;

    public void SetLights(uint objectId, LightFlags lights) =>
        Visual[objectId] = (Visual.GetValueOrDefault(objectId).Running, lights);

    public void Remove(uint objectId)
    {
        Spawned.Remove(objectId);
        Poses.Remove(objectId);
        Tugs.Remove(objectId);
        ParkedAtc.Remove(objectId);
    }

    public void RemoveAll()
    {
        Spawned.Clear();
        Poses.Clear();
        Tugs.Clear();
        ParkedAtc.Clear();
    }

    public event Action<bool>? ConnectionChanged;
    public event Action<UserAircraftState>? UserStateUpdated;

    public event Action<UserAircraftLoad>? UserLoadUpdated;

    public event Action<UserRadios>? UserRadiosUpdated;

    /// <summary>Emite las radios del piloto (tests).</summary>
    public void EmitRadios(UserRadios radios) => UserRadiosUpdated?.Invoke(radios);

    public event Action<Atc.AmbientWeather>? AmbientWeatherUpdated;

    /// <summary>Aeropuertos que devuelve el puente falso (tests).</summary>
    public List<NearbyAirport> Nearby { get; } = [];

    public Task<IReadOnlyList<NearbyAirport>> RequestNearbyAirportsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NearbyAirport>>([.. Nearby]);

    /// <summary>Última sintonía pedida (tests).</summary>
    public (int Radio, int Khz, bool Standby)? LastTune { get; private set; }

    /// <summary>Último squawk pedido (tests).</summary>
    public int? LastTransponder { get; private set; }

    public void TuneCom(int radio, int khz, bool standby) => LastTune = (radio, khz, standby);

    /// <summary>Órdenes al remolque del piloto (tests): "attach", "yaw 1.5", "release".</summary>
    public List<string> TugLog { get; } = [];

    public void AttachPushbackTug() => TugLog.Add("attach");

    public void SetPushbackYawRate(double degPerSecond) =>
        TugLog.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"yaw {degPerSecond:0.0}"));

    public void ReleasePushbackTug() => TugLog.Add("release");

    public void SetTransponder(int code) => LastTransponder = code;

    /// <summary>En el puente falso: se desconecta y vuelve a conectar al instante.</summary>
    public void Reconnect()
    {
        ConnectionChanged?.Invoke(false);
        ConnectionChanged?.Invoke(true);
    }

    /// <summary>Emite clima en la posición del piloto (tests).</summary>
    public void EmitWeather(Atc.AmbientWeather weather) => AmbientWeatherUpdated?.Invoke(weather);

    /// <summary>Emite una carga del avión del piloto (tests).</summary>
    public void EmitLoad(UserAircraftLoad load) => UserLoadUpdated?.Invoke(load);

    public UserAircraftState State
    {
        get { lock (_stateLock) return _state; }
        set { lock (_stateLock) _state = value; }
    }

    public void Start()
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(true);
        if (_interval is { } interval)
            _loop = RunAsync(interval, _cts.Token);
    }

    /// <summary>Publica el estado actual una vez.</summary>
    public void Emit() => UserStateUpdated?.Invoke(State);

    public Task<AirportData> LoadAirportAsync(string icao, CancellationToken ct)
    {
        if (!IsConnected)
            throw new InvalidOperationException("El puente falso no está iniciado.");
        AirportLoads++;
        return _airports.TryGetValue(icao.ToUpperInvariant(), out var airport)
            ? Task.FromResult(airport)
            : Task.FromException<AirportData>(new AirportNotFoundException(icao));
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Emit();
        }
        catch (OperationCanceledException)
        {
            // Cierre normal.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _loop?.GetAwaiter().GetResult();
        _cts.Dispose();
        RemoveAll();
        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
        }
    }
}

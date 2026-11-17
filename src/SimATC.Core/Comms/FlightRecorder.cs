using System.Text.Json;
using System.Text.Json.Serialization;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Units;

namespace SimATC.Core.Comms;

/// <summary>Un vuelo terminado del piloto (historial, ADR-088). Horas en UTC; null si no se registró ese momento.</summary>
public sealed record FlightRecord(
    string Callsign,
    string Aircraft,
    string Origin,
    string Destination,
    string? DepartureRunway,
    string? ArrivalRunway,
    string? ArrivalStand,
    DateTime? OffBlockUtc,
    DateTime? TakeoffUtc,
    DateTime? LandingUtc,
    DateTime OnBlockUtc,
    double DistanceNm,
    double MaxAltitudeFt)
{
    /// <summary>Tiempo de vuelo (despegue a aterrizaje).</summary>
    public TimeSpan? AirTime => TakeoffUtc is { } t && LandingUtc is { } l ? l - t : null;

    /// <summary>Tiempo de calzos (salida del puesto a llegada al puesto).</summary>
    public TimeSpan? BlockTime => OffBlockUtc is { } o ? OnBlockUtc - o : null;
}

/// <summary>
/// Registra el vuelo del piloto a partir de su estado (5 Hz) y la fase del ATC: salida del puesto, despegue, aterrizaje,
/// llegada al puesto, distancia y altitud máxima. El vuelo termina en tierra en el destino (en el puesto, o en cualquier lugar
/// después de aterrizar si no hubo puesto: ADR-089) con los motores apagados (o, si no se sabe de los motores, con el freno de
/// estacionamiento puesto). Lógica pura.
/// </summary>
public sealed class FlightRecorder
{
    private DateTime? _offBlock, _takeoff, _landing;
    private GeoPoint? _last;
    private double _distanceM, _maxAltFt;
    private bool _wasOnGround = true;

    public bool Completed { get; private set; }

    /// <summary>Un paso. Devuelve el registro una sola vez, cuando el vuelo termina.</summary>
    public FlightRecord? Update(UserAircraftState u, PilotPhase phase, DateTime utc, Func<FlightRecord> build)
    {
        if (Completed)
            return null;
        if (_offBlock is null && phase >= PilotPhase.PushbackApproved && u.OnGround && (u.GroundSpeedKt > 1 || u.PushbackAttached))
            _offBlock = utc;
        if (!u.OnGround)
        {
            if (_wasOnGround && _takeoff is null && phase >= PilotPhase.TakeoffCleared)
                _takeoff = utc;
            if (_last is { } prev)
                _distanceM += GeoMath.DistanceM(prev, u.Position);
            _last = u.Position;
            _maxAltFt = Math.Max(_maxAltFt, u.AltitudeMslFt);
        }
        else
        {
            if (!_wasOnGround)
                _landing = utc; // también si LUAW se abrió en vuelo (sin despegue registrado)
            _last = null;
        }
        _wasOnGround = u.OnGround;
        var shutDown = u.EnginesRunning is { } running ? !running : u.ParkingBrakeSet;
        var down = phase == PilotPhase.Arrived || phase is PilotPhase.Landed or PilotPhase.TaxiIn && _landing is not null;
        if (!down || !u.OnGround || u.GroundSpeedKt > 1 || !shutDown)
            return null;
        Completed = true;
        return build() with
        {
            OffBlockUtc = _offBlock,
            TakeoffUtc = _takeoff,
            LandingUtc = _landing,
            OnBlockUtc = utc,
            DistanceNm = _distanceM / UnitConversions.MetersPerNauticalMile,
            MaxAltitudeFt = _maxAltFt,
        };
    }
}

/// <summary>Historial de vuelos del piloto en <c>%LOCALAPPDATA%\LUAW\flights.json</c> (los últimos 500).</summary>
public static class FlightLogbook
{
    public const int MaxFlights = 500;

    public static string PathFor(string root) => Path.Combine(root, "flights.json");

    public static List<FlightRecord> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), FlightLogJsonContext.Default.ListFlightRecord) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Agrega un vuelo y guarda (el más nuevo primero).</summary>
    public static List<FlightRecord> Add(string path, FlightRecord flight)
    {
        var all = Load(path);
        all.Insert(0, flight);
        if (all.Count > MaxFlights)
            all.RemoveRange(MaxFlights, all.Count - MaxFlights);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, FlightLogJsonContext.Default.ListFlightRecord));
        return all;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<FlightRecord>))]
internal sealed partial class FlightLogJsonContext : JsonSerializerContext;

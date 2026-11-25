using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;

namespace SimATC.Core.Traffic;

/// <summary>Un avión estacionado ya decidido: qué modelo y en qué puesto.</summary>
public sealed record ParkedAircraft(
    string Airline,
    string Type,
    string TailNumber,
    ModelMatch Match,
    int ParkingIndex,
    Parking Parking)
{
    /// <summary>Solo decoración (aviación general o "de noche" en plataforma remota): nunca sale (ADR-109).</summary>
    public bool Decor { get; init; }

    /// <summary>Fuselaje ancho en una puerta más chica en los datos (ADR-167): cuánto más atrás queda (m).</summary>
    public double BackM { get; init; }

    /// <summary>Y su media envergadura (m), que deja sin usar las vecinas; 0 = cabe en su puesto.</summary>
    public double WideHalfSpanM { get; init; }

    /// <summary>Lo que ocupa a los lados (m).</summary>
    public double HalfWidthM => Math.Max(Parking.RadiusM, WideHalfSpanM);

    /// <summary>Dónde queda estacionado.</summary>
    public GeoPoint Position => BackM > 0 ? GeoMath.Destination(Parking.Position, Parking.HeadingTrueDeg + 180, BackM) : Parking.Position;
}

/// <summary>
/// Decide qué aviones estacionar en un aeropuerto y en qué puestos (RF-10), a partir de su lista de rutas.
/// Etapa 2: aún no hay horarios; el reparto entre aerolíneas es proporcional a sus destinos (aproximado,
/// ADR-016). Determinista para la misma semilla.
/// </summary>
public static partial class ParkedFleetPlanner
{
    /// <summary>
    /// Media envergadura que se supone para el avión del piloto (m). Valor aproximado (narrowbody grande) hasta
    /// leer su envergadura real; se suma a la media envergadura del estacionado.
    /// </summary>
    public const double UserClearanceM = 25;

    /// <summary>
    /// Parte de los puestos de aerolínea que se llena con estacionados (ADR-085). El resto queda para las llegadas IA y el
    /// piloto (prueba de Marco en SCIE: 20 pedidos para 4 puestos, y su puesto estaba ocupado). APROXIMADO.
    /// </summary>
    public const double FillShare = 0.55;

    /// <summary>
    /// Holgura (m) entre el radio del puesto y la media envergadura (ADR-089): el simulador da 18 m a las puertas de SCIE y
    /// un A320 (17,9 m de media envergadura) se estaciona ahí; antes se pedía 1 m de más y el piloto se quedó sin puerta.
    /// </summary>
    public const double FitToleranceM = 1;

    /// <summary>Holgura (m) para un puesto remoto de respaldo (plataforma), cuando no queda ninguna puerta. APROXIMADO.</summary>
    public const double RemoteToleranceM = 3;

    /// <summary>¿Cabe un avión de esa media envergadura en el puesto?</summary>
    public static bool Fits(Parking p, double halfSpanM, double toleranceM = FitToleranceM) => p.RadiusM >= halfSpanM - toleranceM;

    /// <summary>¿Puesto remoto de respaldo para un avión de línea (plataforma de aviación general mediana o mayor, o de carga)?</summary>
    public static bool RemoteStand(Parking p) => p.Type is ParkingType.RampGaMedium or ParkingType.RampGaLarge or ParkingType.RampGaExtra
        or ParkingType.RampCargo or ParkingType.RampMilCargo;

    /// <summary>
    /// Castigo por la aerolínea (m de puntaje): ninguno en un puesto suyo, más en uno que los datos dan a otras que en uno sin
    /// lista (ADR-170).
    /// </summary>
    public static double AirlinePenalty(Parking p, string? airline) =>
        p.Airlines.Count == 0 ? 50 : airline is not null && p.Airlines.Contains(airline, StringComparer.OrdinalIgnoreCase) ? 0 : 80;

    /// <summary>¿Puerta de pasajeros?</summary>
    public static bool Gate(Parking p) => p.Type is ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy or ParkingType.GateExtra;

    /// <summary>¿Puesto de aerolínea (puerta o plataforma grande o de carga)?</summary>
    public static bool AirlineStand(Parking p) => p.Type is ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy
        or ParkingType.GateExtra or ParkingType.RampGaLarge or ParkingType.RampGaExtra or ParkingType.RampCargo;

    /// <summary>
    /// El puesto para el piloto en su destino (ADR-085): de pasajeros, donde quepa su envergadura, libre; primero los de su
    /// aerolínea. -1 si no hay.
    /// </summary>
    public static int PilotStand(AirportData airport, string? airline, double wingspanM, Func<int, bool> busy, Random rng)
    {
        // Una puerta; si no queda ninguna, un puesto remoto (ADR-089).
        for (var pass = 0; pass < 2; pass++)
        {
            var best = -1;
            var bestScore = double.MaxValue;
            for (var i = 0; i < airport.Parkings.Count; i++)
            {
                var p = airport.Parkings[i];
                var ok = pass == 0 ? Gate(p) && Fits(p, wingspanM / 2) : RemoteStand(p) && Fits(p, wingspanM / 2, RemoteToleranceM);
                if (!ok || busy(i))
                    continue;
                var score = AirlinePenalty(p, airline) + Math.Abs(p.RadiusM - wingspanM / 2) + rng.NextDouble() * 10;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (best >= 0)
                return best;
        }
        return -1;
    }

    /// <summary>Cuántos estacionar en <paramref name="airport"/>: <see cref="FillShare"/> de sus puestos, como mucho <paramref name="max"/>.</summary>
    /// <param name="keepFree">Puestos que se dejan libres para las llegadas de la próxima hora y el piloto (ADR-089).</param>
    public static int FillTarget(AirportData airport, int max, int keepFree = 0)
    {
        var stands = 0;
        foreach (var p in airport.Parkings)
        {
            if (AirlineStand(p))
                stands++;
        }
        return Math.Clamp(Math.Min((int)Math.Floor(stands * FillShare), stands - keepFree), 0, Math.Max(0, max));
    }

    /// <summary>Holgura al comprobar que dos aviones no se tocan (m).</summary>
    public const double SpacingToleranceM = 2;

    private static readonly ParkingType[] PassengerStands =
        [ParkingType.GateSmall, ParkingType.GateMedium, ParkingType.GateHeavy, ParkingType.GateExtra,
         ParkingType.RampGaLarge, ParkingType.RampGaExtra];

    private static readonly ParkingType[] CargoStands =
        [ParkingType.RampCargo, ParkingType.RampGaLarge, ParkingType.RampGaExtra, ParkingType.GateHeavy];

    /// <param name="count">Cuántos aviones se quieren (se recorta a los puestos que alcancen y a los límites).</param>
    /// <param name="user">Posición del piloto, o null si no se conoce.</param>
    /// <param name="typesForAirline">Tipos disponibles para una aerolínea cuando la lista de rutas no los trae.</param>
    public static List<ParkedAircraft> Plan(
        AirportData airport,
        RouteList routes,
        ModelMatcher matcher,
        Func<string, IReadOnlyList<string>> typesForAirline,
        int count,
        GeoPoint? user,
        int seed = 1,
        IReadOnlySet<int>? busyStands = null)
    {
        var rng = new Random(seed);
        var requests = PickAircraft(routes, matcher, typesForAirline, count, rng);

        // Los más grandes primero, para que no se queden sin puesto.
        requests.Sort((a, b) => matcher.WingspanFor(b.Match.Model).CompareTo(matcher.WingspanFor(a.Match.Model)));

        var placed = new List<ParkedAircraft>();
        // Puestos que no se pueden usar: los de salidas en curso (el avión o su remolcador siguen ahí).
        var used = busyStands is null ? new HashSet<int>() : new HashSet<int>(busyStands);
        foreach (var r in requests)
        {
            var stand = BestStand(airport, r, matcher, used, placed, user);
            if (stand < 0)
                continue;
            used.Add(stand);
            placed.Add(new ParkedAircraft(r.Airline, r.Type, r.TailNumber, r.Match, stand, airport.Parkings[stand]));
        }
        return placed;
    }

    private sealed record Candidate(string Airline, string Type, string TailNumber, ModelMatch Match);

    private static List<Candidate> PickAircraft(RouteList routes, ModelMatcher matcher,
        Func<string, IReadOnlyList<string>> typesForAirline, int count, Random rng)
    {
        // Peso por aerolínea: destinos todo el año + la mitad de los estacionales.
        var airlines = routes.Airlines
            .Select(a => (Airline: a, Weight: a.Destinations.Count + a.Seasonal.Count * 0.5))
            .Where(x => x.Weight > 0)
            .ToList();
        var total = airlines.Sum(x => x.Weight);
        var result = new List<Candidate>();
        if (total <= 0)
            return result;

        for (var i = 0; i < count; i++)
        {
            var roll = rng.NextDouble() * total;
            var chosen = airlines[^1].Airline;
            foreach (var (a, w) in airlines)
            {
                if ((roll -= w) <= 0)
                {
                    chosen = a;
                    break;
                }
            }

            var types = chosen.Types.Count > 0 ? chosen.Types : typesForAirline(chosen.Icao);
            if (types.Count == 0)
                continue; // sin modelos de esa aerolínea ni tipos conocidos: no se inventa un tipo
            var type = types[rng.Next(types.Count)];
            // Indicativo variado (aerolínea + número de vuelo de 3-4 cifras); los números reales llegan con los horarios
            // (RF-12). También cambia la livery, que se elige de forma estable por el indicativo.
            var tail = $"{chosen.Icao}{rng.Next(100, 10000)}";
            if (result.Any(c => c.TailNumber == tail))
                tail = $"{chosen.Icao}{rng.Next(100, 10000)}";
            if (matcher.Match(chosen.Icao, type, tail) is not { } match)
                continue;
            result.Add(new Candidate(chosen.Icao, type, tail, match));
        }
        return result;
    }

    /// <summary>El puesto libre más chico en el que cabe el avión, o -1.</summary>
    private static int BestStand(AirportData airport, Candidate r, ModelMatcher matcher, HashSet<int> used,
        List<ParkedAircraft> placed, GeoPoint? user)
    {
        var model = r.Match.Model;
        var halfSpan = HalfSpan(matcher, model);
        var cargo = model.Title.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ||
                    model.ParkingTypes.Contains("CARGO");
        var allowed = cargo ? CargoStands : PassengerStands;

        var best = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            var p = airport.Parkings[i];
            if (used.Contains(i) || Array.IndexOf(allowed, p.Type) < 0 || !Fits(p, halfSpan, 0))
                continue;
            // Si el puesto define aerolíneas, solo esas.
            if (p.Airlines.Count > 0 && !p.Airlines.Contains(r.Airline, StringComparer.OrdinalIgnoreCase))
                continue;
            if (user is { } u && GeoMath.DistanceM(p.Position, u) < UserClearanceM + halfSpan)
                continue;
            if (Collides(p.Position, halfSpan, placed, matcher))
                continue;

            // Preferir: puesto de la aerolínea, luego el tipo de puesto en orden, luego el más ajustado.
            var score = (p.Airlines.Count > 0 ? 0 : 1000) + Array.IndexOf(allowed, p.Type) * 100 + (p.RadiusM - halfSpan);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Media envergadura para puestos; sin dato, la de un A320 (valor aproximado).</summary>
    private static double HalfSpan(ModelMatcher matcher, AircraftModel model)
    {
        var span = matcher.WingspanFor(model);
        return span > 0 ? span / 2 : 18;
    }

    private static bool Collides(GeoPoint position, double halfSpan, List<ParkedAircraft> placed, ModelMatcher matcher)
    {
        foreach (var other in placed)
        {
            var otherHalf = HalfSpan(matcher, other.Match.Model);
            if (GeoMath.DistanceM(position, other.Parking.Position) < halfSpan + otherHalf - SpacingToleranceM)
                return true;
        }
        return false;
    }
}

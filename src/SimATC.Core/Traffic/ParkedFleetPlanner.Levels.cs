using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;

namespace SimATC.Core.Traffic;

/// <summary>
/// Llenado por tipo de puesto según el nivel de tráfico (ADR-109, pedido de Marco 2026-11-09: "siempre se estacionan 20"):
/// cada tipo (puertas grandes, medianas y chicas, carga, remotos y aviación general) se llena con su ocupación, eligiendo
/// los puestos al azar. Puertas grandes con fuselaje ancho, carga con aerolíneas de carga, remotos "de noche" y aviación
/// general con aviones del simulador (estos dos solo decoran: nunca salen). Ninguna aerolínea pasa del 40% salvo la de base.
/// </summary>
public static partial class ParkedFleetPlanner
{
    /// <summary>Parte máxima de los estacionados de una aerolínea que no es la de base del aeropuerto.</summary>
    public const double AirlineCapShare = 0.40;

    /// <summary>Una aerolínea con al menos esta parte de los destinos es la de base (sin tope).</summary>
    public const double HubShare = 0.30;

    /// <summary>Media envergadura mínima (m) que se prefiere en una puerta grande (fuselaje ancho: ~44 m de envergadura).</summary>
    public const double WidebodyHalfSpanM = 22;

    private static readonly StandCategory[] AirlineCategories =
        [StandCategory.GateHeavy, StandCategory.GateMedium, StandCategory.GateSmall, StandCategory.Cargo, StandCategory.Remote];

    /// <param name="keepFree">Puestos de aerolínea libres para las llegadas de la próxima hora y el piloto.</param>
    /// <param name="busyStands">Puestos que no se pueden usar (salidas en curso, reservados, sin camino a las pistas).</param>
    /// <param name="ga">Decoración de aviación general disponible (vacía: las plataformas de aviación general quedan libres).</param>
    public static List<ParkedAircraft> PlanForLevel(
        AirportData airport,
        RouteList routes,
        ModelMatcher matcher,
        Func<string, IReadOnlyList<string>> typesForAirline,
        TrafficPreset preset,
        int keepFree,
        GeoPoint? user,
        int seed,
        IReadOnlySet<int>? busyStands = null,
        IReadOnlyList<GaDecorModel>? ga = null)
    {
        var rng = new Random(seed);
        var (airlineTarget, gaTarget) = StandCensus.Targets(airport, preset, keepFree);
        var counts = StandCensus.Of(airport);
        var used = busyStands is null ? new HashSet<int>() : new HashSet<int>(busyStands);
        var placed = new List<ParkedAircraft>();

        // Cuota de cada tipo: lo que pide el nivel, por prioridad hasta el total: primero las puertas (lo que se ve y lo que
        // sale), después carga y al final los remotos (LEMD tiene 149 remotos: con un reparto proporcional, casi la mitad de
        // los aviones quedaba de decoración en plataformas).
        var quota = new Dictionary<StandCategory, int>();
        var left = airlineTarget;
        foreach (var c in AirlineCategories)
        {
            var want = (int)Math.Round(counts.GetValueOrDefault(c) * preset.ShareOf(c));
            quota[c] = Math.Min(want, left);
            left -= quota[c];
        }

        var pax = Pool(routes.Airlines.Where(a => !a.Cargo));
        var cargo = Pool(routes.Airlines.Where(a => a.Cargo));
        var hub = pax.Count > 0 && pax[0].Weight / pax.Sum(x => x.Weight) >= HubShare ? pax[0].Airline.Icao : null;
        var cap = Math.Max(2, (int)Math.Ceiling(AirlineCapShare * Math.Max(1, airlineTarget)));
        var perAirline = new Dictionary<string, int>(StringComparer.Ordinal);
        var cargoModels = matcher.CargoModels();

        foreach (var category in AirlineCategories)
        {
            var stands = Shuffled(airport, category, rng);
            var filled = 0;
            foreach (var i in stands)
            {
                if (filled >= quota[category] || placed.Count >= airlineTarget)
                    break;
                if (used.Contains(i))
                    continue;
                var p = airport.Parkings[i];
                if (PickForStand(p, category, rng, pax, cargo, cargoModels, typesForAirline, matcher, hub, cap, perAirline, placed, user) is not { } pick)
                    continue;
                used.Add(i);
                perAirline[pick.Airline] = perAirline.GetValueOrDefault(pick.Airline) + 1;
                placed.Add(new ParkedAircraft(pick.Airline, pick.Type, pick.Tail, pick.Match, i, p) { Decor = category == StandCategory.Remote });
                filled++;
            }
        }

        // Aviación general: decoración con aviones del simulador, donde quepan.
        if (ga is { Count: > 0 } && gaTarget > 0)
        {
            var gaPlaced = 0;
            foreach (var i in Shuffled(airport, StandCategory.GeneralAviation, rng))
            {
                if (gaPlaced >= gaTarget)
                    break;
                if (used.Contains(i))
                    continue;
                var p = airport.Parkings[i];
                var fits = ga.Where(m => p.RadiusM >= m.WingspanM / 2 - FitToleranceM).ToList();
                if (fits.Count == 0)
                    continue;
                var m = fits[rng.Next(fits.Count)];
                var half = m.WingspanM / 2;
                if (user is { } u && GeoMath.DistanceM(p.Position, u) < UserClearanceM + half || Collides(p.Position, half, placed, matcher))
                    continue;
                used.Add(i);
                var model = GaDecor.ToModel(m);
                placed.Add(new ParkedAircraft("", m.IcaoType, $"{m.IcaoType}{rng.Next(10, 100)}", new ModelMatch(model, MatchKind.GenericSameType), i, p)
                {
                    Decor = true,
                });
                gaPlaced++;
            }
        }
        return placed;
    }

    private sealed record Pick(string Airline, string Type, string Tail, ModelMatch Match);

    /// <summary>Aerolíneas con su peso (destinos todo el año + la mitad de los estacionales), de mayor a menor.</summary>
    private static List<(RouteAirline Airline, double Weight)> Pool(IEnumerable<RouteAirline> airlines) =>
        [.. airlines.Select(a => (Airline: a, Weight: a.Destinations.Count + a.Seasonal.Count * 0.5))
            .Where(x => x.Weight > 0)
            .OrderByDescending(x => x.Weight)];

    private static List<int> Shuffled(AirportData airport, StandCategory category, Random rng)
    {
        var list = new List<int>();
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            if (StandCensus.CategoryOf(airport.Parkings[i]) == category)
                list.Add(i);
        }
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>
    /// Un avión para ese puesto: aerolínea al azar (ponderada, con tope), un tipo suyo que quepa (en puertas grandes, de
    /// preferencia fuselaje ancho) y que no choque con los ya puestos. En carga, aerolíneas de carga; sin ellas en las rutas,
    /// un modelo de carga instalado. null si no se encontró en unos intentos.
    /// </summary>
    private static Pick? PickForStand(Parking p, StandCategory category, Random rng,
        List<(RouteAirline Airline, double Weight)> pax, List<(RouteAirline Airline, double Weight)> cargo,
        IReadOnlyList<AircraftModel> cargoModels, Func<string, IReadOnlyList<string>> typesForAirline, ModelMatcher matcher,
        string? hub, int cap, Dictionary<string, int> perAirline, List<ParkedAircraft> placed, GeoPoint? user)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            string airline;
            IReadOnlyList<string> types;
            if (category == StandCategory.Cargo && cargo.Count == 0)
            {
                if (cargoModels.Count == 0)
                    return null;
                var cm = cargoModels[rng.Next(cargoModels.Count)];
                (airline, types) = (cm.IcaoAirline, [cm.IcaoType]);
            }
            else
            {
                var pool = category == StandCategory.Cargo ? cargo : pax;
                if (pool.Count == 0)
                    return null;
                var chosen = Weighted(pool, rng);
                airline = chosen.Icao;
                types = chosen.Types.Count > 0 ? chosen.Types : typesForAirline(chosen.Icao);
            }
            if (types.Count == 0 || airline != hub && perAirline.GetValueOrDefault(airline) >= cap)
                continue;
            if (p.Airlines.Count > 0 && !p.Airlines.Contains(airline, StringComparer.OrdinalIgnoreCase))
                continue;
            // Los tipos que caben, en orden al azar; en puertas grandes, primero los de fuselaje ancho.
            var tail = $"{airline}{rng.Next(100, 10000)}";
            var options = new List<ModelMatch>();
            foreach (var type in types.OrderBy(_ => rng.Next()))
            {
                if (matcher.Match(airline, type, tail) is not { } match)
                    continue;
                var half = HalfSpan(matcher, match.Model);
                if (!Fits(p, half) || user is { } u && GeoMath.DistanceM(p.Position, u) < UserClearanceM + half
                    || Collides(p.Position, half, placed, matcher))
                    continue;
                options.Add(match);
            }
            if (options.Count == 0)
                continue;
            var pick = category == StandCategory.GateHeavy && options.FirstOrDefault(o => HalfSpan(matcher, o.Model) >= WidebodyHalfSpanM) is { } wide
                ? wide
                : options[0];
            return new Pick(airline, pick.Model.IcaoType.Length > 0 ? pick.Model.IcaoType : types[0], tail, pick);
        }
        return null;
    }

    private static RouteAirline Weighted(List<(RouteAirline Airline, double Weight)> pool, Random rng)
    {
        var roll = rng.NextDouble() * pool.Sum(x => x.Weight);
        foreach (var (a, w) in pool)
        {
            if ((roll -= w) <= 0)
                return a;
        }
        return pool[^1].Airline;
    }
}

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

    /// <summary>
    /// Puertas grandes que puede ocupar una aerolínea de fuselaje ancho por cada destino suyo (ADR-162): las de largo radio
    /// tienen pocos destinos y casi nunca ganaban el sorteo frente a las de corto radio.
    /// </summary>
    public const int WidebodyPerDestination = 2;

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
        IReadOnlyList<GaDecorModel>? ga = null,
        WideStands? wide = null,
        World.VatsimRouteStore? vatsim = null)
    {
        var rng = new Random(seed);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Los tipos en el orden en que se prueban: al azar, con más peso los que vuelan esas rutas (ADR-168).
        IEnumerable<string> Order(RouteAirline? a, IReadOnlyList<string> types) => types.OrderBy(t =>
            -Math.Log(1 - rng.NextDouble()) / (a is null || vatsim is null ? 1 : vatsim.TypeWeight(a.Icao, airport.Icao, a.Destinations, t)));
        var (airlineTarget, gaTarget) = StandCensus.Targets(airport, preset, keepFree);
        var counts = StandCensus.Of(airport);
        var used = busyStands is null ? new HashSet<int>() : new HashSet<int>(busyStands);
        var placed = new List<ParkedAircraft>();

        // Cuota de cada tipo: lo que pide el nivel, por prioridad hasta el total: primero las puertas (lo que se ve y lo que
        // sale), después carga y al final los remotos (LEMD tiene 149 remotos: con un reparto proporcional, casi la mitad de
        // los aviones quedaba de decoración en plataformas).
        // De carga: las marcadas en la lista o, sin tipos en la lista (base mundial), las que solo tienen liveries de carga.
        bool IsCargo(RouteAirline a) => a.Cargo || a.Types.Count == 0 && matcher.IsCargoAirline(a.Icao);
        var pax = Painted(Pool(routes.Airlines.Where(a => !IsCargo(a))), matcher);
        var cargo = Painted(Pool(routes.Airlines.Where(IsCargo)), matcher);
        // Sin plataformas de carga en los datos (714 aeropuertos en la caché, 43 con ellas): en un aeropuerto de carga los
        // cargueros van a sus puestos normales; en los demás, a los remotos (ADR-166).
        var cargoShare = cargo.Count == 0 || counts.GetValueOrDefault(StandCategory.Cargo) > 0 ? 0
            : cargo.Sum(x => x.Weight) / (cargo.Sum(x => x.Weight) + pax.Sum(x => x.Weight));
        // Las plataformas de carga tienen su parte aunque las puertas alcancen para todos (ADR-170: con el escenario nuevo de KMIA
        // las 157 puertas dejaban 2 cargueros en uno de los aeropuertos de carga más grandes).
        var cargoWant = (int)Math.Round(counts.GetValueOrDefault(StandCategory.Cargo) * preset.ShareOf(StandCategory.Cargo));
        var cargoPart = cargo.Count == 0 ? 0 : cargo.Sum(x => x.Weight) / (cargo.Sum(x => x.Weight) + pax.Sum(x => x.Weight));
        var reserved = Math.Min(cargoWant, (int)Math.Round(airlineTarget * Math.Min(CargoReserveMax, cargoPart)));
        var quota = new Dictionary<StandCategory, int>();
        var left = airlineTarget - reserved;
        foreach (var c in AirlineCategories)
        {
            var want = (int)Math.Round(counts.GetValueOrDefault(c) * preset.ShareOf(c));
            if (c == StandCategory.Cargo)
                left += reserved;
            quota[c] = Math.Min(want, left);
            left -= quota[c];
        }
        var hub = pax.Count > 0 && pax[0].Weight / pax.Sum(x => x.Weight) >= HubShare ? pax[0].Airline.Icao : null;
        var cap = Math.Max(2, (int)Math.Ceiling(AirlineCapShare * Math.Max(1, airlineTarget)));
        var perAirline = new Dictionary<string, int>(StringComparer.Ordinal);
        var cargoModels = matcher.CargoModels();
        var widebody = WidebodyPool(pax, typesForAirline, matcher);
        var onHeavy = new Dictionary<string, int>(StringComparer.Ordinal);

        // Puertas chicas en los datos donde sí cabe un fuselaje ancho (ADR-167): antes que las demás, porque dejan sin usar a
        // las vecinas. Solo aerolíneas con algún destino que de verdad lo pide.
        var wideIn = new Dictionary<StandCategory, int>();
        if (wide is { Max: > 0 })
        {
            foreach (var (pick, stand, back, half) in WidePicks(airport, wide, pax, typesForAirline, matcher, preset, used, user, rng, Order, titles))
            {
                var c = StandCensus.CategoryOf(airport.Parkings[stand]);
                if (wideIn.GetValueOrDefault(c) >= quota.GetValueOrDefault(c) || placed.Count >= airlineTarget)
                    continue;
                wideIn[c] = wideIn.GetValueOrDefault(c) + 1;
                used.Add(stand);
                used.UnionWith(ParkedTraffic.BlockedBy(airport, stand, half));
                perAirline[pick.Airline] = perAirline.GetValueOrDefault(pick.Airline) + 1;
                placed.Add(new ParkedAircraft(pick.Airline, pick.Type, pick.Tail, pick.Match, stand, airport.Parkings[stand])
                {
                    BackM = back,
                    WideHalfSpanM = half,
                });
            }
        }

        foreach (var category in AirlineCategories)
        {
            var stands = Shuffled(airport, category, rng);
            var filled = wideIn.GetValueOrDefault(category);
            foreach (var i in stands)
            {
                if (filled >= quota[category] || placed.Count >= airlineTarget)
                    break;
                if (used.Contains(i))
                    continue;
                var p = airport.Parkings[i];
                var freighter = category == StandCategory.Cargo
                    || (category == StandCategory.Remote || cargoShare >= CargoHubShare) && rng.NextDouble() < cargoShare;
                // Puerta grande: primero una aerolínea de fuselaje ancho que no haya llenado su parte; si no, como siempre.
                var pick = category == StandCategory.GateHeavy && !freighter
                    && WidebodyOpen(widebody, onHeavy) is { Count: > 0 } open
                    && PickForStand(p, category, rng, open, cargo, cargoModels, typesForAirline, matcher, hub, cap, perAirline, placed, user, false, Order, titles, wideOnly: true) is { } big
                        ? big
                        : PickForStand(p, category, rng, pax, cargo, cargoModels, typesForAirline, matcher, hub, cap, perAirline, placed, user, freighter, Order, titles);
                if (pick is null)
                    continue;
                if (category == StandCategory.GateHeavy && HalfSpan(matcher, pick.Match.Model) >= WidebodyHalfSpanM)
                    onHeavy[pick.Airline] = onHeavy.GetValueOrDefault(pick.Airline) + 1;
                used.Add(i);
                titles.Add(pick.Match.Model.Title);
                perAirline[pick.Airline] = perAirline.GetValueOrDefault(pick.Airline) + 1;
                placed.Add(new ParkedAircraft(pick.Airline, pick.Type, pick.Tail, pick.Match, i, p) { Decor = category == StandCategory.Remote && !freighter });
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

    /// <summary>Un tramo más largo que esto (NM) pide fuselaje ancho.</summary>
    public const double WideLegNm = 3000;

    /// <summary>
    /// Los fuselajes anchos para puertas chicas en los datos (ADR-167): uno por cada dos destinos lejanos, con tope, primero en
    /// las puertas de salida más holgada (con azar).
    /// </summary>
    private static List<(Pick Pick, int Stand, double BackM, double HalfM)> WidePicks(AirportData airport, WideStands wide,
        List<(RouteAirline Airline, double Weight)> pax, Func<string, IReadOnlyList<string>> typesForAirline, ModelMatcher matcher,
        TrafficPreset preset, HashSet<int> used, GeoPoint? user, Random rng,
        Func<RouteAirline?, IReadOnlyList<string>, IEnumerable<string>> order, HashSet<string> titles)
    {
        // Por sus destinos lejanos: una aerolínea local con muchas rutas cortas no se lleva todas.
        var pool = new List<(RouteAirline Airline, double Weight)>();
        var far = 0;
        foreach (var (a, _) in pax)
        {
            var n = a.LegsNm.Count(nm => nm >= WideLegNm);
            if (n == 0)
                continue;
            pool.Add((a, n));
            far += n;
        }
        var target = (int)Math.Round(Math.Min(wide.Max * preset.ShareOf(StandCategory.GateHeavy), far / 2.0));
        var stands = wide.Candidates.OrderByDescending(i => wide.ExitHalfM(i) + rng.NextDouble() * 30).ToList();
        var result = new List<(Pick Pick, int Stand, double BackM, double HalfM)>();
        for (var attempt = 0; attempt < target * 8 && result.Count < target && pool.Count > 0; attempt++)
        {
            var a = Weighted(pool, rng);
            var tail = $"{a.Icao}{rng.Next(100, 10000)}";
            foreach (var type in order(a, a.Types.Count > 0 ? a.Types : typesForAirline(a.Icao)))
            {
                if (ModelFor(matcher, a, type, tail, cargo: false, titles) is not { } match || !TypeReach.Wide(match.Model))
                    continue;
                var half = WideStands.HalfSpanFor(match.Model, 2 * HalfSpan(matcher, match.Model));
                var found = stands.FindIndex(i => WideFits(i, match.Model, half, a.Icao));
                if (found < 0)
                    continue;
                var stand = stands[found];
                titles.Add(match.Model.Title);
                result.Add((new Pick(a.Icao, match.Model.IcaoType, tail, match), stand, wide.BackFor(stand, match.Model, half)!.Value, half));
                break;
            }
        }
        return result;

        bool WideFits(int i, AircraftModel model, double half, string airline)
        {
            var p = airport.Parkings[i];
            if (used.Contains(i) || Fits(p, half) || wide.BackFor(i, model, half) is null
                || p.Airlines.Count > 0 && !p.Airlines.Contains(airline, StringComparer.OrdinalIgnoreCase)
                || user is { } u && GeoMath.DistanceM(p.Position, u) < UserClearanceM + half + 20)
                return false;
            foreach (var r in result)
            {
                if (GeoMath.DistanceM(airport.Parkings[r.Stand].Position, p.Position) < r.HalfM + half + WideStands.WingMarginM)
                    return false;
            }
            return true;
        }
    }

    private sealed record Pick(string Airline, string Type, string Tail, ModelMatch Match);

    /// <summary>Aerolíneas con su peso (destinos todo el año + la mitad de los estacionales), de mayor a menor.</summary>
    private static List<(RouteAirline Airline, double Weight)> Pool(IEnumerable<RouteAirline> airlines) =>
        [.. airlines.Select(a => (Airline: a, Weight: a.Weight > 0 ? a.Weight : a.Destinations.Count + a.Seasonal.Count * 0.5))
            .Where(x => x.Weight > 0)
            .OrderByDescending(x => x.Weight)];

    /// <summary>Parte máxima de los estacionados que se guarda para la carga.</summary>
    public const double CargoReserveMax = 0.2;

    /// <summary>Lo que pesa en las puertas grandes un destino de medio alcance frente a uno lejano (ADR-170).</summary>
    public const double MidLegWeight = 0.25;

    /// <summary>En un aeropuerto sin plataformas de carga, desde esta parte de aerolíneas de carga es "de carga".</summary>
    public const double CargoHubShare = 0.4;

    /// <summary>Parte (por peso) de aerolíneas con librea instalada desde la que las demás no se usan.</summary>
    public const double PaintedShare = 0.25;

    /// <summary>
    /// Solo las aerolíneas con librea instalada, salvo que sean tan pocas que el aeropuerto quedaría vacío o siempre igual
    /// (ADR-166: aviones blancos de Qatar y Etihad en Miami).
    /// </summary>
    private static List<(RouteAirline Airline, double Weight)> Painted(List<(RouteAirline Airline, double Weight)> pool, ModelMatcher matcher)
    {
        var painted = pool.Where(x => matcher.HasLivery(x.Airline.Icao)).ToList();
        return painted.Sum(x => x.Weight) >= PaintedShare * pool.Sum(x => x.Weight) ? painted : pool;
    }

    /// <summary>
    /// ¿Vuela ese modelo alguna de sus rutas? Y un fuselaje ancho no va a los tramos cortos si la aerolínea tiene otros aviones.
    /// </summary>
    internal static bool Flies(RouteAirline a, AircraftModel m, bool otherTypes)
    {
        if (a.LegsNm.Count == 0)
            return true;
        var max = TypeReach.MaxNm(m);
        var min = TypeReach.Wide(m) && otherTypes ? TypeReach.WideShortNm : 0;
        foreach (var nm in a.LegsNm)
        {
            if (nm <= max && nm >= min)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Castigo (m de puntaje) por elegir un puesto de otra clase: un carguero en una puerta o uno de pasajeros en una plataforma
    /// de carga (ADR-163). Es preferencia, no prohibición: si no queda otro, igual se usa.
    /// </summary>
    public static double KindPenalty(Parking p, bool cargo) =>
        cargo == (p.Type == ParkingType.RampCargo) ? 0 : 100;

    /// <summary>Las aerolíneas de pasajeros con algún modelo de fuselaje ancho instalado, con su peso.</summary>
    private static List<(RouteAirline Airline, double Weight)> WidebodyPool(List<(RouteAirline Airline, double Weight)> pax,
        Func<string, IReadOnlyList<string>> typesForAirline, ModelMatcher matcher) =>
        [.. pax.Where(x => (x.Airline.Types.Count > 0 ? x.Airline.Types : typesForAirline(x.Airline.Icao))
            .Any(t => ModelFor(matcher, x.Airline, t, x.Airline.Icao, cargo: false) is { } m && HalfSpan(matcher, m.Model) >= WidebodyHalfSpanM
                && Flies(x.Airline, m.Model, otherTypes: true)))
            // Por los destinos que de verdad piden fuselaje ancho: una aerolínea con 150 destinos cortos se llevaba casi todas
            // las puertas grandes (British, con uno a Londres, no salía nunca).
            .Select(x => x.Airline.LegsNm.Count == 0 ? x : (x.Airline, Weight: x.Airline.LegsNm.Sum(nm =>
                nm >= WideLegNm ? 1 : nm >= TypeReach.WideShortNm ? MidLegWeight : 0)))
            .Where(x => x.Weight > 0)];

    /// <summary>
    /// El modelo de una aerolínea para un tipo: el suyo, de esa clase. Con los tipos dados por la lista de rutas, uno genérico si
    /// no tiene librea.
    /// </summary>
    private static ModelMatch? ModelFor(ModelMatcher matcher, RouteAirline a, string type, string key, bool cargo,
        HashSet<string>? used = null) =>
        matcher.MatchOwn(a.Icao, type, key, cargo, used) ?? (a.Types.Count > 0 ? matcher.Match(a.Icao, type, key) : null);

    /// <summary>Las de fuselaje ancho que todavía no ocupan sus puertas grandes (<see cref="WidebodyPerDestination"/> por destino).</summary>
    private static List<(RouteAirline Airline, double Weight)> WidebodyOpen(List<(RouteAirline Airline, double Weight)> widebody,
        Dictionary<string, int> onHeavy) =>
        [.. widebody.Where(x => onHeavy.GetValueOrDefault(x.Airline.Icao) < Math.Max(1, (int)Math.Ceiling(x.Weight * WidebodyPerDestination)))];

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
        string? hub, int cap, Dictionary<string, int> perAirline, List<ParkedAircraft> placed, GeoPoint? user, bool freighter,
        Func<RouteAirline?, IReadOnlyList<string>, IEnumerable<string>> order, HashSet<string> titles,
        bool wideOnly = false)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            string airline;
            IReadOnlyList<string> types;
            RouteAirline? chosen = null;
            if (freighter && cargo.Count == 0)
            {
                if (cargoModels.Count == 0)
                    return null;
                var cm = cargoModels[rng.Next(cargoModels.Count)];
                (airline, types) = (cm.IcaoAirline, [cm.IcaoType]);
            }
            else
            {
                var pool = freighter ? cargo : pax;
                // Puesto con su lista de aerolíneas (escenarios detallados): se sortea solo entre ellas.
                if (p.Airlines.Count > 0)
                    pool = [.. pool.Where(x => p.Airlines.Contains(x.Airline.Icao, StringComparer.OrdinalIgnoreCase))];
                if (pool.Count == 0)
                    return null;
                chosen = Weighted(pool, rng);
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
            var others = types.Count > 1;
            foreach (var type in order(chosen, types))
            {
                if ((chosen is null ? matcher.Match(airline, type, tail) : ModelFor(matcher, chosen, type, tail, freighter, titles)) is not { } match
                    || chosen is not null && !Flies(chosen, match.Model, others))
                    continue;
                var half = HalfSpan(matcher, match.Model);
                if (wideOnly && half < WidebodyHalfSpanM)
                    continue;
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

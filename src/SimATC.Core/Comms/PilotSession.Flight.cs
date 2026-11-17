using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Flight;
using SimATC.Core.Taxi;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>
/// El aeropuerto de destino para el ATC del vuelo (bloque C, ADR-069): sus datos y dependencias (simulador y
/// <c>data/atc</c>), su grafo de rodaje (para el rodaje al puesto), la pista y la aproximación que se esperan, y la altitud
/// inicial de la aproximación.
/// </summary>
public sealed record ArrivalInfo(AirportData Airport, AtcStations Stations, TaxiGraph? Graph, string Runway, string? ApproachName,
    string ApproachWord, double ApproachAltitudeFt, double? TransitionAltitudeFt)
{
    /// <summary>
    /// Restricciones "a o por encima de" de la STAR del plan, en orden (ADR-098): el fijo, dónde está y la altitud mínima
    /// (ft). Las da el ATC con el descenso ("cross ARUNI FL100 or above"). Vacía sin STAR conocida.
    /// </summary>
    public IReadOnlyList<(string Fix, GeoPoint Position, double MinFt)> StarRestrictions { get; init; } = [];

    /// <summary>La STAR del plan tal como está en el simulador ("ARUNI1A"), o null si no se conoce (ADR-101).</summary>
    public string? StarName { get; init; }

    /// <summary>La aproximación de la carta (tramos de final y de frustrada), o null (ADR-105).</summary>
    public Approach? Procedure { get; init; }

    /// <summary>La transición de la aproximación por la que se llega desde la STAR ("BERRY"), o null (ADR-105).</summary>
    public string? Transition { get; init; }

    /// <summary>El primer tramo con fijo de la transición por la que llega (el IAF), o null sin transición.</summary>
    private ProcedureLeg? IafLeg
    {
        get
        {
            if (Procedure is not { } a || Transition is not { } name)
                return null;
            foreach (var t in a.Transitions)
            {
                if (t.Name != name)
                    continue;
                foreach (var leg in t.Legs)
                {
                    if (leg.FixIcao.Length > 0 && !(leg.FixPosition.LatDeg == 0 && leg.FixPosition.LonDeg == 0))
                        return leg;
                }
            }
            return null;
        }
    }

    /// <summary>Dónde está el IAF de la transición por la que llega, o null (ADR-122).</summary>
    public GeoPoint? IafPosition => IafLeg?.FixPosition;

    /// <summary>
    /// La altitud (ft) a la que baja la Aproximación antes de autorizar la aproximación (ADR-122): la mínima publicada en el
    /// IAF de la transición; sin ella, la del fin de la STAR; si no, la inicial de la aproximación. Nunca menos que esta: la
    /// plataforma de la final llega recién con la autorización.
    /// </summary>
    public double InitialAltitudeFt
    {
        get
        {
            var alt = 0.0;
            if (IafLeg is { Altitude1Ft: > 0 } leg)
                alt = leg.AltitudeConstraint == AltitudeConstraint.Between && leg.Altitude2Ft > 0 ? Math.Min(leg.Altitude1Ft, leg.Altitude2Ft) : leg.Altitude1Ft;
            else if (StarRestrictions.Count > 0)
                alt = StarRestrictions[^1].MinFt;
            return Math.Max(alt, ApproachAltitudeFt);
        }
    }

    /// <summary>
    /// La transición de <paramref name="approach"/> que empieza donde termina la llegada (<paramref name="fromFix"/>, el
    /// último fijo de la STAR): la de ese nombre o la que empieza en ese fijo. null si ninguna calza.
    /// </summary>
    public static string? TransitionFor(Approach? approach, string? fromFix)
    {
        if (approach is null || string.IsNullOrEmpty(fromFix))
            return null;
        foreach (var t in approach.Transitions)
        {
            if (string.Equals(t.Name, fromFix, StringComparison.OrdinalIgnoreCase)
                || t.Legs.Count > 0 && string.Equals(t.Legs[0].FixIcao, fromFix, StringComparison.OrdinalIgnoreCase))
                return t.Name;
        }
        return null;
    }

    /// <summary>El último fijo de la STAR <paramref name="starIdent"/> hacia <paramref name="runway"/>, o null.</summary>
    public static string? StarEndFix(AirportData airport, string? starIdent, string? starTransition, string runway)
    {
        if (ProcedureMatch.Best(ProcedureMatch.Rank(airport.Arrivals, starIdent, runway)) is not { } match)
            return null;
        string? last = null;
        foreach (var leg in ProcedureGeometry.StarLegs(match.Procedure, runway, starTransition))
        {
            if (leg.FixIcao.Length > 0 && !leg.FixIcao.StartsWith("RW", StringComparison.Ordinal))
                last = leg.FixIcao;
        }
        return last;
    }

    /// <summary>El nombre de la STAR del plan en los procedimientos del simulador (como la elige el asesor), o null.</summary>
    public static string? StarNameFor(AirportData airport, string? starIdent, string runway) =>
        ProcedureMatch.Best(ProcedureMatch.Rank(airport.Arrivals, starIdent, runway))?.Procedure.Name;

    /// <summary>Las restricciones de la STAR <paramref name="starIdent"/> (por nombre, como la elige el asesor de procedimientos).</summary>
    public static IReadOnlyList<(string Fix, GeoPoint Position, double MinFt)> RestrictionsFor(AirportData airport, string? starIdent,
        string? starTransition, string runway)
    {
        if (ProcedureMatch.Best(ProcedureMatch.Rank(airport.Arrivals, starIdent, runway)) is not { } match)
            return [];
        var list = new List<(string, GeoPoint, double)>();
        foreach (var leg in ProcedureGeometry.StarLegs(match.Procedure, runway, starTransition))
        {
            if (leg.FixIcao.Length == 0 || leg.Altitude1Ft <= 0)
                continue;
            var min = leg.AltitudeConstraint switch
            {
                AltitudeConstraint.AtOrAbove => leg.Altitude1Ft,
                AltitudeConstraint.Between => leg.Altitude2Ft > 0 ? Math.Min(leg.Altitude1Ft, leg.Altitude2Ft) : 0,
                _ => 0,
            };
            if (min > 0)
                list.Add((leg.FixIcao, leg.FixPosition, min));
        }
        return list;
    }

    /// <summary>
    /// Arma el destino con los datos del simulador: la pista del plan (o la de la aproximación elegida), la aproximación
    /// (la elegida si es de esa pista; si no, la mejor: ILS, RNP, VOR, NDB) y su altitud inicial (la del IF de la carta).
    /// Sin altitud en la carta: elevación + 3000 ft redondeada al millar (APROXIMADO).
    /// </summary>
    public static ArrivalInfo Create(AirportData airport, AtcStations stations, TaxiGraph? graph, AirportAtcData? atc,
        string? plannedRunway, Approach? preferred)
    {
        var runway = plannedRunway ?? (preferred is null ? null : ProcedureMatch.ApproachRunway(preferred))
                     ?? (airport.Runways.Count > 0 ? airport.Runways[0].Primary.Ident : "");
        var approach = preferred is not null && ProcedureMatch.ApproachRunway(preferred) == runway ? preferred : null;
        if (approach is null)
        {
            foreach (var a in airport.Approaches)
            {
                if (ProcedureMatch.ApproachRunway(a) == runway && FlightPlanner.ApproachWord(a.Type).Length > 0
                    && (approach is null || Rank(a.Type) < Rank(approach.Type)))
                    approach = a;
            }
        }
        double? altitude = null;
        if (approach is not null)
        {
            foreach (var leg in approach.FinalLegs)
            {
                if (leg.Altitude1Ft > 0 && (leg.IsIf || altitude is null))
                    altitude = leg.Altitude1Ft;
                if (leg.IsIf && leg.Altitude1Ft > 0)
                    break;
            }
        }
        var ta = atc?.TransitionAltitudeFt ?? (airport.TransitionAltitudeFt > 0 ? airport.TransitionAltitudeFt : null);
        return new ArrivalInfo(airport, stations, graph, runway, approach is null ? null : ProcedureMatch.ApproachName(approach),
            approach is null ? "" : FlightPlanner.ApproachWord(approach.Type),
            altitude ?? Math.Ceiling((airport.ElevationFt + 3000) / 1000) * 1000, ta) { Procedure = approach };
    }

    private static int Rank(ApproachType t) => t switch
    {
        ApproachType.Ils => 0,
        ApproachType.Rnav or ApproachType.Gps => 1,
        ApproachType.Localizer => 2,
        ApproachType.Vor or ApproachType.VorDme => 3,
        _ => 4,
    };
}

/// <summary>Un avión en vuelo cerca del piloto, para la información de tráfico (ADR-075). Altitud en pies, rumbo verdadero.</summary>
public readonly record struct AirTraffic(uint Id, GeoPoint Position, double HeadingDeg, double AltitudeFt, double SpeedKt);

/// <summary>
/// El vuelo del piloto después del primer contacto con Salida (bloque C, ADR-069): ascenso a crucero, Centro (cambios de
/// FIR, si se conoce su frecuencia), descenso en el TOD, Aproximación del destino (descenso a altitud con QNH, aproximación
/// esperada y autorización), Torre (aterrizaje, frustrada), salida de pista y Ground (rodaje al puesto con calles).
/// Solicitudes: directo a un fijo, descenso, otro nivel y frustrada (RF-29b). Las frases son las de §8.3b y §8.3c.
/// </summary>
public sealed partial class PilotSession
{
    private const double Nm = UnitConversions.MetersPerNauticalMile;

    /// <summary>A esta distancia del destino (NM) se pasa a su Aproximación; sin Aproximación, a la Torre en <see cref="TowerHandoffNm"/>.</summary>
    public const double ApproachHandoffNm = 45;

    /// <summary>A esta distancia (NM) Aproximación autoriza la aproximación.</summary>
    public const double ApproachClearanceNm = 18;

    /// <summary>A esta distancia (NM) se pasa a la Torre del destino.</summary>
    public const double TowerHandoffNm = 9;

    /// <summary>Distancia antes del TOD (NM) a la que se da el descenso (3° más un margen para bajar la velocidad). APROXIMADO.</summary>
    public const double DescentMarginNm = 30;

    /// <summary>
    /// ¿Toca dar el descenso? En el TOD (3° desde la altitud de la aproximación más el margen para reducir), medido por la
    /// ruta que queda pero sin creerle más de un 35 % sobre la línea recta, o cuando el piloto ya empezó a bajar por su
    /// cuenta cerca de ahí. ADR-129 (vuelo LEMG-LEMD): la ruta estimada salía mucho más larga que la real, el descenso no
    /// llegó nunca, y la Aproximación lo recibió a FL227 y a 45 NM, con virajes de 360° para bajar.
    /// </summary>
    private bool DescentDue(double altFt, double floorFt, double destNm)
    {
        var remaining = Math.Min(Math.Max(destNm, RouteRemainingNm()), destNm * 1.35 + 10);
        var todNm = (altFt - floorFt) / 318 + DescentMarginNm;
        return remaining <= todNm || _pilotStartedDescent && remaining <= todNm + EarlyDescentNm;
    }

    /// <summary>Hasta esto (NM) antes del TOD del ATC, si el piloto empieza a bajar se le da el descenso en vez de devolverlo.</summary>
    public const double EarlyDescentNm = 60;

    private bool _pilotStartedDescent;

    // Dependencia que lleva al piloto en ruta (Salida, Centro o la Aproximación del destino).
    private AtcStation? _enroute;
    // Dependencia a la que se lo pasó y a la que tiene que llamar.
    private AtcStation? _handoffTo;
    private AtcStation? _awaitingCheckIn;
    private double? _clearedFt;
    private bool _cruiseCleared;
    private bool _descentGiven;
    private bool _approachExpected;
    private bool _approachCleared;
    private bool _landingCleared;
    private bool _continueGiven;
    // La dependencia que lo pasó a la Torre del destino (Aproximación o la de ruta).
    private AtcStation? _approachStation;
    private string? _readbackHint;
    private string? _directFix;
    // Información de tráfico ya dada (avión → cuándo), y si el piloto puede acusarla ("traffic in sight").
    private readonly Dictionary<uint, double> _trafficInfoAt = [];
    private bool _trafficAckPending;

    /// <summary>
    /// Altitud de transición del origen: la del AIP si hay archivo <c>data/atc</c>; si no, la que trae el simulador para ese
    /// aeropuerto (funciona en cualquier parte del mundo; antes caía en 18000 ft, el valor de EE. UU.).
    /// </summary>
    private double? OriginTa => ctx.Atc?.TransitionAltitudeFt ?? (ctx.Airport.TransitionAltitudeFt > 0 ? ctx.Airport.TransitionAltitudeFt : null);

    /// <summary>Distancia máxima (NM) para dar información de tráfico.</summary>
    public const double TrafficInfoNm = 8;

    /// <summary>Diferencia de altitud máxima (ft) para dar información de tráfico.</summary>
    public const double TrafficInfoFt = 2500;

    /// <summary>Se avisa si el punto de máximo acercamiento queda a menos de esto (NM)…</summary>
    public const double TrafficCpaNm = 3;

    /// <summary>…dentro de este tiempo (s).</summary>
    public const double TrafficCpaS = 360;

    /// <summary>El mismo avión no se vuelve a informar antes de esto (s).</summary>
    public const double TrafficRepeatS = 600;

    /// <summary>Ruta de rodaje al puesto en el destino (para el radar), o null.</summary>
    public IReadOnlyList<GeoPoint>? TaxiInRoute { get; private set; }

    /// <summary>El puesto asignado en el destino ("16", "C4"), o null.</summary>
    public string? ArrivalStand { get; private set; }

    /// <summary>Lo pasaron a otra dependencia y tiene que llamarla (primer contacto).</summary>
    public bool AwaitingCheckIn => _awaitingCheckIn is not null;

    /// <summary>La Torre del destino lo autorizó a aterrizar.</summary>
    public bool LandingCleared => _landingCleared;

    /// <summary>Último "direct to" autorizado (el plan y el radar siguen desde ahí).</summary>
    public string? DirectFix => _directFix;

    private ArrivalInfo? Arrival => ctx.Arrival?.Invoke();

    /// <summary>Un nivel pedido en ruta por debajo de esto (ft) no es un cambio de crucero: es el descenso de llegada.</summary>
    public const double MinCruiseRequestFt = 15_000;

    /// <summary>Sin puesto libre al pedir rodaje: Ground vuelve a buscar cada tanto (s).</summary>
    public const double TaxiInRetryS = 20;

    /// <summary>El puesto reservado se respeta si su rodaje no pasa de este factor (más el extra, m) del de la puerta libre más cercana.</summary>
    public const double ReservedStandFactor = 1.5;
    public const double ReservedStandExtraM = 600;

    private double _taxiInRetryAt = double.NaN;

    /// <summary>Pista de llegada en uso en el destino (para el resumen del vuelo), o null.</summary>
    public string? ArrivalRunway => Arrival?.Runway;

    /// <summary>La dependencia con la que habla en las fases de vuelo.</summary>
    private AtcStation? FlightStation => Phase switch
    {
        PilotPhase.WithApproach or PilotPhase.EnRoute => _awaitingCheckIn ?? _enroute ?? DepartureStation,
        PilotPhase.Arrival => _awaitingCheckIn ?? Arrival?.Stations.For(AtcRole.Approach) ?? _enroute,
        PilotPhase.ArrivalTower or PilotPhase.Landed => _awaitingCheckIn ?? Arrival?.Stations.For(AtcRole.Tower),
        PilotPhase.TaxiIn or PilotPhase.Arrived => Arrival?.Stations.For(AtcRole.Ground) ?? Arrival?.Stations.For(AtcRole.Tower),
        _ => null,
    };

    /// <summary>La dependencia que corresponde a esa frecuencia: origen, la de ruta, la del destino.</summary>
    private AtcStation? StationOn(int khz)
    {
        // En la llegada, primero las del destino (ADR-117: RKSI y RKPK tienen Ground en 121.65 y salía "Incheon" en Gimhae).
        if (Phase >= PilotPhase.Arrival && Arrival?.Stations.OnFrequency(khz) is { } dest)
            return dest;
        if (ctx.Stations.OnFrequency(khz) is { } own)
            return own;
        foreach (var s in new[] { _enroute, _handoffTo, _awaitingCheckIn, Phase >= PilotPhase.Airborne ? DepartureStation : null })
        {
            if (s is not null && Math.Abs(s.Khz - khz) <= 2)
                return s;
        }
        if (Phase >= PilotPhase.WithApproach && ctx.PilotPosition() is { } here && ctx.CenterAt?.Invoke(here) is { } c && Math.Abs(c.Khz - khz) <= 2)
            return c;
        return Arrival?.Stations.OnFrequency(khz);
    }

    /// <summary>La dependencia en esa frecuencia (origen, ruta, Centro de la posición o destino), o null: para la UI.</summary>
    public AtcStation? StationOnFrequency(int khz) => StationOn(khz);

    /// <summary>
    /// Las dependencias conocidas para el menú "Sintonizar", sin repetir frecuencia: las del origen, la de salida, las de
    /// ruta (la actual, a la que lo pasaron y el Centro de la posición) y las del destino.
    /// </summary>
    public IReadOnlyList<AtcStation> KnownStations()
    {
        var list = new List<AtcStation>();
        void Add(AtcStation? s)
        {
            if (s is not null && !list.Exists(x => Math.Abs(x.Khz - s.Khz) <= 2))
                list.Add(s);
        }
        foreach (var s in ctx.Stations.All)
            Add(s);
        Add(DepartureStation);
        Add(_enroute);
        Add(_handoffTo);
        Add(_awaitingCheckIn);
        if (Phase >= PilotPhase.Airborne && ctx.PilotPosition() is { } here)
            Add(ctx.CenterAt?.Invoke(here));
        if (Arrival is { } arrival)
        {
            foreach (var s in arrival.Stations.All)
                Add(s);
        }
        return list;
    }

    /// <summary>
    /// Las dependencias al alcance desde donde está el piloto (ADR-078), sin repetir frecuencia: las del origen y del destino
    /// según su distancia y el horizonte de radio (<see cref="RadioRange"/>), el Centro de su posición, y siempre la que le
    /// toca llamar y a la que lo transfirieron. Se recalcula al moverse (el menú "Sintonizar" se actualiza solo).
    /// </summary>
    public IReadOnlyList<AtcStation> TunableStations(GeoPoint here, double altitudeMslFt)
    {
        var list = new List<AtcStation>();
        void Add(AtcStation? s)
        {
            if (s is not null && !list.Exists(x => Math.Abs(x.Khz - s.Khz) <= 2))
                list.Add(s);
        }
        // Las que tiene que usar ahora, primero.
        Add(ExpectedStation);
        Add(_awaitingCheckIn);
        Add(_handoffTo);
        void AddAirport(AirportData airport, IEnumerable<AtcStation> stations)
        {
            var nm = GeoMath.DistanceM(here, airport.Reference) / Nm;
            var agl = Math.Max(0, altitudeMslFt - airport.ElevationFt);
            foreach (var s in stations)
            {
                if (RadioRange.InRange(s.Role, nm, agl))
                    Add(s);
            }
        }
        AddAirport(ctx.Airport, DepartureStation is { } dep ? [.. ctx.Stations.All, dep] : ctx.Stations.All);
        if (Arrival is { } arrival)
            AddAirport(arrival.Airport, arrival.Stations.All);
        if (ctx.OnGround?.Invoke() != true)
            Add(ctx.CenterAt?.Invoke(here));
        return list;
    }

    private double DestinationNm()
    {
        var dest = Arrival?.Airport.Reference ?? ctx.Plan.Destination.Position;
        return dest is { } d && ctx.PilotPosition() is { } here ? GeoMath.DistanceM(here, d) / Nm : double.PositiveInfinity;
    }

    private double? DestTa => Arrival?.TransitionAltitudeFt;

    private string LevelText(double ft, double? ta) => RadioFormat.Level(ft, ta, ctx.Language);

    /// <summary>Instrucción con su colación esperada y el texto que sugerirle al piloto.</summary>
    private AtcTransmission FlightInstruction(AtcStation station, string id, Dictionary<string, string> fields, ExpectedReadback expected,
        string? hintId, Dictionary<string, string>? hintFields, double nowS)
    {
        _expected = expected;
        _readbackHint = hintId is null ? null : Canonical(hintId, hintFields ?? new Dictionary<string, string>(fields));
        return Instruction(station, id, fields, nowS);
    }

    /// <summary>"Contact (dependencia) (frecuencia)": el piloto colaciona y después llama a la nueva.</summary>
    private AtcTransmission Handoff(AtcStation from, AtcStation to, double nowS)
    {
        _handoffTo = to;
        if (to.Role == AtcRole.Tower && from.Role != AtcRole.Tower)
            _approachStation = from; // a quien vuelve tras un motor y al aire
        var f = new Dictionary<string, string> { ["next_station"] = to.Callsign, ["frequency"] = to.FrequencyText };
        return FlightInstruction(from, "atc.contact", f, new ExpectedReadback(Callsign) { FrequencyKhz = to.Khz }, "pilot.readback_contact",
            new Dictionary<string, string>(f), nowS);
    }

    /// <summary>Lo que el ATC hace solo en las fases de vuelo, según la posición, la altitud y la distancia al destino.</summary>
    private IReadOnlyList<AtcTransmission> FlightProgress(double nowS)
    {
        if (_expected is not null || _awaitingCheckIn is not null)
            return [];
        var alt = ctx.AltitudeFt?.Invoke() ?? 0;
        var destNm = DestinationNm();
        var arrival = Arrival;
        var station = FlightStation;
        if (station is not null && LevelBust(station, alt, nowS) is { } bust)
            return [bust];
        // La llegada vigilada (ADR-105): velocidad, perfil, senda y estabilización.
        if (Phase is PilotPhase.EnRoute or PilotPhase.Arrival or PilotPhase.ArrivalTower && arrival is not null && station is not null
            && Watch(station, arrival, alt, nowS) is { Count: > 0 } watched)
            return watched;
        switch (Phase)
        {
            // Ascenso a crucero cuando se acerca al nivel inicial de la autorización.
            case PilotPhase.WithApproach when station is not null && !_cruiseCleared:
            {
                var cruise = (double)ctx.Plan.CruiseAltitudeFt;
                var initial = Clearance?.LevelFt ?? 5000;
                if (cruise <= initial + 500)
                {
                    _cruiseCleared = true;
                    Phase = PilotPhase.EnRoute;
                    return [];
                }
                if (alt < initial - 2000)
                    return [];
                _cruiseCleared = true;
                _clearedFt = cruise;
                Phase = PilotPhase.EnRoute;
                var level = LevelText(cruise, OriginTa);
                return [FlightInstruction(station, "atc.climb", new() { ["level"] = level }, new ExpectedReadback(Callsign) { LevelFt = cruise },
                    "pilot.readback_climb", new() { ["level"] = level }, nowS)];
            }
            case PilotPhase.EnRoute when station is not null && RunwayChangeToAnnounce(arrival):
                return [ExpectApproach(station, arrival!)];
            case PilotPhase.Arrival when station is not null && RunwayChangeToAnnounce(arrival):
                return [ExpectApproach(station, arrival!)];
            case PilotPhase.EnRoute when station is not null:
            {
                // Aproximación del destino (o su Torre si no tiene).
                var destApp = arrival?.Stations.For(AtcRole.Approach);
                if (destApp is not null && destNm <= ApproachHandoffNm && destApp.Khz != station.Khz)
                    return [Handoff(station, destApp, nowS)];
                // Sin Aproximación en el destino (SCIE): la dependencia de ruta hace su trabajo (descenso con QNH a la altitud
                // de la carta, aproximación esperada y autorización) y después pasa a la Torre (prueba de Marco, ADR-075).
                if (destApp is null && arrival is not null && destNm <= ApproachHandoffNm)
                {
                    Phase = PilotPhase.Arrival;
                    return ArrivalCheckIn(station, arrival, nowS, checkIn: false);
                }
                // Descenso en el TOD (3° desde la altitud de la aproximación).
                var floor = arrival?.ApproachAltitudeFt ?? (arrival?.Airport.ElevationFt ?? 0) + 3000;
                var target = Math.Max(10_000, Math.Ceiling(((arrival?.Airport.ElevationFt ?? 0) + 6000) / 1000) * 1000);
                // Por la ruta que queda (STAR incluida), no en línea recta: con la STAR que da la vuelta el descenso llegaba
                // antes de tiempo (prueba de Marco 2026-11-06). ADR-089.
                if (!_descentGiven && alt > target + 1000 && DescentDue(alt, floor, destNm))
                {
                    // Con el primer descenso, la aproximación que se espera (prueba de Marco: llegaba a 45 NM, "encima"),
                    // para que el piloto la prepare con tiempo (CAP 413 'expect (type) approach runway').
                    // "Descend via STAR to" el fin de la STAR (ADR-122), sin pasar bajo la altitud de transición: bajo ella la
                    // altitud va con QNH y eso lo da la Aproximación.
                    // FAA ("descend via PHLBO4", JO 7110.65 4-5-7): la frase no dice nivel y el piloto baja por las
                    // restricciones hasta el final de la STAR (el altímetro lo cambia él a 18000 ft). Con el tope en la
                    // altitud de transición se lo hacía subir a 18000 (vuelo de Marco a KEWR, 2026-11-15).
                    var saysLevel = ctx.Phrases.FieldsOf("atc.cleared_star_descend_via").Contains("level");
                    var starTarget = arrival is { StarRestrictions.Count: > 0 } withStar
                        ? saysLevel
                            ? Math.Max(withStar.StarRestrictions[^1].MinFt, DestTa ?? target)
                            : Math.Max(withStar.StarRestrictions[^1].MinFt, floor)
                        : target;
                    var d = arrival?.StarName is { } star
                        ? DescendViaStar(station, alt > starTarget + 1000 ? starTarget : target, star, DestTa ?? OriginTa, nowS)
                        : StarRestrictionAhead(arrival, target, alt) is { } cross
                            ? DescendCrossing(station, target, cross.Fix, cross.MinFt, DestTa ?? OriginTa, nowS)
                            : Descend(station, target, DestTa ?? OriginTa, nowS);
                    if (arrival?.ApproachName is null)
                        return [d];
                    _approachExpected = true;
                    return [d, ExpectApproach(station, arrival)];
                }
                // Cambio de Centro (FIR), si se conoce la frecuencia del siguiente.
                if (ctx.PilotPosition() is { } here && alt > 10_000 && ctx.CenterAt?.Invoke(here) is { } center && center.Khz != station.Khz
                    && destNm > ApproachHandoffNm)
                    return [Handoff(station, center, nowS)];
                return [];
            }
            case PilotPhase.Arrival when arrival is not null && station is not null:
            {
                // Tras la frustrada, sin primer contacto con otra dependencia (la misma sigue): la reintegra ella (ADR-105).
                if (_missedApproach && !_reintegrationGiven)
                    return Reintegration(station, arrival, checkIn: false, nowS);
                // Vectores a la final (ADR-123): el primero, o el siguiente paso.
                if (!_missedApproach || _reintegrationGiven)
                {
                    if (VectorStep(station, arrival, alt, nowS) is { } vector)
                        return [vector];
                }
                if (ReadyForApproach(arrival, alt, nowS))
                {
                    ResetVectors();
                    _approachCleared = true;
                    // La autorización permite bajar a la plataforma de la final según la carta (ADR-122).
                    if (!_missedApproach && _clearedFt is { } was && was > arrival.ApproachAltitudeFt)
                        _clearedFt = arrival.ApproachAltitudeFt;
                    ResetFinalWatch();
                    // Con la letra de la carta ("ILS Zulu": hay ILS Y e ILS Z) y la transición por la que llega ("via SNOOK"),
                    // PLAN-PULIDO bloque 2.
                    var f = new Dictionary<string, string> { ["approach"] = ApproachSpoken(arrival), ["runway"] = arrival.Runway };
                    var via = arrival.Transition is { Length: > 0 } tr && !_missedApproach;
                    if (via)
                        f["transition"] = arrival.Transition!;
                    return [FlightInstruction(station, via ? "atc.cleared_approach_via" : "atc.cleared_approach", f,
                        new ExpectedReadback(Callsign) { Runway = arrival.Runway },
                        via ? "pilot.readback_cleared_approach_via" : "pilot.readback_cleared_approach", new Dictionary<string, string>(f), nowS)];
                }
                if (ReadyForTower(arrival, destNm) && arrival.Stations.For(AtcRole.Tower) is { } tower && tower.Khz != station.Khz)
                    return [Handoff(station, tower, nowS)];
                return [];
            }
            // Ground dijo "espere" (sin puesto libre): vuelve a buscar y, si encuentra, da el rodaje (ADR-089).
            case PilotPhase.TaxiIn when TaxiInRoute is null && !double.IsNaN(_taxiInRetryAt) && nowS >= _taxiInRetryAt
                                        && Arrival?.Stations.For(AtcRole.Ground) is { } gnd:
                _taxiInRetryAt = double.NaN;
                return TaxiToStand(gnd, nowS);
            // En el puesto: detenido a menos de 40 m del puesto asignado (fin del vuelo; la app ya puede cambiar de aeropuerto).
            case PilotPhase.TaxiIn when TaxiInRoute is { Count: > 0 } tin && ctx.PilotPosition() is { } at
                                        && GeoMath.DistanceM(at, tin[^1]) < 40 && (ctx.GroundSpeedKt?.Invoke() ?? 0) < 2:
                Phase = PilotPhase.Arrived;
                return [];
            // "Continue approach": cuando la pista queda libre y nadie aterriza antes, autorizado; si llega muy cerca sin
            // autorización, motor y al aire (ADR-076).
            case PilotPhase.ArrivalTower when arrival is not null && station is not null && !_landingCleared && _continueGiven
                                              && ctx.OnGround?.Invoke() != true:
                if (!RunwayBusyForLanding(arrival))
                    return LandingDecision(station, arrival, nowS);
                if (destNm >= TowerGoAroundNm)
                    return [];
                // Como con los IA (ADR-120, Doc 4444 7.10.2): si la pista va a quedar libre antes de que cruce el umbral, la
                // Torre no lo frustra todavía; decide como tarde a LateGoAroundNm. Marco vio motores y al aire con la pista vacía.
                if (destNm > LateGoAroundNm && ctx.ArrivalRunwayClearInS?.Invoke() is { } clearS
                    && clearS + 8 < destNm / Math.Max(60, ctx.GroundSpeedKt?.Invoke() ?? 140) * 3600)
                    return [];
                return TowerGoAround(station, arrival, nowS);
            case PilotPhase.ArrivalTower when arrival is not null && station is not null && _landingCleared && ctx.OnGround?.Invoke() == true
                                              && (ctx.GroundSpeedKt?.Invoke() ?? 0) < 60:
                return Vacate(arrival, station, nowS);
            // Se pasó la salida que le dio la Torre y sigue en la pista: la siguiente (ADR-085, prueba de Marco).
            case PilotPhase.Landed when arrival is not null && station is not null && _expected is null && _exitAt is { } exitAt
                                        && _exitReissues < 3 && PassedExit(arrival, exitAt):
                _exitReissues++;
                return Vacate(arrival, station, nowS);
            default:
                return [];
        }
    }

    // La pista de la última "expect (aproximación) approach runway (pista)" dicha (ADR-100).
    private string? _announcedRunway;

    /// <summary>"Expect ILS approach runway 17L" (o "expect runway 17L" sin aproximación); recuerda la pista anunciada.</summary>
    private AtcTransmission ExpectApproach(AtcStation station, ArrivalInfo arrival)
    {
        _announcedRunway = arrival.Runway;
        if (arrival.ApproachName is null)
            return Say(station, "atc.expect_runway", new() { ["runway"] = arrival.Runway });
        return arrival.Transition is { } t
            ? Say(station, "atc.expect_approach_via", new() { ["approach"] = arrival.ApproachWord, ["runway"] = arrival.Runway, ["transition"] = t })
            : Say(station, "atc.expect_approach", new() { ["approach"] = arrival.ApproachWord, ["runway"] = arrival.Runway });
    }

    /// <summary>
    /// ¿Cambió la pista de la llegada después de anunciarla y antes de la autorización de aproximación? (la pista en uso del
    /// destino cambió por el viento de su METAR, ADR-100). Entonces el ATC dice la nueva.
    /// </summary>
    private bool RunwayChangeToAnnounce(ArrivalInfo? arrival) =>
        arrival is not null && _approachExpected && !_approachCleared && _announcedRunway is not null
        && !string.Equals(arrival.Runway, _announcedRunway, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Autorización de la STAR con el descenso (ADR-101, PANS-ATM Enmienda 7-A): "cleared ARUNI1A arrival, descend via STAR
    /// to FL120" (FAA: "descend via ARUNI1A"). "Descend via" obliga a cumplir las restricciones publicadas de la STAR.
    /// </summary>
    private AtcTransmission DescendViaStar(AtcStation station, double targetFt, string star, double? ta, double nowS)
    {
        _descentGiven = true;
        _viaStar = true;
        _clearedFt = targetFt;
        var f = new Dictionary<string, string> { ["level"] = LevelText(targetFt, ta), ["star"] = star };
        return FlightInstruction(station, "atc.cleared_star_descend_via", f, new ExpectedReadback(Callsign) { Sid = star },
            "pilot.readback_cleared_star_descend_via", new Dictionary<string, string>(f), nowS);
    }

    /// <summary>Descenso con la restricción de la STAR: "descend FL120, cross ARUNI FL170 or above" (ADR-098).</summary>
    private AtcTransmission DescendCrossing(AtcStation station, double targetFt, string fix, double crossFt, double? ta, double nowS)
    {
        _descentGiven = true;
        _clearedFt = targetFt;
        var f = new Dictionary<string, string> { ["level"] = LevelText(targetFt, ta), ["fix"] = fix, ["cross_level"] = LevelText(crossFt, ta) };
        return FlightInstruction(station, "atc.descend_cross_above", f, new ExpectedReadback(Callsign) { LevelFt = targetFt },
            "pilot.readback_descend_cross_above", new Dictionary<string, string>(f), nowS);
    }

    /// <summary>
    /// La primera restricción "a o por encima de" de la STAR que queda por delante, por encima del nivel al que se baja y
    /// por debajo del actual (si no, no dice nada nuevo), o null.
    /// </summary>
    private (string Fix, double MinFt)? StarRestrictionAhead(ArrivalInfo? arrival, double targetFt, double altFt)
    {
        if (arrival is null || arrival.StarRestrictions.Count == 0 || ctx.PilotPosition() is not { } here)
            return null;
        var d = arrival.Airport.Reference;
        var mine = GeoMath.DistanceM(here, d);
        foreach (var r in arrival.StarRestrictions)
        {
            if (GeoMath.DistanceM(r.Position, d) >= mine - 2 * Nm)
                continue; // ya pasado (o al lado)
            if (r.MinFt > targetFt + 500 && r.MinFt < altFt - 500)
                return (r.Fix, r.MinFt);
        }
        return null;
    }

    /// <summary>Lejos del TOD (más de esto, NM) un pedido de descenso recibe "expect descent after (fijo)". APROXIMADO.</summary>
    public const double ExpectDescentMinNm = 40;

    /// <summary>
    /// El último fijo del plan antes del TOD, si el TOD está todavía a más de <see cref="ExpectDescentMinNm"/>: "expect descent
    /// after (fijo)". null cerca del TOD (se da el descenso) o sin fijos.
    /// </summary>
    private string? DescentFixAhead(ArrivalInfo? arrival)
    {
        var alt = ctx.AltitudeFt?.Invoke() ?? 0;
        var floor = arrival?.ApproachAltitudeFt ?? (arrival?.Airport.ElevationFt ?? 0) + 3000;
        var todNm = (alt - floor) / 318 + DescentMarginNm; // NM antes del destino donde empieza a bajar
        var remaining = RouteRemainingNm();
        if (double.IsInfinity(remaining) || remaining - todNm < ExpectDescentMinNm)
            return null;
        var dest = Arrival?.Airport.Reference ?? ctx.Plan.Destination.Position;
        if (dest is not { } d)
            return null;
        // NM por la ruta desde cada fijo por delante hasta el destino, de atrás hacia adelante: el primero (desde el
        // destino) que queda más lejos que el TOD es el último antes de empezar a bajar.
        var ahead = AheadFixes().ToList();
        var after = 0.0;
        for (var i = ahead.Count - 1; i >= 0; i--)
        {
            after += GeoMath.DistanceM(ahead[i].Position, i + 1 < ahead.Count ? ahead[i + 1].Position : d) / Nm;
            if (after >= todNm)
                return ahead[i].Ident;
        }
        return null;
    }

    private AtcTransmission Descend(AtcStation station, double targetFt, double? ta, double nowS, bool approachDescent = true)
    {
        if (approachDescent)
            _descentGiven = true;
        _clearedFt = targetFt;
        var level = LevelText(targetFt, ta);
        return FlightInstruction(station, "atc.descend", new() { ["level"] = level }, new ExpectedReadback(Callsign) { LevelFt = targetFt },
            "pilot.readback_descend", new() { ["level"] = level }, nowS);
    }

    /// <summary>Después del aterrizaje: la Torre lo manda a librar por la salida más próxima y a Ground (CAP 413 cap. 4).</summary>
    private IReadOnlyList<AtcTransmission> Vacate(ArrivalInfo arrival, AtcStation tower, double nowS)
    {
        Phase = PilotPhase.Landed;
        var ground = arrival.Stations.For(AtcRole.Ground);
        if (ground is null || ground.Khz == tower.Khz)
        {
            Phase = PilotPhase.TaxiIn;
            return [];
        }
        _handoffTo = ground;
        var exitName = NextExitName(arrival);
        var f = new Dictionary<string, string> { ["next_station"] = ground.Callsign, ["frequency"] = ground.FrequencyText };
        if (exitName is { Length: > 0 })
            f["taxiway"] = exitName;
        var id = exitName is { Length: > 0 } ? "atc.vacate_contact_ground" : "atc.when_vacated_contact_ground";
        var hint = exitName is { Length: > 0 } ? "pilot.readback_vacate_contact_ground" : "pilot.readback_when_vacated_contact_ground";
        return [FlightInstruction(tower, id, f, new ExpectedReadback(Callsign) { FrequencyKhz = ground.Khz }, hint, new Dictionary<string, string>(f), nowS)];
    }

    // La salida que dio la Torre (para notar si el piloto se la pasó) y cuántas veces se dio otra (ADR-085).
    private GeoPoint? _exitAt;
    private int _exitReissues;

    /// <summary>Frenada supuesta para elegir una salida que el piloto alcance (m/s², ~autobrake LO). APROXIMADO.</summary>
    public const double ExitDecelMs2 = 1.5;

    /// <summary>
    /// La salida de pista con nombre más próxima por delante del piloto (en su pista) que alcanza a tomar a su velocidad
    /// (frenando a <see cref="ExitDecelMs2"/> hasta 20 kt; antes podía ser una que ya no alcanzaba), o null.
    /// </summary>
    private string? NextExitName(ArrivalInfo arrival)
    {
        _exitAt = null;
        if (arrival.Graph is not { } g || ctx.PilotPosition() is not { } here)
            return null;
        var heading = ctx.HeadingDeg?.Invoke() ?? 0;
        var v = (ctx.GroundSpeedKt?.Invoke() ?? 0) * 0.514444;
        var minD = Math.Max(30, (v * v - 10.3 * 10.3) / (2 * ExitDecelMs2));
        var me = g.Projection.ToLocal(here);
        // Del lado de su puesto (como los IA, ADR-087): no cruzar la pista ni rodearla después. Si por ese lado no hay
        // ninguna alcanzable, cualquiera.
        var standSide = 0;
        if (ctx.PreferredStand?.Invoke() is { } stand && stand >= 0 && stand < arrival.Airport.Parkings.Count)
            standSide = Side(me, g.Projection.ToLocal(arrival.Airport.Parkings[stand].Position), heading);
        for (var pass = 0; pass < 2; pass++)
        {
            string? best = null;
            var bestD = 3500.0;
            foreach (var e in g.RunwayEntries)
            {
                if (e.TaxiwayName.Length == 0)
                    continue;
                var p = g.Position(e.RunwayNode);
                if (pass == 0 && standSide != 0 && Side(p, g.Position(e.EntryNode), heading) != standSide)
                    continue;
                var d = LocalPoint.Distance(me, p);
                var bearing = Math.Atan2(p.EastM - me.EastM, p.NorthM - me.NorthM) * GeoMath.RadToDeg;
                if (d < bestD && d > minD && Math.Abs(GeoMath.AngleDiffDeg(heading, bearing)) < 30)
                {
                    bestD = d;
                    best = e.TaxiwayName;
                    _exitAt = g.Projection.ToGeo(p);
                }
            }
            if (best is not null || standSide == 0)
                return best;
        }
        return null;
    }

    /// <summary>Lado de <paramref name="to"/> respecto de <paramref name="from"/> mirando a <paramref name="headingDeg"/>: +1 derecha, -1 izquierda.</summary>
    private static int Side(LocalPoint from, LocalPoint to, double headingDeg)
    {
        var h = headingDeg * GeoMath.DegToRad;
        var cross = Math.Sin(h) * (to.NorthM - from.NorthM) - Math.Cos(h) * (to.EastM - from.EastM);
        return cross < 0 ? 1 : -1;
    }

    /// <summary>¿Dejó atrás (más de 60 m) la salida asignada, rodando todavía por la pista de aterrizaje?</summary>
    private bool PassedExit(ArrivalInfo arrival, GeoPoint exitAt)
    {
        if (ctx.PilotPosition() is not { } here || (ctx.GroundSpeedKt?.Invoke() ?? 0) < 3)
            return false;
        var heading = ctx.HeadingDeg?.Invoke() ?? 0;
        var d = GeoMath.DistanceM(here, exitAt);
        var behind = Math.Abs(GeoMath.AngleDiffDeg(heading, GeoMath.InitialBearingDeg(here, exitAt))) > 120;
        if (!behind || d < 60)
            return false;
        // Sigue sobre la pista: a menos de 35 m de su eje.
        foreach (var r in arrival.Airport.Runways)
        {
            foreach (var end in new[] { r.Primary, r.Secondary })
            {
                if (end.Ident != arrival.Runway)
                    continue;
                var dt = GeoMath.DistanceM(end.Position, here);
                var off = GeoMath.AngleDiffDeg(end.HeadingTrueDeg, GeoMath.InitialBearingDeg(end.Position, here)) * GeoMath.DegToRad;
                return Math.Abs(dt * Math.Sin(off)) < 35 && dt * Math.Cos(off) < r.LengthM + 50;
            }
        }
        return false;
    }

    /// <summary>"Runway vacated": Ground le da el rodaje al puesto con las calles (CAP 413 pág. 26).</summary>
    private IReadOnlyList<AtcTransmission> TaxiToStand(AtcStation ground, double nowS)
    {
        Phase = PilotPhase.TaxiIn;
        if (Arrival is not { Graph: { } g } arrival || ctx.PilotPosition() is not { } here)
            return [Say(ground, "atc.say_again", [])];
        var me = g.Projection.ToLocal(here);
        // Desde el nodo más cercano fuera de pista.
        var start = -1;
        var bestD = double.MaxValue;
        for (var n = 0; n < g.NodeCount; n++)
        {
            if (g.RunwayAt(n) >= 0 || g.IsParking(n))
                continue;
            var d = LocalPoint.Distance(me, g.Position(n));
            if (d < bestD)
            {
                bestD = d;
                start = n;
            }
        }
        // El puesto reservado para el piloto (ADR-085) si está libre y tiene ruta; si no, el puesto de pasajeros libre más
        // cercano con ruta (antes podía darle uno ocupado por un estacionado: prueba de Marco en SCIE, Gate 11).
        TaxiRoute? route = null;
        var standIndex = -1;
        TaxiRoute? reserved = null;
        var reservedIndex = -1;
        if (ctx.PreferredStand?.Invoke() is { } preferred && preferred >= 0 && preferred < arrival.Airport.Parkings.Count
            && ctx.StandBusy?.Invoke(preferred) != true && start >= 0 && g.FindRoute(start, g.ParkingNode(preferred)) is { } pr)
        {
            reserved = pr;
            reservedIndex = preferred;
        }
        // Una puerta libre; si no queda ninguna, un puesto remoto donde quepa (ADR-089: en SCIE, 4 puertas ocupadas, Ground
        // respondía "repita" y el piloto quedó detenido junto a la pista).
        var halfSpan = Engine.TrafficEngine.UserWingspanM / 2;
        for (var pass = 0; pass < 2 && route is null; pass++)
        {
            var candidates = Enumerable.Range(0, arrival.Airport.Parkings.Count)
                .Where(i => (pass == 0
                                ? Core.Traffic.ParkedFleetPlanner.Gate(arrival.Airport.Parkings[i])
                                : Core.Traffic.ParkedFleetPlanner.RemoteStand(arrival.Airport.Parkings[i])
                                  && Core.Traffic.ParkedFleetPlanner.Fits(arrival.Airport.Parkings[i], halfSpan, Core.Traffic.ParkedFleetPlanner.RemoteToleranceM))
                            && ctx.StandBusy?.Invoke(i) != true)
                .OrderBy(i => LocalPoint.Distance(me, g.Position(g.ParkingNode(i))))
                .Take(25);
            foreach (var i in candidates)
            {
                if (start >= 0 && g.FindRoute(start, g.ParkingNode(i)) is { } r && (route is null || r.LengthM < route.LengthM))
                {
                    route = r;
                    standIndex = i;
                }
            }
        }
        // El reservado al salir (ADR-085) se eligió sin saber por qué pista se iba a aterrizar: se usa si no queda mucho
        // más lejos que la puerta libre más cercana (ADR-129: en LEMD, aterrizando por la 18L, mandaba al otro lado del
        // aeropuerto por 17 calles).
        if (reserved is not null && (route is null || reserved.LengthM <= route.LengthM * ReservedStandFactor + ReservedStandExtraM))
        {
            route = reserved;
            standIndex = reservedIndex;
        }
        else if (reserved is not null && route is not null)
        {
            log?.Invoke(Tr.F("{0}: el puesto reservado queda a {1:0} m de rodaje y hay uno libre a {2:0} m: se usa el cercano.", Callsign, reserved.LengthM, route.LengthM));
        }
        if (route is null || standIndex < 0)
        {
            // Nada libre todavía: "espere" (no "repita": se le entendió) y se vuelve a buscar en un momento.
            _taxiInRetryAt = nowS + TaxiInRetryS;
            return [Say(ground, "atc.standby", [])];
        }
        ArrivalStand = Engine.DepartureAgent.SpokenStand(arrival.Airport.Parkings[standIndex]);
        TaxiInRoute = [.. route.Nodes.Select(n => g.Projection.ToGeo(g.Position(n)))];
        var via = string.Join(", ", route.Taxiways);
        var f = new Dictionary<string, string> { ["stand"] = ArrivalStand };
        if (via.Length > 0)
            f["route"] = via;
        return [FlightInstruction(ground, via.Length > 0 ? "atc.taxi_to_stand" : "atc.taxi_to_stand_no_route", f, new ExpectedReadback(Callsign) { Stand = ArrivalStand },
            via.Length > 0 ? "pilot.readback_taxi_to_stand" : "pilot.readback_taxi_to_stand_no_route", new Dictionary<string, string>(f), nowS)];
    }

    /// <summary>
    /// Mensajes del piloto en las fases de vuelo: primer contacto con la dependencia nueva, solicitudes (directo, descenso,
    /// ascenso), en final, frustrada, pista libre. null si no corresponde (sigue el resto de <see cref="OnPilotMessage"/>).
    /// </summary>
    private IReadOnlyList<AtcTransmission>? FlightMessage(AtcStation station, ParsedMessage m, double nowS)
    {
        if (Phase < PilotPhase.WithApproach)
            return null;
        var arrival = Arrival;
        // Primer contacto con la dependencia a la que lo pasaron.
        if (_awaitingCheckIn is { } next && Math.Abs(next.Khz - station.Khz) <= 2 && m.HasCallsign
            && !m.Has(PilotIntent.RequestDirect) && !m.Has(PilotIntent.RequestDescent) && !m.Has(PilotIntent.RequestClimb))
        {
            _awaitingCheckIn = null;
            _enroute = station;
            if (arrival is not null && station.Role == AtcRole.Approach && Math.Abs(station.Khz - (arrival.Stations.For(AtcRole.Approach)?.Khz ?? -1)) <= 2)
            {
                Phase = PilotPhase.Arrival;
                return _missedApproach && !_reintegrationGiven ? Reintegration(station, arrival, checkIn: true, nowS) : ArrivalCheckIn(station, arrival, nowS);
            }
            if (arrival is not null && station.Role == AtcRole.Tower && Math.Abs(station.Khz - (arrival.Stations.For(AtcRole.Tower)?.Khz ?? -1)) <= 2)
            {
                Phase = PilotPhase.ArrivalTower;
                return LandingDecision(station, arrival, nowS);
            }
            if (arrival is not null && station.Role == AtcRole.Ground && Phase is PilotPhase.Landed or PilotPhase.TaxiIn)
                return m.Has(PilotIntent.RunwayVacated) || Phase == PilotPhase.Landed ? TaxiToStand(station, nowS) : [];
            // Tras la frustrada sin Aproximación en el destino: la dependencia de ruta lo reintegra (ADR-105).
            if (arrival is not null && _missedApproach && !_reintegrationGiven && Phase == PilotPhase.Arrival)
                return Reintegration(station, arrival, checkIn: true, nowS);
            // Centro: "identified" (sin instrucción nueva).
            return [Say(station, "atc.identified_no_level", [])];
        }
        // Fuera de guion (ADR-133): llama a la dependencia anterior después de un traspaso: que contacte la nueva.
        if (_awaitingCheckIn is { } moved && Math.Abs(moved.Khz - station.Khz) > 2 && m.HasCallsign && !m.Has(PilotIntent.GoingAround))
            return [Say(station, "atc.contact", new() { ["next_station"] = moved.Callsign, ["frequency"] = moved.FrequencyText })];
        // Motor y al aire del piloto: la frustrada (publicada o rumbo de pista y altitud) y el paso a la Aproximación (ADR-105).
        if (m.Has(PilotIntent.GoingAround) && Phase is PilotPhase.ArrivalTower or PilotPhase.Arrival && arrival is not null)
            return GoAround(station, arrival, nowS, ordered: false);
        if (m.Has(PilotIntent.RunwayVacated) && Phase is PilotPhase.Landed or PilotPhase.TaxiIn && arrival is not null && TaxiInRoute is null)
            return TaxiToStand(station, nowS);
        if (m.Has(PilotIntent.Final) && Phase == PilotPhase.ArrivalTower && arrival is not null && !_landingCleared)
            return LandingDecision(station, arrival, nowS);
        // Fuera de guion (ADR-133): "final" con el aterrizaje ya autorizado, o todavía con la Aproximación; "pista libre"
        // con el rodaje al puesto ya dado; descenso pedido con la aproximación autorizada. Se repite lo vigente.
        if (m.Has(PilotIntent.Final) && Phase == PilotPhase.ArrivalTower && _landingCleared)
            return LastFrom(station, nowS) ?? [Say(station, "atc.continue_approach", [])];
        if (m.Has(PilotIntent.Final) && Phase == PilotPhase.Arrival && arrival is not null)
            return _approachCleared ? [Say(station, "atc.continue_approach", [])] : LastFrom(station, nowS) ?? [];
        if (m.Has(PilotIntent.RunwayVacated) && Phase == PilotPhase.TaxiIn && TaxiInRoute is not null)
            return LastFrom(station, nowS) ?? [];
        if (m.Has(PilotIntent.RequestDescent) && Phase == PilotPhase.Arrival && _approachCleared)
            return [Say(station, "atc.continue_approach", [])];
        if (m.Has(PilotIntent.RequestDirect))
            return DirectTo(station, m.Fix, nowS);
        // Otra pista para aterrizar (ADR-097): antes de la autorización de aproximación.
        if (m.Has(PilotIntent.RequestRunway) && m.Runway is { } askedRunway && arrival is not null
            && Phase is PilotPhase.WithApproach or PilotPhase.EnRoute or PilotPhase.Arrival)
            return RunwayRequest(station, askedRunway, nowS);
        // Otro nivel en crucero (ADR-089): "solicito nivel 330" / "request FL330" más bajo que el actual es un descenso a ese
        // nivel (antes respondía "ascienda"); "solicito descenso a nivel 330" respeta el nivel pedido. No cuenta como el
        // descenso de llegada: el del TOD llega igual después.
        if (m.AltitudeFt is { } lower && (m.Has(PilotIntent.RequestDescent) || m.Has(PilotIntent.RequestClimb))
            && Phase is PilotPhase.WithApproach or PilotPhase.EnRoute && lower < (ctx.AltitudeFt?.Invoke() ?? 0) - 300 && lower >= MinCruiseRequestFt)
        {
            return [Descend(station, lower, OriginTa ?? DestTa, nowS, approachDescent: false)];
        }
        // Lejos del TOD: "maintain FL350, expect descent after (fijo)" (CAP 413 cap. 7 §7.1, ADR-098).
        if (m.Has(PilotIntent.RequestDescent) && m.AltitudeFt is null && Phase == PilotPhase.EnRoute && !_descentGiven
            && DescentFixAhead(arrival) is { } after)
        {
            var level = LevelText(_clearedFt ?? ctx.AltitudeFt?.Invoke() ?? 0, OriginTa ?? DestTa);
            return [FlightInstruction(station, "atc.expect_descent_after", new() { ["level"] = level, ["fix"] = after },
                new ExpectedReadback(Callsign), "pilot.readback_maintaining", new() { ["level"] = level }, nowS)];
        }
        if (m.Has(PilotIntent.RequestDescent) && Phase is PilotPhase.EnRoute or PilotPhase.Arrival)
        {
            var alt = ctx.AltitudeFt?.Invoke() ?? 0;
            var lowest = arrival?.InitialAltitudeFt ?? 4000;
            var target = Math.Max(lowest, Math.Floor((alt - 4000) / 1000) * 1000);
            if (m.AltitudeFt is { } asked && asked < alt - 300)
                target = Math.Max(lowest, asked);
            return [Descend(station, target, Phase == PilotPhase.Arrival ? DestTa : DestTa ?? OriginTa, nowS)];
        }
        if (m.Has(PilotIntent.RequestClimb) && m.AltitudeFt is { } wanted && Phase is PilotPhase.WithApproach or PilotPhase.EnRoute)
        {
            _clearedFt = wanted;
            _cruiseCleared = true;
            var level = LevelText(wanted, OriginTa);
            return [FlightInstruction(station, "atc.climb", new() { ["level"] = level }, new ExpectedReadback(Callsign) { LevelFt = wanted },
                "pilot.readback_climb", new() { ["level"] = level }, nowS)];
        }
        return null;
    }

    /// <summary>La última instrucción de esa dependencia, repetida; null si no hay.</summary>
    private IReadOnlyList<AtcTransmission>? LastFrom(AtcStation station, double nowS) =>
        _lastInstruction is { } last && last.Station.Role == station.Role ? [Repeat(last, nowS)] : null;

    /// <summary>
    /// El piloto pide otra pista (ADR-097). Si la app la aprueba (existe y va en el sentido de la que se usa) se le dice la
    /// aproximación que se espera ahora; si no, "negative, unable runway X, runway Y in use". Ya autorizado a la
    /// aproximación, no se cambia.
    /// </summary>
    private IReadOnlyList<AtcTransmission> RunwayRequest(AtcStation station, string wanted, double nowS)
    {
        wanted = wanted.ToUpperInvariant();
        var answer = _approachCleared ? new RunwayRequestAnswer(false, Arrival?.Runway) : ctx.RequestRunway?.Invoke(wanted) ?? default;
        if (!answer.Approved || Arrival is not { } now)
            return [Say(station, "atc.unable_runway", new() { ["runway"] = wanted, ["in_use"] = answer.RunwayInUse ?? Arrival?.Runway ?? "" })];
        _approachExpected = now.ApproachName is not null;
        _approachCleared = false;
        if (now.ApproachName is null)
            return [Say(station, "atc.expect_runway", new() { ["runway"] = now.Runway })];
        return [ExpectApproach(station, now)];
    }

    /// <summary>Approach del destino: descenso a la altitud inicial con QNH y la aproximación que se espera.</summary>
    /// <param name="checkIn">Responde a un primer contacto ("identified" si no hay que bajar); sin él, lo inicia la dependencia de ruta.</param>
    private IReadOnlyList<AtcTransmission> ArrivalCheckIn(AtcStation station, ArrivalInfo arrival, double nowS, bool checkIn = true)
    {
        var alt = ctx.AltitudeFt?.Invoke() ?? 0;
        var list = new List<AtcTransmission>();
        // La altitud del IAF (o del fin de la STAR); la plataforma de la final, recién con la autorización (ADR-122: a FL195
        // y a más de 40 NM se daba "descienda a 1500 pies").
        var target = arrival.InitialAltitudeFt;
        // Ya autorizado a esa altitud o más abajo (retomar el vuelo, prueba de Marco 2026-11-09: "descienda a 4000"
        // yendo autorizado a 2500): solo "identified".
        if (alt > target + 300 && !(_clearedFt is { } already && already <= target + 50 && _descentGiven))
        {
            _clearedFt = target;
            var level = LevelText(target, arrival.TransitionAltitudeFt);
            if (ctx.QnhHpa?.Invoke() is { } qnh && target < (arrival.TransitionAltitudeFt ?? 18_000))
            {
                var f = new Dictionary<string, string>
                {
                    ["level"] = level, ["qnh"] = qnh.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["altimeter"] = AtcWords.AltimeterInHg(qnh), // fraseología FAA (ADR-099)
                };
                list.Add(FlightInstruction(station, "atc.descend_qnh", f, new ExpectedReadback(Callsign) { LevelFt = target },
                    "pilot.readback_descend_qnh", new Dictionary<string, string>(f), nowS));
            }
            else
            {
                list.Add(Descend(station, target, arrival.TransitionAltitudeFt, nowS));
            }
            _descentGiven = true;
        }
        else if (checkIn)
        {
            list.Add(Say(station, "atc.identified_no_level", []));
        }
        if (arrival.ApproachName is not null && !_approachExpected)
        {
            _approachExpected = true;
            list.Add(ExpectApproach(station, arrival));
        }
        return list;
    }

    /// <summary>Torre del destino: autorizado a aterrizar con el viento (o "continue approach" si la pista está ocupada).</summary>
    /// <summary>A menos de esto (NM) detrás de un pesado, la Torre avisa de la estela con la autorización de aterrizaje.</summary>
    public const double WakeCautionNm = 8;

    private bool _wakeCautioned;

    private IReadOnlyList<AtcTransmission> LandingDecision(AtcStation tower, ArrivalInfo arrival, double nowS)
    {
        // La secuencia de llegadas del aeropuerto activo (ADR-076): IA antes que él o la pista ocupada → "continue approach",
        // y se vuelve a evaluar cada segundo (FlightProgress). Sin datos del motor, la pista se da por libre.
        if (RunwayBusyForLanding(arrival))
        {
            _continueGiven = true;
            return [Say(tower, "atc.continue_approach", [])];
        }
        _landingCleared = true;
        var f = new Dictionary<string, string> { ["runway"] = arrival.Runway };
        var id = "atc.cleared_to_land_no_wind";
        if (ctx.Wind?.Invoke() is { } wind)
        {
            f["wind"] = wind;
            id = "atc.cleared_to_land";
        }
        var landing = FlightInstruction(tower, id, f, new ExpectedReadback(Callsign) { Runway = arrival.Runway }, "pilot.readback_land",
            new() { ["runway"] = arrival.Runway }, nowS);
        // Detrás de un pesado (o súper) a menos de 8 NM: aviso de estela antes de la autorización, una vez (ADR-124).
        if (!_wakeCautioned && ctx.LeaderWake?.Invoke() is SimATC.Core.Fleet.WakeCategory.Heavy or SimATC.Core.Fleet.WakeCategory.Super
            && ctx.LeaderAhead?.Invoke() is { GapNm: > 0 and <= WakeCautionNm } lead)
        {
            _wakeCautioned = true;
            var caution = Say(tower, "atc.caution_wake", new()
            {
                ["traffic"] = lead.Type, ["distance"] = Math.Max(1, Math.Round(lead.GapNm)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return [caution, landing];
        }
        return [landing];
    }

    /// <summary>El tipo de aproximación con la letra de la carta en el alfabeto de radio: "ILS Zulu", "RNP Yankee", "ILS".</summary>
    private static string ApproachSpoken(ArrivalInfo arrival) =>
        arrival.Procedure is { Suffix: { Length: 1 } sfx } && char.IsAsciiLetter(sfx[0])
            ? $"{arrival.ApproachWord} {AtisBuilder.Phonetic(char.ToUpperInvariant(sfx[0]))}"
            : arrival.ApproachWord;

    private bool RunwayBusyForLanding(ArrivalInfo arrival) =>
        ctx.ArrivalRunwayBusy?.Invoke() == true
        || string.Equals(arrival.Airport.Icao, ctx.Airport.Icao, StringComparison.OrdinalIgnoreCase) && ctx.RunwayStatus?.Invoke() is { Free: false };

    /// <summary>A esta distancia del aeropuerto (NM) sin autorización para aterrizar, la Torre ordena motor y al aire.</summary>
    public const double TowerGoAroundNm = 1.5;

    /// <summary>Con la pista por liberarse, la Torre espera como mucho hasta aquí (NM) antes de ordenar motor y al aire.</summary>
    public const double LateGoAroundNm = 0.7;

    /// <summary>
    /// La Torre ordena motor y al aire (CAP 413 cap. 4): vuelve a la llegada con la Aproximación del destino o, si no tiene,
    /// con la dependencia de ruta (ADR-076).
    /// </summary>
    private IReadOnlyList<AtcTransmission> TowerGoAround(AtcStation tower, ArrivalInfo arrival, double nowS) =>
        GoAround(tower, arrival, nowS, ordered: true);

    /// <summary>"Request direct (fijo)": si el fijo está más adelante en la ruta, "route direct"; si no, negativo.</summary>
    private IReadOnlyList<AtcTransmission> DirectTo(AtcStation station, string? fix, double nowS)
    {
        if (fix is null)
            return [Say(station, "atc.say_again", [])];
        var ahead = AheadFixes().Select(f => f.Ident).ToList();
        if (!ahead.Contains(fix))
            return [Say(station, "atc.unable_direct", new() { ["fix"] = fix })];
        _directFix = fix;
        ctx.DirectTo?.Invoke(fix);
        return [FlightInstruction(station, "atc.route_direct", new() { ["fix"] = fix }, new ExpectedReadback(Callsign) { Fix = fix }, "pilot.readback_route_direct",
            new() { ["fix"] = fix }, nowS)];
    }

    /// <summary>
    /// NM que quedan por la ruta del plan (SID y STAR incluidas) hasta el destino: del avión al próximo fijo por delante, los
    /// tramos que siguen y del último al aeropuerto. Sin fijos por delante, la distancia en línea recta (ADR-089).
    /// </summary>
    private double RouteRemainingNm()
    {
        var dest = Arrival?.Airport.Reference ?? ctx.Plan.Destination.Position;
        if (dest is not { } d || ctx.PilotPosition() is not { } here)
            return double.PositiveInfinity;
        var mine = GeoMath.DistanceM(here, d);
        var total = 0.0;
        GeoPoint? prev = here;
        var any = false;
        foreach (var f in ctx.Plan.Fixes)
        {
            if (f.IsPseudo || GeoMath.DistanceM(f.Position, d) >= mine - 2 * Nm)
                continue;
            total += GeoMath.DistanceM(prev.Value, f.Position);
            prev = f.Position;
            any = true;
        }
        return any ? (total + GeoMath.DistanceM(prev!.Value, d)) / Nm : mine / Nm;
    }

    /// <summary>Fijos del plan que quedan por delante (los que están más cerca del destino que el piloto), sin TOC/TOD.</summary>
    private IEnumerable<PlanFix> AheadFixes()
    {
        var dest = Arrival?.Airport.Reference ?? ctx.Plan.Destination.Position;
        if (dest is not { } d || ctx.PilotPosition() is not { } here)
            return [];
        var mine = GeoMath.DistanceM(here, d);
        return ctx.Plan.Fixes.Where(f => !f.IsPseudo && !f.IsSidStar && GeoMath.DistanceM(f.Position, d) < mine - 5 * Nm);
    }

    /// <summary>Después de una colación correcta en vuelo: si fue un cambio de frecuencia, ahora tiene que llamar a la nueva.</summary>
    private void FlightReadbackDone()
    {
        _readbackHint = null;
        if (_handoffTo is { } to)
        {
            _awaitingCheckIn = to;
            _handoffTo = null;
        }
    }

    /// <summary>Sugerencias en las fases de vuelo (RF-30b): la colación pendiente, el primer contacto o lo que conviene pedir.</summary>
    private void FlightSuggestions(List<string> list)
    {
        if (_trafficAckPending && _expected is null)
            list.Add(Canonical("pilot.traffic_in_sight", []));
        if (_expected is not null)
        {
            if (_readbackHint is { } hint)
                list.Add(hint);
            return;
        }
        var alt = ctx.AltitudeFt?.Invoke() is { } a ? Math.Round(a / 100) * 100 : 0;
        var ta = Phase >= PilotPhase.Arrival ? DestTa : OriginTa;
        if (_awaitingCheckIn is { } next)
        {
            if (Phase is PilotPhase.Landed or PilotPhase.TaxiIn && next.Role == AtcRole.Ground)
            {
                list.Add(Canonical("pilot.runway_vacated", new() { ["station"] = next.Callsign }));
                return;
            }
            if (next.Role == AtcRole.Tower && Arrival is { } arr)
            {
                list.Add(Canonical("pilot.arrival_final", new() { ["station"] = next.Callsign, ["runway"] = arr.Runway }));
                return;
            }
            list.Add(_clearedFt is { } c && Math.Abs(c - alt) > 500
                ? Canonical("pilot.check_in_enroute_changing", new()
                {
                    ["station"] = next.Callsign, ["level"] = LevelText(alt, ta), ["cleared"] = LevelText(c, ta),
                    ["verb"] = c > alt ? (ctx.Language == AtcLanguage.Spanish ? "ascendiendo" : "climbing") : (ctx.Language == AtcLanguage.Spanish ? "descendiendo" : "descending"),
                })
                : Canonical("pilot.check_in_enroute", new() { ["station"] = next.Callsign, ["level"] = LevelText(alt, ta) }));
            return;
        }
        var station = FlightStation;
        if (station is null)
            return;
        switch (Phase)
        {
            case PilotPhase.EnRoute:
                // El segundo fijo por delante (pedir directo al siguiente no ahorra nada).
                if (AheadFixes().Skip(1).FirstOrDefault() is { } fix && fix.Ident != _directFix)
                    list.Add(Canonical("pilot.request_direct", new() { ["station"] = station.Callsign, ["fix"] = fix.Ident }));
                if (!_descentGiven && DestinationNm() < 200)
                    list.Add(Canonical("pilot.request_descent", new() { ["station"] = station.Callsign }));
                break;
            // Pedir descenso solo si todavía no lo bajaron a la altitud de la aproximación (ni en la frustrada).
            case PilotPhase.Arrival when !_missedApproach && !(_clearedFt is { } cl && cl <= (Arrival?.InitialAltitudeFt ?? 0) + 50):
                list.Add(Canonical("pilot.request_descent", new() { ["station"] = station.Callsign }));
                break;
            case PilotPhase.ArrivalTower when !_landingCleared && Arrival is { } arr2:
                list.Add(Canonical("pilot.arrival_final", new() { ["station"] = station.Callsign, ["runway"] = arr2.Runway }));
                list.Add(Canonical("pilot.going_around", new() { ["station"] = station.Callsign }));
                break;
            case PilotPhase.ArrivalTower:
                list.Add(Canonical("pilot.going_around", new() { ["station"] = station.Callsign }));
                break;
        }
    }

    /// <summary>El paso actual en español para las fases de vuelo.</summary>
    private PilotStep? FlightStep()
    {
        var station = FlightStation;
        var arrival = Arrival;
        if (_expected is not null)
            return new(Tr.T("Colaciona la instrucción del ATC."), _lastInstruction?.Station ?? station);
        if (_awaitingCheckIn is { } next)
            return new(next.Role == AtcRole.Ground
                ? Tr.F("Libra la pista y avisa a {0} {1} que la dejaste libre.", next.Callsign, next.FrequencyText)
                : Tr.F("Cambia a {0} {1} y haz el primer contacto (indicativo y nivel).", next.Callsign, next.FrequencyText), next);
        return Phase switch
        {
            PilotPhase.WithApproach => new(Tr.T("Sigue la SID y sube al nivel autorizado: te darán el crucero."), station),
            PilotPhase.EnRoute when _descentGiven => new(Tr.T("Desciende al nivel autorizado. Te pasarán a la Aproximación del destino."), station),
            PilotPhase.EnRoute => new(Tr.F("En ruta a {0}{1}. ", ctx.Plan.Destination.Icao, (_clearedFt is { } c ? $" a {LevelText(c, OriginTa)}" : "")) +
                                      Tr.T("Puedes pedir directo a un fijo de la ruta o descenso; el ATC te bajará en el TOD."), station),
            PilotPhase.Arrival when arrival is not null => new(ArrivalStepText(arrival), station),
            PilotPhase.ArrivalTower when _landingCleared => new(Tr.T("Autorizado a aterrizar. Si no estás estabilizado, avisa \"going around\"."), station),
            PilotPhase.ArrivalTower => new(Tr.T("Avisa a la Torre que estás en final."), station),
            PilotPhase.Landed => new(Tr.T("Libra la pista por la calle indicada."), station),
            PilotPhase.TaxiIn when ArrivalStand is { } stand => new(Tr.F("Rueda al puesto {0} por la ruta del radar.", stand), null),
            PilotPhase.TaxiIn => new(Tr.T("Avisa a Ground que dejaste la pista libre."), station),
            _ => null,
        };
    }

    /// <summary>
    /// Información de tráfico en vuelo (CAP 413 cap. 5 §5.22-5.27, ADR-075): de los aviones en vuelo a menos de
    /// <see cref="TrafficInfoNm"/> y <see cref="TrafficInfoFt"/>, el primero que se acerca a menos de
    /// <see cref="TrafficCpaNm"/> en <see cref="TrafficCpaS"/>; una vez por avión cada <see cref="TrafficRepeatS"/>.
    /// Solo con la dependencia de ruta o llegada, sin colaciones ni primeros contactos pendientes.
    /// </summary>
    public IReadOnlyList<AtcTransmission> AirTrafficInfo(IReadOnlyList<AirTraffic> traffic, double nowS)
    {
        if (Phase is not (PilotPhase.WithApproach or PilotPhase.EnRoute or PilotPhase.Arrival) || _expected is not null
            || _awaitingCheckIn is not null || FlightStation is not { } station || ctx.PilotPosition() is not { } here
            || ctx.OnGround?.Invoke() == true)
            return [];
        var myHdg = ctx.HeadingDeg?.Invoke() ?? 0;
        var myAlt = ctx.AltitudeFt?.Invoke() ?? 0;
        var myKt = ctx.GroundSpeedKt?.Invoke() ?? 0;
        var (myE, myN) = Velocity(myHdg, myKt);
        foreach (var t in traffic)
        {
            if (Math.Abs(t.AltitudeFt - myAlt) > TrafficInfoFt
                || _trafficInfoAt.TryGetValue(t.Id, out var at) && nowS - at < TrafficRepeatS)
                continue;
            var nm = GeoMath.DistanceM(here, t.Position) / Nm;
            if (nm > TrafficInfoNm || nm < 0.3)
                continue;
            // Posición y velocidad relativas (NM y NM/h, este-norte).
            var brg = GeoMath.InitialBearingDeg(here, t.Position);
            var rE = nm * Math.Sin(brg * GeoMath.DegToRad);
            var rN = nm * Math.Cos(brg * GeoMath.DegToRad);
            var (tE, tN) = Velocity(t.HeadingDeg, t.SpeedKt);
            var (vE, vN) = (tE - myE, tN - myN);
            var v2 = vE * vE + vN * vN;
            var tcpaH = v2 < 1 ? 0 : -(rE * vE + rN * vN) / v2;
            if (tcpaH <= 0 || tcpaH * 3600 > TrafficCpaS)
                continue;
            var cpa = Math.Sqrt(Math.Pow(rE + vE * tcpaH, 2) + Math.Pow(rN + vN * tcpaH, 2));
            if (cpa > TrafficCpaNm)
                continue;
            _trafficInfoAt[t.Id] = nowS;
            _trafficAckPending = true;
            var f = new Dictionary<string, string>
            {
                ["clock"] = Clock(GeoMath.AngleDiffDeg(myHdg, brg)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["distance"] = Math.Max(1, Math.Round(nm)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["movement"] = ctx.Phrases.Canonical(Movement(myHdg, t.HeadingDeg, GeoMath.AngleDiffDeg(myHdg, brg)), new Dictionary<string, string>()),
                ["level"] = LevelText(Math.Round(t.AltitudeFt / 100) * 100, DestTa ?? OriginTa),
            };
            return [Say(station, "atc.traffic_info", f)];
        }
        return [];
    }

    private static (double E, double N) Velocity(double headingDeg, double kt) =>
        (kt * Math.Sin(headingDeg * GeoMath.DegToRad), kt * Math.Cos(headingDeg * GeoMath.DegToRad));

    /// <summary>Marcación relativa (−180..180) en horas de reloj: 0° → 12, 90° → 3, −90° → 9.</summary>
    public static int Clock(double relativeDeg)
    {
        var h = (int)Math.Round(GeoMath.NormalizeDeg(relativeDeg) / 30) % 12;
        return h == 0 ? 12 : h;
    }

    /// <summary>Movimiento relativo (CAP 413 §5.23): misma u opuesta dirección, o cruzando por delante o por detrás.</summary>
    public static string Movement(double myHeadingDeg, double theirHeadingDeg, double relativeBearingDeg)
    {
        var rel = GeoMath.AngleDiffDeg(myHeadingDeg, theirHeadingDeg);
        if (Math.Abs(rel) < 45)
            return "traffic.similar_direction";
        if (Math.Abs(rel) > 135)
            return "traffic.opposite_direction";
        var leftToRight = rel > 0;
        var ahead = Math.Abs(relativeBearingDeg) < 90;
        return (leftToRight, ahead) switch
        {
            (true, true) => "traffic.crossing_left_right_ahead",
            (true, false) => "traffic.crossing_left_right_behind",
            (false, true) => "traffic.crossing_right_left_ahead",
            _ => "traffic.crossing_right_left_behind",
        };
    }
}

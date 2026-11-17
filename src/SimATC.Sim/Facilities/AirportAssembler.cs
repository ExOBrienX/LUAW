using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Units;

namespace SimATC.Sim.Facilities;

/// <summary>
/// Junta los mensajes FACILITY_DATA de un pedido y arma un <see cref="AirportData"/> en unidades internas.
/// Cada hijo se cuelga de su padre por <c>ParentUniqueRequestId</c> y se ordena por <c>ItemIndex</c>, porque los
/// tramos de rodaje se refieren a puntos y puestos por índice.
/// </summary>
internal sealed class AirportAssembler
{
    private readonly string _icao;
    private readonly Dictionary<uint, object> _nodes = [];
    private AirportRaw? _airport;
    private readonly List<(uint Index, RunwayRaw Raw)> _runways = [];
    private readonly List<(uint Index, FrequencyRaw Raw)> _frequencies = [];
    private readonly List<(uint Index, TaxiPointRaw Raw)> _points = [];
    private readonly List<(uint Index, ParkingNode Node)> _parkings = [];
    private readonly List<(uint Index, TaxiPathRaw Raw)> _paths = [];
    private readonly List<(uint Index, string Name)> _names = [];
    private readonly List<(uint Index, ProcedureNode Node)> _departures = [];
    private readonly List<(uint Index, ProcedureNode Node)> _arrivals = [];
    private readonly List<(uint Index, ApproachNode Node)> _approaches = [];

    public AirportAssembler(string icao)
    {
        _icao = icao;
    }

    public int MessageCount { get; private set; }

    /// <summary>Mensajes cuyo padre no se encontró (no debería pasar; se informa en el log).</summary>
    public int OrphanCount { get; private set; }

    public void Add(SIMCONNECT_RECV_FACILITY_DATA msg)
    {
        MessageCount++;
        var data = msg.Data[0];
        var index = msg.ItemIndex;
        _nodes.TryGetValue(msg.ParentUniqueRequestId, out var parent);

        switch ((SIMCONNECT_FACILITY_DATA_TYPE)msg.Type)
        {
            case SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT:
                _airport = (AirportRaw)data;
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY:
                _runways.Add((index, (RunwayRaw)data));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY:
                _frequencies.Add((index, (FrequencyRaw)data));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_POINT:
                _points.Add((index, (TaxiPointRaw)data));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING:
                var parking = new ParkingNode((TaxiParkingRaw)data);
                _nodes[msg.UniqueRequestId] = parking;
                _parkings.Add((index, parking));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING_AIRLINE:
                if (parent is ParkingNode p) p.Airlines.Add((index, ((AirlineRaw)data).Name));
                else OrphanCount++;
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PATH:
                _paths.Add((index, (TaxiPathRaw)data));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_NAME:
                _names.Add((index, ((TaxiNameRaw)data).Name));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.DEPARTURE:
            case SIMCONNECT_FACILITY_DATA_TYPE.ARRIVAL:
                var proc = new ProcedureNode(((ProcedureRaw)data).Name);
                _nodes[msg.UniqueRequestId] = proc;
                ((SIMCONNECT_FACILITY_DATA_TYPE)msg.Type == SIMCONNECT_FACILITY_DATA_TYPE.DEPARTURE ? _departures : _arrivals)
                    .Add((index, proc));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY_TRANSITION:
                if (parent is ProcedureNode rp)
                {
                    var rt = new RunwayTransitionNode((RunwayTransitionRaw)data);
                    _nodes[msg.UniqueRequestId] = rt;
                    rp.RunwayTransitions.Add((index, rt));
                }
                else OrphanCount++;
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.ENROUTE_TRANSITION:
                if (parent is ProcedureNode ep)
                {
                    var et = new NamedTransitionNode(((NamedTransitionRaw)data).Name);
                    _nodes[msg.UniqueRequestId] = et;
                    ep.EnrouteTransitions.Add((index, et));
                }
                else OrphanCount++;
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.APPROACH:
                var approach = new ApproachNode((ApproachRaw)data);
                _nodes[msg.UniqueRequestId] = approach;
                _approaches.Add((index, approach));
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.APPROACH_TRANSITION:
                if (parent is ApproachNode ap)
                {
                    var at = new NamedTransitionNode(((NamedTransitionRaw)data).Name);
                    _nodes[msg.UniqueRequestId] = at;
                    ap.Transitions.Add((index, at));
                }
                else OrphanCount++;
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.APPROACH_LEG:
                var leg = (LegRaw)data;
                switch (parent)
                {
                    case ProcedureNode n: n.Legs.Add((index, leg)); break;
                    case RunwayTransitionNode n: n.Legs.Add((index, leg)); break;
                    case NamedTransitionNode n: n.Legs.Add((index, leg)); break;
                    default: OrphanCount++; break;
                }
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.FINAL_APPROACH_LEG:
                if (parent is ApproachNode fa) fa.Final.Add((index, (LegRaw)data));
                else OrphanCount++;
                break;
            case SIMCONNECT_FACILITY_DATA_TYPE.MISSED_APPROACH_LEG:
                if (parent is ApproachNode ma) ma.Missed.Add((index, (LegRaw)data));
                else OrphanCount++;
                break;
        }
    }

    public AirportData Build()
    {
        if (_airport is not { } a || string.IsNullOrEmpty(a.Icao))
            throw new AirportNotFoundException(_icao);

        var reference = new GeoPoint(a.Latitude, a.Longitude);
        var proj = new LocalProjection(reference);
        GeoPoint FromBias(float x, float z) => proj.ToGeo(new LocalPoint(x, z));

        var runways = Ordered(_runways, r => Runway.FromCenter(
            new GeoPoint(r.Latitude, r.Longitude), r.Heading, r.Length, r.Width,
            UnitConversions.MetersToFeet(r.AltitudeM),
            r.PrimaryNumber, (RunwayDesignator)r.PrimaryDesignator,
            r.SecondaryNumber, (RunwayDesignator)r.SecondaryDesignator)
            .WithUse(new RunwayUse(r.PrimaryClosed != 0, r.PrimaryTakeoff != 0, r.PrimaryLanding != 0),
                new RunwayUse(r.SecondaryClosed != 0, r.SecondaryTakeoff != 0, r.SecondaryLanding != 0)));

        var frequencies = Ordered(_frequencies, f => new Frequency((FrequencyType)f.Type, f.FrequencyHz / 1000, FacilityText.FromUtf8(f.NameUtf8)));

        var points = Ordered(_points, p => new TaxiPoint((TaxiPointType)p.Type, (HoldShortOrientation)p.Orientation,
            FromBias(p.BiasX, p.BiasZ)));

        var parkings = Ordered(_parkings, n => new Parking((ParkingType)n.Raw.Type, (ParkingName)n.Raw.Name,
            (ParkingName)n.Raw.Suffix, (int)n.Raw.Number, FromBias(n.Raw.BiasX, n.Raw.BiasZ),
            GeoMath.NormalizeDeg(n.Raw.Heading), n.Raw.Radius, Ordered(n.Airlines, s => s.Trim())));

        var paths = Ordered(_paths, p => new TaxiPath((TaxiPathType)p.Type, p.Width, p.RunwayNumber,
            (RunwayDesignator)p.RunwayDesignator, p.Start, p.End, (int)p.NameIndex));

        return new AirportData(
            a.Icao.Trim(), FacilityText.FromUtf8(a.NameUtf8), reference, UnitConversions.MetersToFeet(a.AltitudeM), a.MagVar,
            UnitConversions.MetersToFeet(a.TransitionAltitudeM), UnitConversions.MetersToFeet(a.TransitionLevelM),
            runways, frequencies, points, parkings, paths,
            Ordered(_names, s => s.Trim()),
            Ordered(_departures, BuildProcedure),
            Ordered(_arrivals, BuildProcedure),
            Ordered(_approaches, BuildApproach));
    }

    private static Procedure BuildProcedure(ProcedureNode n) => new(
        n.Name.Trim(),
        Ordered(n.Legs, BuildLeg),
        Ordered(n.RunwayTransitions, t => new RunwayTransition(t.Raw.RunwayNumber,
            (RunwayDesignator)t.Raw.RunwayDesignator, Ordered(t.Legs, BuildLeg))),
        Ordered(n.EnrouteTransitions, t => new EnrouteTransition(t.Name.Trim(), Ordered(t.Legs, BuildLeg))));

    private static Approach BuildApproach(ApproachNode n) => new(
        (ApproachType)n.Raw.Type,
        ApproachSuffix(n.Raw.Suffix),
        n.Raw.RunwayNumber,
        (RunwayDesignator)n.Raw.RunwayDesignator,
        n.Raw.FafIcao.Trim(),
        Ordered(n.Transitions, t => new ApproachTransition(t.Name.Trim(), Ordered(t.Legs, BuildLeg))),
        Ordered(n.Final, BuildLeg),
        Ordered(n.Missed, BuildLeg));

    private static ProcedureLeg BuildLeg(LegRaw l) => new(
        (LegType)l.Type,
        l.FixIcao.Trim(),
        l.FixRegion.Trim(),
        CharField(l.FixType),
        new GeoPoint(l.FixLatitude, l.FixLongitude),
        l.FlyOver != 0,
        (TurnDirection)l.TurnDirection,
        l.Course,
        l.TrueDegree != 0,
        l.RouteDistance,
        l.DistanceMinute != 0,
        (AltitudeConstraint)l.AltitudeDescriptor,
        UnitConversions.MetersToFeet(l.Altitude1M),
        UnitConversions.MetersToFeet(l.Altitude2M),
        (SpeedConstraint)l.SpeedDescriptor,
        l.SpeedLimit,
        l.VerticalAngle,
        new GeoPoint(l.ArcCenterLatitude, l.ArcCenterLongitude),
        l.RadiusM,
        l.IsIaf != 0,
        l.IsIf != 0,
        l.IsFaf != 0,
        l.IsMap != 0);

    /// <summary>Los campos "char" llegan como INT32 con el código ASCII (0 = vacío).</summary>
    private static string CharField(int value) => value is > 32 and < 127 ? ((char)value).ToString() : "";

    /// <summary>
    /// El sufijo de aproximación múltiple es una letra (ILS X, RNAV Z…). En SCEL las aproximaciones sin sufijo llegan
    /// con '0', así que todo lo que no sea letra se toma como "sin sufijo".
    /// </summary>
    private static string ApproachSuffix(int value) => value is >= 'A' and <= 'Z' ? ((char)value).ToString() : "";

    private static List<TOut> Ordered<TIn, TOut>(List<(uint Index, TIn Item)> items, Func<TIn, TOut> map)
    {
        items.Sort((x, y) => x.Index.CompareTo(y.Index));
        var result = new List<TOut>(items.Count);
        foreach (var (_, item) in items)
            result.Add(map(item));
        return result;
    }

    private sealed class ParkingNode(TaxiParkingRaw raw)
    {
        public TaxiParkingRaw Raw { get; } = raw;
        public List<(uint Index, string Name)> Airlines { get; } = [];
    }

    private sealed class ProcedureNode(string name)
    {
        public string Name { get; } = name;
        public List<(uint Index, LegRaw Leg)> Legs { get; } = [];
        public List<(uint Index, RunwayTransitionNode Node)> RunwayTransitions { get; } = [];
        public List<(uint Index, NamedTransitionNode Node)> EnrouteTransitions { get; } = [];
    }

    private sealed class RunwayTransitionNode(RunwayTransitionRaw raw)
    {
        public RunwayTransitionRaw Raw { get; } = raw;
        public List<(uint Index, LegRaw Leg)> Legs { get; } = [];
    }

    private sealed class NamedTransitionNode(string name)
    {
        public string Name { get; } = name;
        public List<(uint Index, LegRaw Leg)> Legs { get; } = [];
    }

    private sealed class ApproachNode(ApproachRaw raw)
    {
        public ApproachRaw Raw { get; } = raw;
        public List<(uint Index, NamedTransitionNode Node)> Transitions { get; } = [];
        public List<(uint Index, LegRaw Leg)> Final { get; } = [];
        public List<(uint Index, LegRaw Leg)> Missed { get; } = [];
    }
}

using SimATC.Core.Atc;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>Algo que el piloto le puede pedir al ATC ahora, para el menú "Qué puedo pedir" (ADR-132).</summary>
/// <param name="Group">"Siempre", "En esta fase" o "Si algo no avanza".</param>
/// <param name="Help">Qué pasa si lo pides, en español.</param>
public sealed record PilotRequest(string Group, string Text, string Spoken, string Help);

/// <summary>
/// Pedidos fuera de guion (ADR-132, §8.7): qué responde el ATC cuando el piloto pide algo repetido, antes de tiempo, tarde
/// o a la dependencia que no es. Regla general: nunca "repita" si se entendió, nunca una autorización nueva sobre una
/// vigente, y si lo que lo tenía esperando ya pasó, se vuelve a evaluar y se le da. Solo frases que ya existen en §8.
/// </summary>
public sealed partial class PilotSession
{
    public static string GroupAlways => Tr.T("Siempre");
    public static string GroupPhase => Tr.T("En esta fase");
    public static string GroupStuck => Tr.T("Si algo no avanza");

    /// <summary>Pide la autorización otra vez teniéndola: se le repite la misma (mismo transponder), no una nueva.</summary>
    private IReadOnlyList<AtcTransmission> ClearanceAgain(AtcStation station, double nowS)
    {
        if (Clearance is not { } c || Phase >= PilotPhase.Airborne)
            return [];
        if (Phase == PilotPhase.ClearanceGiven && _lastInstruction is { } last)
            return [Repeat(last, nowS)];
        var fields = new Dictionary<string, string>
        {
            ["destination"] = ctx.Plan.Destination.Icao, ["runway"] = c.Runway, ["squawk"] = c.Squawk.ToString("0000"),
            ["after_departure"] = c.Next is { } n
                ? ctx.Phrases.Say("atc.clearance.after_departure", new Dictionary<string, string> { ["next_station"] = n.Callsign, ["frequency"] = n.FrequencyText })
                : "",
        };
        if (c.Sid is not null)
            fields["sid"] = c.Sid;
        if (c.LevelText is not null)
            fields["level"] = c.LevelText;
        var id = (c.Sid, c.LevelText) switch
        {
            (not null, not null) => "atc.clearance",
            (not null, null) => "atc.clearance_no_level",
            (null, not null) => "atc.clearance_no_sid",
            _ => "atc.clearance_no_sid_no_level",
        };
        log?.Invoke(Tr.F("{0}: pide la autorización otra vez; se le repite la vigente (transponder {1:0000}).", Callsign, c.Squawk));
        return [Say(station, id, fields)];
    }

    /// <summary>Pide pushback o rodaje sin autorización de vuelo: a Autorizaciones (o, si la da esta misma dependencia, se la da).</summary>
    private IReadOnlyList<AtcTransmission> ClearanceFirst(AtcStation station, double nowS)
    {
        if (ctx.Stations.For(AtcRole.Delivery) is { } delivery && delivery.Khz != station.Khz)
            return [Say(station, "atc.contact", new() { ["next_station"] = delivery.Callsign, ["frequency"] = delivery.FrequencyText })];
        return IssueClearance(station, nowS);
    }

    /// <summary>
    /// Pide pushback o rodaje cuando ya rueda o ya está con la Torre: Ground le repite el rodaje vigente o "continúe rodaje";
    /// pasado a la Torre, que la contacte. Nunca se le vuelve atrás la fase.
    /// </summary>
    private IReadOnlyList<AtcTransmission> AlreadyMoving(AtcStation station, double nowS)
    {
        if (Phase >= PilotPhase.Airborne)
            return [];
        if (Phase >= PilotPhase.TowerHandoffGiven)
        {
            return ctx.Stations.For(AtcRole.Tower) is { } tower && tower.Khz != station.Khz
                ? [Say(station, "atc.contact", new() { ["next_station"] = tower.Callsign, ["frequency"] = tower.FrequencyText })]
                : [Say(station, "atc.continue_taxi", [])];
        }
        return _lastInstruction is { Station.Role: AtcRole.Ground } last && _expected is not null
            ? [Repeat(last, nowS)]
            : [Say(station, "atc.continue_taxi", [])];
    }

    /// <summary>
    /// "Listo para salida" otra vez, ya con la Torre: se vuelve a mirar la pista. Si lo que lo tenía esperando ya pasó, se
    /// lo autoriza ahora; si no, se le repite por qué espera. Alineado y con la pista libre: despegue.
    /// </summary>
    private IReadOnlyList<AtcTransmission> ReadyAgain(AtcStation station, ClearanceData c, double nowS)
    {
        switch (Phase)
        {
            case PilotPhase.HoldShortGiven:
                _expected = null;
                Phase = PilotPhase.WithTower;
                return TowerDecision(station, c, nowS);
            case PilotPhase.LinedUp when Runway() is null or { TakeoffClear: true, ArrivalAllowsRoll: true }:
                return ClearForTakeoff(station, c, nowS);
            default:
                return _lastInstruction is { } last && last.Station.Role == AtcRole.Tower ? [Repeat(last, nowS)] : [];
        }
    }

    /// <summary>
    /// "¿Estoy autorizado?", "sigo esperando", "confirme": el ATC dice qué tiene vigente. Con una colación pendiente, repite
    /// esa instrucción; si no, según la fase (y reevaluando lo que estaba en espera).
    /// </summary>
    private IReadOnlyList<AtcTransmission> ConfirmStatus(AtcStation station, double nowS)
    {
        if (_expected is not null && _lastInstruction is { } pending && pending.Station.Role == station.Role)
            return [Repeat(pending, nowS)];
        switch (Phase)
        {
            case PilotPhase.Parked:
                return [];
            case PilotPhase.ClearanceGiven or PilotPhase.Cleared:
                return Route(station, AtcRole.Delivery, () => ClearanceAgain(station, nowS));
            case PilotPhase.PushbackApproved:
                return Route(station, AtcRole.Ground, () => [Say(station, "atc.pushback_approved", [])]);
            case PilotPhase.TaxiGiven or PilotPhase.Taxiing:
                return Route(station, AtcRole.Ground, () => IssueTaxi(station, nowS));
            case PilotPhase.TowerHandoffGiven:
                return AlreadyMoving(station, nowS);
            case >= PilotPhase.WithTower and < PilotPhase.Airborne when Clearance is { } c:
                return Route(station, AtcRole.Tower, () => Phase == PilotPhase.WithTower ? ReadyForDeparture(station, nowS) : ReadyAgain(station, c, nowS));
            case PilotPhase.ArrivalTower when Arrival is { } arrival && !_landingCleared:
                return LandingDecision(station, arrival, nowS);
            default:
                return _lastInstruction is { } last && last.Station.Role == station.Role ? [Repeat(last, nowS)] : [];
        }
    }

    /// <summary>
    /// Lo que se le puede pedir al ATC ahora (ADR-132): lo de siempre, lo propio de la fase (las mismas frases que las
    /// sugerencias cuando no hay colación pendiente) y qué decir si algo no avanza. Para el menú del panel y el manual.
    /// </summary>
    public IReadOnlyList<PilotRequest> RequestMenu()
    {
        var list = new List<PilotRequest>();
        var station = ExpectedStation?.Callsign ?? "";
        void Add(string group, string text, string help)
        {
            if (text.Length > 0 && !list.Exists(r => r.Text == text))
                list.Add(new PilotRequest(group, text, SpokenForm.Of(text, ctx.Airlines, ctx.Language), help));
        }
        // En esta fase: lo que no está ya en las sugerencias de arriba (ahí se ve lo principal; aquí, el resto).
        var shown = new HashSet<string>(Suggestions().Select(x => x.Text));
        void AddPhase(string text, string help)
        {
            if (!shown.Contains(text))
                Add(GroupPhase, text, help);
        }
        switch (Phase)
        {
            case PilotPhase.Parked or PilotPhase.ClearanceGiven or PilotPhase.Cleared when ctx.Plan.Destination.Icao is { Length: > 0 } dest:
                AddPhase(Canonical("pilot.request_clearance_no_atis", new() { ["station"] = ctx.Stations.For(AtcRole.Delivery)?.Callsign ?? station, ["destination"] = dest }),
                    Tr.T("Pide la autorización de vuelo (o que te la repitan si ya la tienes)."));
                break;
            case PilotPhase.WithApproach or PilotPhase.EnRoute or PilotPhase.Arrival:
                AddPhase(Canonical("pilot.request_descent", new() { ["station"] = station }), Tr.T("Pides bajar: el ATC te da un nivel o te dice cuándo esperarlo."));
                break;
        }
        // Si algo no avanza.
        if (Phase is >= PilotPhase.ClearanceGiven and < PilotPhase.Airborne or PilotPhase.ArrivalTower)
            Add(GroupStuck, Canonical("pilot.confirm_clearance", new() { ["station"] = station }),
                Tr.T("El ATC te dice qué tienes autorizado ahora y vuelve a revisar lo que te tenía esperando."));
        switch (Phase)
        {
            case PilotPhase.PushbackApproved when NearestStand() is { } stand:
                Add(GroupStuck, Canonical("pilot.request_pushback", new() { ["stand"] = stand }), Tr.T("Si el pushback no empieza, pídelo otra vez: te lo confirma."));
                break;
            case PilotPhase.Taxiing or PilotPhase.TaxiGiven:
                Add(GroupStuck, Canonical("pilot.request_taxi", new() { ["station"] = station }), Tr.T("Te da otra vez la ruta de rodaje, desde donde estás."));
                break;
            case PilotPhase.HoldShortGiven or PilotPhase.LinedUp when Clearance is { } c:
                Add(GroupStuck, Canonical("pilot.ready_for_departure", new() { ["station"] = ctx.Stations.For(AtcRole.Tower)?.Callsign ?? station, ["runway"] = c.Runway }),
                    Tr.T("La Torre vuelve a mirar la pista: si ya está libre, te autoriza; si no, te dice por qué esperas."));
                break;
        }
        // Siempre.
        Add(GroupAlways, Canonical("pilot.say_again", []), Tr.T("Repite la última instrucción de esa dependencia."));
        Add(GroupAlways, Canonical("pilot.radio_check", new() { ["station"] = station }), Tr.T("Comprueba que te recibe en esa frecuencia."));
        if (_expected is not null)
            Add(GroupAlways, Canonical("pilot.unable", []), Tr.T("No puedes cumplir la instrucción: el ATC te da otra."));
        return list;
    }

    internal string PhaseHelp() => Phase switch
    {
        PilotPhase.Parked => Tr.T("Pide la autorización de vuelo (pista, salida, nivel y transponder)."),
        PilotPhase.Cleared => Tr.T("Pide el pushback si estás en una puerta, o el rodaje si no hace falta."),
        PilotPhase.PushbackApproved => Tr.T("Con los motores en marcha, pide el rodaje."),
        PilotPhase.WithTower => Tr.T("Avisa que estás listo en el punto de espera."),
        PilotPhase.WithApproach or PilotPhase.EnRoute => Tr.T("Lo que puedes solicitar en ruta."),
        PilotPhase.Arrival => Tr.T("Lo que puedes solicitar en la llegada."),
        PilotPhase.ArrivalTower => Tr.T("Avisa que estás en final, o frustra."),
        PilotPhase.Landed or PilotPhase.TaxiIn => Tr.T("Avisa que dejaste la pista."),
        _ => Tr.T("Lo que corresponde decir ahora."),
    };
}

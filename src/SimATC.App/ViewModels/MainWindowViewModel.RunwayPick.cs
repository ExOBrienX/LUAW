using CommunityToolkit.Mvvm.ComponentModel;
using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Elegir la pista en las pestañas Salida y Llegada del plan (ADR-127, pedido de Marco 2026-11-11): todas las cabeceras,
/// con un punto verde en las que están en uso según el clima (como el EFB del simulador), su rol y el viento. Elegir una
/// cambia la pista del plan: se vuelven a evaluar la SID, la STAR y la aproximación, y el radar y el mapa las siguen. El
/// ATC la asigna si está en uso; si no, da la que corresponda y aquí se avisa.
/// </summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    public partial IReadOnlyList<RunwayOption> DepartureRunwayOptions { get; set; } = [];

    [ObservableProperty]
    public partial RunwayOption? SelectedDepartureRunway { get; set; }

    [ObservableProperty]
    public partial bool CanPickDepartureRunway { get; set; }

    [ObservableProperty]
    public partial string DepartureRunwayHint { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<RunwayOption> ArrivalRunwayOptions { get; set; } = [];

    [ObservableProperty]
    public partial RunwayOption? SelectedArrivalRunway { get; set; }

    [ObservableProperty]
    public partial bool CanPickArrivalRunway { get; set; }

    [ObservableProperty]
    public partial string ArrivalRunwayHint { get; set; } = "";

    private string? _runwayPickKey;
    private bool _syncingRunwayPick;
    // El cambio de la pista de llegada no rehace la sesión del ATC (no perder la autorización ya dada).
    private bool _planEditKeepsSession;
    // El piloto eligió la pista en las pestañas: no cambia el plan "como se cargó" (ADR-140).
    private bool _pickingRunway;

    partial void OnSelectedDepartureRunwayChanged(RunwayOption? value)
    {
        if (_syncingRunwayPick || value is null || Plan is not { } p || !CanPickDepartureRunway
            || string.Equals(value.Ident, p.Origin.PlannedRunway, StringComparison.OrdinalIgnoreCase) && !_useAtcDeparture)
            return;
        _useAtcDeparture = false;
        Log(Tr.F("Plan: pista de salida {0} → {1} (elegida en el plan).", p.Origin.PlannedRunway ?? "—", value.Ident));
        _pickingRunway = true;
        try
        {
            Plan = p with { Origin = p.Origin with { PlannedRunway = value.Ident } };
        }
        finally
        {
            _pickingRunway = false;
        }
        _ = RefreshProceduresAsync();
    }

    partial void OnSelectedArrivalRunwayChanged(RunwayOption? value)
    {
        if (_syncingRunwayPick || value is null || !CanPickArrivalRunway)
            return;
        SetPlanArrivalRunway(value.Ident, Tr.T("elegida en el plan"));
    }

    /// <summary>
    /// Cambia la pista de llegada del plan sin rehacer la sesión del ATC: la STAR, la aproximación, el radar y la pestaña
    /// Llegada la siguen. También al aceptar la pista en uso que propone el aviso de cambio de pista (ADR-140: antes el botón
    /// solo cerraba el aviso y el plan seguía con la anterior).
    /// </summary>
    private void SetPlanArrivalRunway(string ident, string why)
    {
        if (Plan is not { } p || string.Equals(ident, p.Destination.PlannedRunway, StringComparison.OrdinalIgnoreCase))
            return;
        Log(Tr.F("Plan: pista de llegada {0} → {1} ({2}).", p.Destination.PlannedRunway ?? "—", ident, why));
        _requestedRunway = null;
        _resumeRunway = null;
        _planEditKeepsSession = true;
        _pickingRunway = true;
        try
        {
            Plan = p with { Destination = p.Destination with { PlannedRunway = ident } };
        }
        finally
        {
            _planEditKeepsSession = false;
            _pickingRunway = false;
        }
        _approachesFor = null; // las aproximaciones se vuelven a evaluar para la pista nueva
        _ = RefreshProceduresAsync();
    }

    /// <summary>Opciones, selección y avisos al día. Barato si nada cambió (se llama con cada muestra de viento).</summary>
    private void RefreshRunwayPick()
    {
        if (Plan is not { } p)
        {
            if (_runwayPickKey is null)
                return;
            _runwayPickKey = null;
            SetRunwayPick([], null, false, "", [], null, false, "");
            return;
        }
        var origin = _originData?.Icao == p.Origin.Icao ? _originData : Airport?.Icao == p.Origin.Icao ? Airport : null;
        var dest = _destData?.Icao == p.Destination.Icao ? _destData : null;
        var phase = _session?.Phase;
        var cleared = _session?.Clearance;

        // Origen: la pista en uso solo se conoce si es el aeropuerto activo.
        var (oWind, oArr, oDep, oFrom, oKt) = InUseAt(origin);
        var (dWind, dArr, dDep, dFrom, dKt) = InUseAt(dest);
        var atcArrival = phase is >= PilotPhase.EnRoute and < PilotPhase.Landed ? _arrival?.Runway : null;
        var key = string.Join('|', origin?.Icao, string.Join(',', oWind), string.Join(',', oArr), string.Join(',', oDep), Bucket(oFrom, 10),
            Bucket(oKt, 1), p.Origin.PlannedRunway, cleared?.Runway, ChosenAtcRunway, phase, dest?.Icao, string.Join(',', dWind),
            string.Join(',', dArr), string.Join(',', dDep), Bucket(dFrom, 10), Bucket(dKt, 1), p.Destination.PlannedRunway, atcArrival,
            _settings.UsePlanRunways);
        if (key == _runwayPickKey)
            return;
        _runwayPickKey = key;

        var depOptions = origin is null ? [] : RunwayOptions.For(origin, oWind, oArr, oDep, oFrom, oKt);
        var depIdent = cleared?.Runway ?? ChosenAtcRunway ?? p.Origin.PlannedRunway;
        var canDep = origin is not null && cleared is null && phase is null or PilotPhase.Parked;
        var arrOptions = dest is null ? [] : RunwayOptions.For(dest, dWind, dArr, dDep, dFrom, dKt);
        var arrIdent = atcArrival ?? p.Destination.PlannedRunway;
        var canArr = dest is not null && phase is null or <= PilotPhase.EnRoute;
        SetRunwayPick(depOptions, Find(depOptions, depIdent), canDep, DepartureHint(p, origin, depOptions, depIdent, cleared, oWind, oDep),
            arrOptions, Find(arrOptions, arrIdent), canArr, ArrivalHint(p, dest, arrOptions, atcArrival, dWind, dArr));
    }

    private void SetRunwayPick(IReadOnlyList<RunwayOption> dep, RunwayOption? depSelected, bool canDep, string depHint,
        IReadOnlyList<RunwayOption> arr, RunwayOption? arrSelected, bool canArr, string arrHint)
    {
        _syncingRunwayPick = true;
        try
        {
            DepartureRunwayOptions = dep;
            SelectedDepartureRunway = depSelected;
            CanPickDepartureRunway = canDep;
            DepartureRunwayHint = depHint;
            ArrivalRunwayOptions = arr;
            SelectedArrivalRunway = arrSelected;
            CanPickArrivalRunway = canArr;
            ArrivalRunwayHint = arrHint;
        }
        finally
        {
            _syncingRunwayPick = false;
        }
    }

    /// <summary>
    /// Qué se usa ahora en ese aeropuerto y con qué viento: si es el activo, su selector y su reparto con el viento del
    /// simulador; si es el destino lejano, su pista por el METAR de internet. Sin datos, nada (ningún punto verde).
    /// </summary>
    private (IReadOnlyList<string> Wind, IReadOnlyList<string> Arrivals, IReadOnlyList<string> Departures, double? FromDeg, double? Kt) InUseAt(
        AirportData? airport)
    {
        if (airport is null)
            return ([], [], [], null, null);
        if (airport.Icao == Airport?.Icao)
        {
            if (_runwaySelector?.Current is not { } cur)
                return ([], [], [], Weather?.WindFromTrueDeg, Weather?.WindKt);
            return (cur.Ends, ArrivalEndsNow(), DepartureEnds, Weather?.WindFromTrueDeg, Weather?.WindKt);
        }
        if (_destSelector is { Current: { } far } && _destSelectorFor == airport.Icao && MetarFor(airport.Icao) is { } m)
        {
            // Con el reparto del destino (ADR-129): el punto verde de llegadas va en las pistas donde se aterriza de verdad.
            var ends = airport.Icao == _arrivalAirport?.Icao ? DestinationEnds() : null;
            return (far.Ends, ends?.Arrivals ?? [], ends?.Departures ?? [], m.WindFromTrueDeg ?? 0, m.WindFromTrueDeg is null ? 0 : m.WindKt);
        }
        return ([], [], [], null, null);
    }

    private static string Bucket(double? value, double step) => value is { } v ? Math.Round(v / step).ToString("0") : "";

    private static RunwayOption? Find(IReadOnlyList<RunwayOption> options, string? ident)
    {
        foreach (var o in options)
        {
            if (string.Equals(o.Ident, ident, StringComparison.OrdinalIgnoreCase))
                return o;
        }
        return null;
    }

    private string DepartureHint(Core.Plan.FlightPlan p, AirportData? origin, IReadOnlyList<RunwayOption> options, string? ident,
        ClearanceData? cleared, IReadOnlyList<string> windEnds, IReadOnlyList<string> departures)
    {
        if (origin is null)
            return "";
        if (cleared is not null)
            return Tr.F("Autorizado por la {0}. Para salir por otra, pídesela al ATC.", cleared.Runway);
        if (origin.Icao != Airport?.Icao)
            return Tr.F("No estás en {0}: la pista en uso se verá al cargar ese aeropuerto.", origin.Icao);
        if (windEnds.Count == 0)
            return Tr.T("Todavía no hay pista en uso: falta el viento del aeropuerto.");
        if (Find(options, ident) is not { } chosen)
            return Tr.F("El plan no trae pista de salida: el ATC te dará la {0}. El punto verde marca las que están en uso.", string.Join("/", departures));
        if (!_settings.UsePlanRunways && !_useAtcDeparture)
            return Tr.F("«Usar las pistas del plan» está apagado: el ATC te dará la {0}.", string.Join("/", departures));
        if (!chosen.InUse)
            return Tr.F("⚠ La {0} no está en uso con este viento: el ATC te dará la {1}. ", chosen.Ident, string.Join("/", departures)) +
                   Tr.T("Elige una con punto verde.");
        return departures.Contains(chosen.Ident, StringComparer.OrdinalIgnoreCase)
            ? Tr.F("La {0} está en uso para salidas: el ATC te la dará en la autorización.", chosen.Ident)
            : Tr.F("La {0} está en uso: el ATC te la dará. Los IA salen por la {1} y aterrizan por esta; ", chosen.Ident, string.Join("/", departures)) +
              Tr.T("la Torre te intercala entre las llegadas.");
    }

    private string ArrivalHint(Core.Plan.FlightPlan p, AirportData? dest, IReadOnlyList<RunwayOption> options, string? atcArrival,
        IReadOnlyList<string> windEnds, IReadOnlyList<string> arrivals)
    {
        if (dest is null)
            return "";
        if (atcArrival is not null)
            return Tr.F("El ATC te asignó la {0}. Para otra, pídela por radio (\"request runway …\").", atcArrival);
        if (windEnds.Count == 0)
            return Tr.F("Sin viento de {0} todavía: el punto verde aparece cuando llega su METAR.", dest.Icao);
        var inUse = arrivals.Count > 0 ? arrivals : windEnds;
        if (Find(options, p.Destination.PlannedRunway) is not { } chosen)
            return Tr.F("El plan no trae pista de llegada: ahora en {0} se usa la {1}.", dest.Icao, string.Join("/", inUse));
        return chosen.InUse
            ? Tr.F("La {0} está en uso en {1} ahora: si sigue así al llegar, el ATC te la asigna.", chosen.Ident, dest.Icao)
            : Tr.F("⚠ La {0} no está en uso en {1} ahora: al llegar, el ATC te dará la que esté en uso ({2}).", chosen.Ident, dest.Icao, string.Join("/", inUse));
    }
}

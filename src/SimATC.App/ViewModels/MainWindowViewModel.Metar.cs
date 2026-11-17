using Avalonia.Threading;
using SimATC.Core.Airports;
using SimATC.Core.Atc;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// METAR de internet (ADR-100): el viento de los aeropuertos lejanos (el activo cuando el piloto no está en él, y el destino
/// del plan) para elegir la pista en uso desde lejos. El simulador no da el clima de otro lugar, y con su clima en vivo el
/// METAR real es el mismo. Sin internet, todo sigue como antes (pista provisional, ADR-089).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<string, Metar> _metars = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _metarCts = new();
    private bool _metarFailed;

    /// <summary>Cada cuánto se piden los METAR (min); se publican cada 30 o 60 min.</summary>
    public const int MetarEveryMin = 5;

    /// <summary>Un METAR más viejo que esto (min) ya no se usa.</summary>
    public const int MetarMaxAgeMin = 90;

    // Pista en uso del destino del plan (cuando no es el aeropuerto activo), por el viento de su METAR.
    private ActiveRunwaySelector? _destSelector;
    private string? _destSelectorFor;
    private DateTime _destUpdatedUtc;

    /// <summary>Los aeropuertos cuyo METAR hace falta: el activo, el origen y el destino del plan.</summary>
    private List<string> MetarAirports()
    {
        var list = new List<string>();
        void Add(string? icao)
        {
            if (icao is { Length: 4 } i && !list.Contains(i, StringComparer.OrdinalIgnoreCase))
                list.Add(i);
        }
        Add(Airport?.Icao);
        Add(Plan?.Origin.Icao);
        Add(Plan?.Destination.Icao);
        return list;
    }

    private async Task MetarLoopAsync(CancellationToken ct)
    {
        var client = new MetarClient(Http);
        var last = DateTime.MinValue;
        var lastSet = "";
        while (!ct.IsCancellationRequested)
        {
            var icaos = await Dispatcher.UIThread.InvokeAsync(MetarAirports);
            var set = string.Join(",", icaos.Order());
            // Cada MetarEveryMin, o enseguida si cambiaron los aeropuertos (plan nuevo, aeropuerto activo nuevo).
            if (icaos.Count > 0 && (set != lastSet || DateTime.UtcNow - last > TimeSpan.FromMinutes(MetarEveryMin)))
            {
                last = DateTime.UtcNow;
                lastSet = set;
                try
                {
                    var got = await client.FetchAsync(icaos, DateTime.UtcNow, ct);
                    Dispatcher.UIThread.Post(() => OnMetars(got, icaos));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_metarFailed)
                            Log(Tr.F("Sin METAR ({0}): sin internet, la pista de los aeropuertos lejanos queda provisional (la de tu plan).", ex.Message));
                        _metarFailed = true;
                    });
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnMetars(IReadOnlyDictionary<string, Metar> got, IReadOnlyList<string> asked)
    {
        if (_metarFailed)
            Log(Tr.T("METAR de internet otra vez disponible."));
        _metarFailed = false;
        foreach (var (icao, m) in got)
        {
            if (!_metars.TryGetValue(icao, out var old) || old.Raw != m.Raw)
                Log($"METAR {m.Raw}");
            _metars[icao] = m;
        }
        foreach (var icao in asked)
        {
            if (!got.ContainsKey(icao))
                Log(Tr.F("{0}: sin METAR publicado; su pista queda por el viento local o provisional.", icao));
        }
        UpdateDestinationRunway();
        UpdateBriefing(); // el METAR actual y la comparación con el del OFP (ADR-101)
    }

    /// <summary>El METAR reciente de ese aeropuerto, o null.</summary>
    private Metar? MetarFor(string? icao) =>
        icao is not null && _metars.TryGetValue(icao, out var m) && DateTime.UtcNow - m.TimeUtc < TimeSpan.FromMinutes(MetarMaxAgeMin) ? m : null;

    /// <summary>La observación del aeropuerto activo desde su METAR, cuando el piloto no está en él (ADR-100).</summary>
    private WeatherObservation? MetarObservation(AirportData airport, double magVarDeg) =>
        MetarFor(airport.Icao) is { } m ? WeatherObservation.FromMetar(m, magVarDeg) : null;

    /// <summary>
    /// La pista en uso del destino por el viento de su METAR (si no es el aeropuerto activo, que tiene su propio selector).
    /// Si cambió, la llegada del piloto se rearma y el ATC se la dice ("expect ILS approach runway 32L").
    /// </summary>
    private void UpdateDestinationRunway()
    {
        if (_arrivalAirport is not { } dest || dest.Icao == Airport?.Icao || MetarFor(dest.Icao) is not { } m)
            return;
        if (_destSelector is null || _destSelectorFor != dest.Icao)
        {
            _destSelector = new ActiveRunwaySelector(dest, _arrivalAtc?.PreferredRunways);
            _destSelectorFor = dest.Icao;
            _destUpdatedUtc = default;
        }
        var now = DateTime.UtcNow;
        var elapsed = _destUpdatedUtc == default ? 0 : (now - _destUpdatedUtc).TotalSeconds;
        _destUpdatedUtc = now;
        _destSelector.Planned = _settings.UsePlanRunways ? Plan?.Destination.PlannedRunway : null; // viento calmo (ADR-127)
        if (_destSelector.Update(m.WindFromTrueDeg ?? 0, m.WindVariable ? 0 : m.WindKt, elapsed) && _destSelector.Current is { } cur)
        {
            Log(Tr.F("Pista en uso {0} (METAR): {1} ({2}).", dest.Icao, cur.Label, cur.Reason));
            _requestedRunway = null; // la pedida antes podía quedar con viento de cola
        }
    }

    /// <summary>
    /// Pista de llegada del piloto según la pista en uso del destino (ADR-100): la del plan si está en uso; si no, la
    /// primera en uso. null sin pista en uso conocida (se queda la del plan).
    /// </summary>
    private string? DestinationRunwayInUse(string? planned)
    {
        if (_arrivalAirport is not { } dest)
            return null;
        IReadOnlyList<string>? ends = dest.Icao == Airport?.Icao && _runwaySelector?.Current is not null && ArrivalEnds.Count > 0
            ? ArrivalEnds
            : DestinationEnds()?.Arrivals;
        if (ends is not { Count: > 0 })
            return null;
        // Solo cabeceras con aproximación publicada (ADR-129: en LEMD las 14 son de despegue).
        ends = RunwayPlanner.WithApproach(dest, ends);
        return planned is not null && ends.Contains(planned, StringComparer.OrdinalIgnoreCase) ? planned : ends[0];
    }

    private (string Key, RunwayPlan Plan)? _destPlan;

    /// <summary>
    /// Qué pistas usa el destino lejano para aterrizar y para despegar (ADR-129): con el viento de su METAR y, si ya se
    /// tiene su rodaje, el mismo reparto que usará cuando sea el aeropuerto activo (antes solo se miraba el viento: a Marco
    /// le salía la 14L de LEMD para aterrizar, y al llegar el reparto real eran las 18). Si las del viento no tienen ninguna
    /// aproximación publicada, las que sí la tienen en ese sentido. null sin pista en uso conocida.
    /// </summary>
    private (IReadOnlyList<string> Arrivals, IReadOnlyList<string> Departures)? DestinationEnds()
    {
        if (_arrivalAirport is not { } dest || _destSelector is not { Current: { } cur } || _destSelectorFor != dest.Icao)
            return null;
        IReadOnlyList<string> arrivals = cur.Ends, departures = cur.Ends;
        if (_arrivalGraph is { } graph && MetarFor(dest.Icao) is { } m)
        {
            var demand = _director?.ArrivalsPerHour(dest, EffectiveDensity) ?? 0;
            var planArr = PlanRunwaysAt(dest.Icao, asLoaded: true).Arrival;
            var key = $"{dest.Icao}:{cur.Label}:{Math.Round(demand / 8)}:{planArr}";
            if (_destPlan?.Key != key)
            {
                var plan = RunwayPlanner.Plan(dest, graph, cur.Ends, m.WindFromTrueDeg ?? 0, m.WindVariable ? 0 : m.WindKt,
                    arrivalsPerHour: demand, departuresPerHour: demand, planArrival: planArr);
                _destPlan = (key, plan);
                Log(Tr.F("Pistas {0} (destino, {1:0} por hora): {2}.", dest.Icao, demand, plan.Reason));
            }
            (arrivals, departures) = (_destPlan.Value.Plan.Arrival, _destPlan.Value.Plan.Departure);
        }
        if (RunwayPlanner.ArrivalAlternatives(dest, arrivals) is { Count: > 0 } alternatives)
            arrivals = alternatives;
        return (RunwayPlanner.WithApproach(dest, arrivals), departures);
    }
}

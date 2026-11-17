using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Una fila del historial de vuelos.</summary>
public sealed record FlightRow(string Route, string When, string Times, string Detail);

/// <summary>
/// Fin del vuelo y "Mis vuelos" (ADR-088): al apagar motores en el puesto se cierra el vuelo, se guarda el log (motor y las
/// tres radios), se agrega al historial y se muestra el resumen. "Cerrar vuelo" quita el plan (y su ruta del radar) para
/// cargar el siguiente.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private FlightRecorder? _recorder;
    private PilotSession? _recorderSession;

    public bool IsFlights => Panel == "flights";

    /// <summary>Resumen del último vuelo terminado (filas Dato: valor), vacío si no hay.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DiagItem> LastFlight { get; set; } = [];

    [ObservableProperty]
    public partial string LastFlightTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool HasLastFlight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoFlightHistory))]
    public partial IReadOnlyList<FlightRow> FlightHistory { get; set; } = [];

    public bool NoFlightHistory => FlightHistory.Count == 0;

    private void InitFlights()
    {
        ShowHistory(FlightLogbook.Load(FlightLogbook.PathFor(Core.AppPaths.Root)));
        Core.Engine.TrafficEngine.SetInjectRadiusNm(_settings.InjectRadiusNm); // radio de inyección guardado (ADR-089)
    }

    /// <summary>Del reloj del ATC (1 s): registra el vuelo y lo cierra al apagar motores en el puesto.</summary>
    private void UpdateFlightRecorder(PilotSession s)
    {
        if (!ReferenceEquals(s, _recorderSession))
        {
            _recorderSession = s;
            _recorder = new FlightRecorder();
        }
        if (UserState is not { } u || Plan is not { } plan || _recorder is null)
            return;
        var record = _recorder.Update(u, s.Phase, DateTime.UtcNow, () => new FlightRecord(plan.Callsign, plan.AircraftIcao, plan.Origin.Icao,
            plan.Destination.Icao, s.Clearance?.Runway, s.ArrivalRunway, s.ArrivalStand, null, null, null, default, 0, 0));
        if (record is null)
            return;
        var logPath = SaveFlightLog(plan.Callsign);
        try
        {
            ShowHistory(FlightLogbook.Add(FlightLogbook.PathFor(Core.AppPaths.Root), record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(Tr.F("No se pudo guardar el historial de vuelos ({0}).", ex.Message));
        }
        ShowSummary(record, logPath);
        Log(Tr.F("Vuelo terminado: {0} → {1}, {2:0} NM{3}.", record.Origin, record.Destination, record.DistanceNm, (logPath is null ? "" : Tr.F("; log en {0}", logPath))));
        Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System,
            Tr.T("Vuelo terminado. El resumen está en \"Mis vuelos\"; con \"Cerrar vuelo\" se quita el plan para cargar el siguiente.")));
        Panel = "flights";
    }

    private void ShowSummary(FlightRecord r, string? logPath)
    {
        LastFlightTitle = $"{r.Callsign} · {r.Origin} → {r.Destination}";
        var rows = new List<DiagItem>
        {
            new(Tr.T("Avión"), r.Aircraft),
            new(Tr.T("Salida"), Tr.F("{0} del puesto · despegue {1} por la {2}", Time(r.OffBlockUtc), Time(r.TakeoffUtc), r.DepartureRunway ?? "—")),
            new(Tr.T("Llegada"), Tr.F("aterrizaje {0} en la {1} · puesto {2} a las {3}", Time(r.LandingUtc), r.ArrivalRunway ?? "—", r.ArrivalStand ?? "—", Time(r.OnBlockUtc))),
            new(Tr.T("Tiempo de vuelo"), Span(r.AirTime)),
            new(Tr.T("Tiempo de calzos"), Span(r.BlockTime)),
            new(Tr.T("Distancia volada"), $"{r.DistanceNm:N0} NM"),
            new(Tr.T("Altitud máxima"), $"{r.MaxAltitudeFt:N0} ft"),
        };
        if (logPath is not null)
            rows.Add(new(Tr.T("Log del vuelo"), logPath));
        LastFlight = rows;
        HasLastFlight = true;
    }

    private void ShowHistory(List<FlightRecord> all) =>
        FlightHistory = [.. all.Select(r => new FlightRow($"{r.Callsign}  {r.Origin} → {r.Destination}",
            r.OnBlockUtc.ToLocalTime().ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture),
            Tr.F("vuelo {0} · calzos {1}", Span(r.AirTime), Span(r.BlockTime)),
            Tr.F("{0} · {1:N0} NM · pistas {2} / {3}", r.Aircraft, r.DistanceNm, r.DepartureRunway ?? "—", r.ArrivalRunway ?? "—")))];

    private static string Time(DateTime? utc) => utc is { } t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) + " Z" : "—";

    private static string Span(TimeSpan? t) => t is { } s ? Tr.F("{0} h {1:00} min", (int)s.TotalHours, s.Minutes) : "—";

    /// <summary>Cierra el vuelo terminado: quita el plan (y su ruta del radar) para cargar el siguiente.</summary>
    [RelayCommand]
    private void CloseFlight()
    {
        HasLastFlight = false;
        LastFlight = [];
        UserRoute = null;
        ClearPlan();
        Panel = "plan";
    }
}

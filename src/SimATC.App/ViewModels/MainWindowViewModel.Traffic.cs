using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Radar;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Traffic;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Datos de una fila del panel de tráfico, calculados en cada refresco.</summary>
public sealed record TrafficRowData(uint ObjectId, string Callsign, string Type, string Airline, string From, string Phase,
    string Runway, string Hold, string Distance, bool Waiting, bool IsParked, bool IsSelected);

/// <summary>
/// Una fila del panel de tráfico. Se actualiza en su lugar (no se recrea en cada refresco): si la fila y sus botones se
/// reemplazaran 5 veces por segundo, un clic que empieza en un botón y termina en su reemplazo se perdería.
/// </summary>
public sealed partial class TrafficRow(uint objectId) : ObservableObject
{
    public uint ObjectId { get; } = objectId;

    [ObservableProperty] public partial string Callsign { get; set; } = "";
    [ObservableProperty] public partial string Type { get; set; } = "";
    [ObservableProperty] public partial string Airline { get; set; } = "";
    [ObservableProperty] public partial string From { get; set; } = "";
    [ObservableProperty] public partial string Phase { get; set; } = "";
    [ObservableProperty] public partial string Runway { get; set; } = "";
    [ObservableProperty] public partial string Hold { get; set; } = "";
    [ObservableProperty] public partial string Distance { get; set; } = "";
    [ObservableProperty] public partial bool Waiting { get; set; }
    [ObservableProperty] public partial bool IsParked { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    /// <summary>Fila desplegada con la ficha completa (clic en la fila).</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial IReadOnlyList<InspectorRow> DetailRows { get; set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixes))]
    public partial IReadOnlyList<WaypointRow> Fixes { get; set; } = [];
    public bool HasFixes => Fixes.Count > 0;

    public void Apply(TrafficRowData d)
    {
        Callsign = d.Callsign;
        Type = d.Type;
        Airline = d.Airline;
        From = d.From;
        Phase = d.Phase;
        Runway = d.Runway;
        Hold = d.Hold;
        Distance = d.Distance;
        Waiting = d.Waiting;
        IsParked = d.IsParked;
        IsSelected = d.IsSelected;
    }
}

/// <summary>Qué aviones muestra la tabla de tráfico.</summary>
public enum TrafficFilter
{
    All,
    Moving,
    Waiting,
    Parked,
}

/// <summary>Panel Tráfico: tabla filtrable, acciones por avión, salidas en lote y congelar el tráfico.</summary>
public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<Choice<TrafficFilter>> TrafficFilters { get; } =
    [
        new(TrafficFilter.All, Tr.T("Todos")),
        new(TrafficFilter.Moving, Tr.T("En movimiento")),
        new(TrafficFilter.Waiting, Tr.T("Esperando")),
        new(TrafficFilter.Parked, Tr.T("Estacionados")),
    ];

    [ObservableProperty]
    public partial Choice<TrafficFilter> SelectedTrafficFilter { get; set; } = null!;

    [ObservableProperty]
    public partial string TrafficSearch { get; set; } = "";

    public ObservableCollection<TrafficRow> TrafficRows { get; } = [];

    [ObservableProperty]
    public partial string TrafficCounts { get; set; } = "";

    [ObservableProperty]
    public partial string TrafficSummary { get; set; } = Tr.T("Sin tráfico");

    public bool HasNoTrafficRows => TrafficRows.Count == 0;

    partial void OnSelectedTrafficFilterChanged(Choice<TrafficFilter> value) => RefreshTraffic();

    partial void OnTrafficSearchChanged(string value) => RefreshTraffic();

    /// <summary>Salidas en lote: cuántas y cada cuántos segundos.</summary>
    [ObservableProperty]
    public partial decimal LaunchCount { get; set; } = 3;

    [ObservableProperty]
    public partial decimal LaunchSpacing { get; set; } = LaunchSpacingS;

    /// <summary>Congela el tráfico IA (el simulador sigue): para mirar con calma una situación.</summary>
    public bool TrafficPaused
    {
        get => _engine?.Paused ?? false;
        set
        {
            if (_engine is null)
                return;
            _engine.Paused = value;
            OnPropertyChanged();
            Log(value ? Tr.T("Tráfico IA congelado.") : Tr.T("Tráfico IA reanudado."));
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseTraffic))]
    private async Task LaunchBatchAsync()
    {
        var n = (int)LaunchCount;
        var spacing = TimeSpan.FromSeconds((double)LaunchSpacing);
        for (var i = 0; i < n; i++)
        {
            if (i > 0)
                await Task.Delay(spacing);
            if (!await TryLaunchOneAsync())
                return;
        }
    }

    /// <summary>Hace salir ese avión estacionado por la cabecera elegida.</summary>
    [RelayCommand]
    private async Task LaunchThisAsync(TrafficRow row)
    {
        if (CanUseTraffic() && row.IsParked)
            await TryLaunchOneAsync(row.ObjectId);
    }

    /// <summary>Quita un solo avión (estacionado o en movimiento) sin tocar los demás.</summary>
    [RelayCommand]
    private void RemoveAircraft(TrafficRow row)
    {
        if (row.IsParked)
        {
            if (_parked.Release(row.ObjectId) is not null)
                _bridge.Remove(row.ObjectId);
        }
        else
        {
            _engine?.RequestRemove(row.ObjectId);
        }
        if (SelectedId == row.ObjectId)
            SelectedId = null;
        Log(Tr.F("{0}: quitado desde la tabla de tráfico.", row.Callsign));
        RefreshTraffic();
    }

    /// <summary>Selecciona el avión y lo muestra en el radar (con "Seguir" activado).</summary>
    [RelayCommand]
    private void ShowOnRadar(TrafficRow row)
    {
        SelectedId = row.ObjectId;
        FollowSelected = true;
        Panel = "radar";
    }

    private void UpdateTrafficTable(IReadOnlyList<InjectedParked> parked, AgentSnapshot[] moving)
    {
        var filter = SelectedTrafficFilter?.Value ?? TrafficFilter.All;
        var search = TrafficSearch.Trim();
        var rows = new List<TrafficRowData>(parked.Count + moving.Length);
        var air = 0;
        var virtualCount = 0;
        var waitingCount = 0;
        foreach (var a in moving)
        {
            var waiting = a.Hold != HoldKind.None && a.SpeedKt < 1;
            if (a.Airborne && a.Virtual)
                virtualCount++;
            else if (a.Airborne || a.AglFt > 20)
                air++;
            if (a.Airborne)
            {
                if (filter is TrafficFilter.All or TrafficFilter.Moving)
                    AddRow(rows, search, new TrafficRowData(a.ObjectId, a.Callsign, a.Type ?? "", a.Callsign.Length >= 3 ? a.Callsign[..3] : "",
                        $"{a.Origin}→{a.Destination}", $"{PhaseText(a.Phase)} · {FlightLevelText(a.AltitudeFt)}{(a.Virtual ? " · virtual" : "")}",
                        "", "—", DistanceText(a.Position), false, false, SelectedId == a.ObjectId));
                continue;
            }
            if (waiting)
                waitingCount++;
            if (filter == TrafficFilter.Parked || (filter == TrafficFilter.Waiting && !waiting))
                continue;
            var info = _flights.TryGetValue(a.ObjectId, out var f) ? f : default;
            AddRow(rows, search, new TrafficRowData(a.ObjectId, a.Callsign, info.Aircraft?.Type ?? "", info.Aircraft?.Airline ?? "",
                info.Aircraft is { } pa ? RadarView.StandName(pa.Parking) : "", PhaseText(a.Phase), info.Runway ?? "",
                waiting ? HoldText(a.Hold, a.HoldFor) : "—", DistanceText(a.Position), waiting, false, SelectedId == a.ObjectId));
        }
        if (filter is TrafficFilter.All or TrafficFilter.Parked)
        {
            foreach (var p in parked)
            {
                var phase = _director?.ScheduleFor(p.ObjectId) is { } sch
                    ? Tr.F("{0} → {1} · sale {2:HH:mm} UTC", sch.Callsign, sch.Destination, sch.Std)
                    : p.Aircraft.Decor ? Tr.T("decoración (no sale)")
                    : p.IsParkedAtc ? Tr.T("estacionado (pasarela)") : "estacionado";
                // La decoración no se puede lanzar (ADR-109): sin el botón de salida.
                AddRow(rows, search, new TrafficRowData(p.ObjectId, p.Aircraft.TailNumber, p.Aircraft.Type,
                    p.Aircraft.Airline.Length > 0 ? p.Aircraft.Airline : "GA",
                    RadarView.StandName(p.Aircraft.Parking), phase, "", "—",
                    DistanceText(p.Position), false, !p.Aircraft.Decor, SelectedId == p.ObjectId));
            }
        }
        KeepTrafficLimits();
        if (IsTrafficPanel)
        {
            UpdateBreakdowns(parked);
            SyncRows(rows);
            foreach (var row in TrafficRows)
            {
                row.IsExpanded = row.ObjectId == _expandedId;
                if (row.IsExpanded)
                    (row.DetailRows, row.Fixes) = Details(row.ObjectId, parked);
            }
        }

        var ground = parked.Count + moving.Length - air - virtualCount;
        TrafficSummary = parked.Count + moving.Length == 0 ? Tr.T("Sin tráfico")
            : Tr.F("{0} en tierra · {1} en el aire · {2} lejos (virtuales)", ground, air, virtualCount);
        TrafficCounts = Tr.F("Rodando {0}/{1} · Aire {2}/{3} · ", Math.Max(0, ground - parked.Count), _settings.MaxGround, air, AirMax) +
                        Tr.F("{0} en movimiento · {1} esperando · {2} estacionados · ", moving.Length - virtualCount, waitingCount, parked.Count) +
                        Tr.F("{0} virtuales", virtualCount) + (_director is { } d ? " · " + d.Status : "");
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(AirportText));
    }

    /// <summary>Lleva la tabla a <paramref name="rows"/> moviendo, agregando o quitando filas; las que siguen se actualizan.</summary>
    private void SyncRows(List<TrafficRowData> rows)
    {
        var hadRows = TrafficRows.Count > 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var d = rows[i];
            if (i < TrafficRows.Count && TrafficRows[i].ObjectId == d.ObjectId)
            {
                TrafficRows[i].Apply(d);
                continue;
            }
            var existing = -1;
            for (var j = i + 1; j < TrafficRows.Count; j++)
            {
                if (TrafficRows[j].ObjectId == d.ObjectId)
                {
                    existing = j;
                    break;
                }
            }
            if (existing >= 0)
            {
                TrafficRows.Move(existing, i);
            }
            else
            {
                TrafficRows.Insert(i, new TrafficRow(d.ObjectId));
            }
            TrafficRows[i].Apply(d);
        }
        while (TrafficRows.Count > rows.Count)
            TrafficRows.RemoveAt(TrafficRows.Count - 1);
        if (hadRows != TrafficRows.Count > 0)
            OnPropertyChanged(nameof(HasNoTrafficRows));
    }

    private static void AddRow(List<TrafficRowData> rows, string search, TrafficRowData row)
    {
        if (search.Length == 0 || row.Callsign.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            row.Type.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Airline.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            row.From.Contains(search, StringComparison.OrdinalIgnoreCase))
            rows.Add(row);
    }

    private string DistanceText(GeoPoint position) =>
        UserState is { } u ? $"{GeoMath.DistanceM(u.Position, position) / UnitConversions.MetersPerNauticalMile:0.0} NM" : "";
}

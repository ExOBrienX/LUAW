using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Radar;
using SimATC.Core.Engine;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Traffic;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Un fijo de la ruta de un IA en su ficha: restricción de la carta, altitud prevista, hora estimada y si ya pasó.</summary>
public sealed record WaypointRow(string Name, string Constraint, string Planned, string Eta, bool Passed, string Leg = "");

/// <summary>
/// Ficha completa de un avión IA (prueba de Marco, 2026-11-01): para diagnosticar si respeta las restricciones de altitud y
/// velocidad, ver su ruta (lo recorrido y lo que falta) y su horario. Se usa en el radar y en la fila desplegada de Tráfico.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Ruta del IA elegido en el radar (recorrido, por recorrer, fijos y puesto).</summary>
    [ObservableProperty]
    public partial SelectedFlightView? SelectedFlight { get; set; }

    private uint? _expandedId;

    /// <summary>Clic en una fila de Tráfico: la despliega con su ficha (y la elige en el radar); otro clic la cierra.</summary>
    [RelayCommand]
    private void ToggleTrafficDetail(TrafficRow row)
    {
        _expandedId = _expandedId == row.ObjectId ? null : row.ObjectId;
        SelectedId = row.ObjectId;
        RefreshTraffic();
    }

    private const double Nm = UnitConversions.MetersPerNauticalMile;

    /// <summary>Por qué se eligió cada procedimiento y lo que el validador encontró raro (como en el plan del piloto).</summary>
    private static void AddPlanReasons(List<InspectorRow> rows, Core.Flight.PlannedFlight? plan)
    {
        if (plan is null)
            return;
        if (plan.Reasons.Count > 0)
            rows.Add(new(Tr.T("Por qué"), string.Join(Environment.NewLine, plan.Reasons)));
        if (plan.Issues.Count > 0)
            rows.Add(new(Tr.T("Avisos del plan"), string.Join(Environment.NewLine, plan.Issues)));
    }

    /// <summary>Ficha de un avión (en el aire, rodando o estacionado), con los fijos de su ruta si es un vuelo.</summary>
    private (List<InspectorRow> Rows, List<WaypointRow> Fixes) Details(uint id, IReadOnlyList<InjectedParked> parked)
    {
        var rows = new List<InspectorRow>();
        var fixes = new List<WaypointRow>();
        var now = DateTime.UtcNow;
        switch (_engine?.Find(id))
        {
            case AirborneAgent f:
            {
                var path = f.Path;
                // Título, subtítulo y los tres recuadros ya dicen vuelo, fase, altitud y velocidad: aquí, el detalle por grupos.
                var procs = string.Join(" · ", new[] { path.SidName is { } sid ? "SID " + sid : null, path.StarName is { } star ? "STAR " + star : null,
                    path.ApproachName is { } app ? Tr.T("Aproximación ") + app : null }.Where(x => x is not null));
                var profile = f.ProfileAltitudeFt;
                rows.Add(new(Tr.T("Altitud"), double.IsNaN(profile)
                    ? AltBoth(f.AltitudeFt)
                    : Tr.F("{0} · perfil {1} · {2:+0;-0;0} ft", AltBoth(f.AltitudeFt), AltBoth(profile), f.AltitudeFt - profile), Tr.T("Ahora")));
                rows.Add(new(Tr.T("Perfil"), DescentText(f)));
                var ias = f.SpeedKt / (1 + 0.02 * Math.Max(0, f.AltitudeFt) / 1000);
                rows.Add(new(Tr.T("Velocidad"), Tr.F("IAS ≈{0:0} kt · GS {1:0} kt", ias, f.SpeedKt) +
                    (f.ChartSpeedLimitKt > 0 ? Tr.F(" · límite de la carta {0:0} kt", f.ChartSpeedLimitKt) : "") +
                    (f.AssignedSpeedKt > 0 ? Tr.F(" · asignada por el ATC {0:0} kt", f.AssignedSpeedKt) : "")));
                rows.Add(new(Tr.T("Autorizado"), double.IsNaN(f.ClearedAltitudeFt)
                    ? (f.ApproachCleared ? Tr.F("aproximación {0} {1}", f.Path.ApproachWord, f.Landing?.Ident) : "—")
                    : Tr.F("descender a {0}", AltBoth(f.ClearedAltitudeFt))));
                if (!f.Injected)
                    rows.Add(new(Tr.T("Simulador"), Tr.T("virtual (lejos de ti, no está en el simulador)")));
                var eta = now.AddSeconds(f.SecondsToEnd);
                rows.Add(new(Tr.T("Falta"), Tr.F("{0:0} NM · {1:HH:mm} UTC (en {2:0} min)", f.RemainingNm, eta, f.SecondsToEnd / 60),
                    f.Landing is not null ? Tr.F("Llegada a {0}", f.Destination) : Tr.T("Fin de ruta")));
                if (f.Landing is { } l)
                {
                    rows.Add(new(Tr.T("Pista"), Tr.F("{0} · senda {1:0.0}° · salida a {2:0} m del umbral", l.Ident, path.GlideDeg, l.ExitAlongM) +
                        (f.LandingCleared ? Tr.T(" · autorizado a aterrizar") : "")));
                    if (Airport is { } ap && f.StandIndex >= 0 && f.StandIndex < ap.Parkings.Count)
                        rows.Add(new(Tr.T("Puesto"), RadarView.StandName(ap.Parkings[f.StandIndex])));
                }
                if (f.GoArounds > 0)
                    rows.Add(new(Tr.T("Motor y al aire"), f.GoArounds.ToString(CultureInfo.InvariantCulture)));
                rows.Add(new(Tr.T("Procedimientos"), procs.Length > 0 ? procs : Tr.T("directo (sin procedimientos)"), Tr.T("Ruta")));
                if (path.RouteText is { } route)
                    rows.Add(new(Tr.T("Ruta"), route));
                rows.Add(new(Tr.T("Crucero"), AltBoth(f.CruiseAltFt)));
                if (_director?.PlanForCallsign(f.Callsign) is { } fp)
                    rows.Add(new(Tr.T("Fuente"), RouteSourceText(fp.Source)));
                AddPlanReasons(rows, _director?.PlanForCallsign(f.Callsign));
                rows.Add(new(Tr.T("Modelo"), f.Model?.Title ?? "—", Tr.T("Avión")));

                // Fijos de la carta (y el destino), con lo previsto y la hora estimada.
                var here = f.RemainingM;
                var gs = Math.Max(1, f.SpeedKt * 0.514444);
                for (var i = 0; i < path.Count; i++)
                {
                    var p = path[i];
                    if (!p.IsFix)
                        continue;
                    var passed = i < f.TargetIndex;
                    var eta2 = passed ? Tr.T("pasado") : now.AddSeconds(Math.Max(0, here - p.RemainingM) / gs).ToString("HH:mm", CultureInfo.InvariantCulture);
                    fixes.Add(new WaypointRow(p.Name, ConstraintText(p), p.ProfileFt > 0 ? AltBoth(p.ProfileFt) : "—", eta2, passed, LegText(p.Section)));
                }
                break;
            }
            case DepartureAgent d:
            {
                var group = Tr.T("Rodaje");
                if (d.Inbound)
                {
                    if (Airport is { } ap && d.StandIndex >= 0 && d.StandIndex < ap.Parkings.Count)
                        rows.Add(new(Tr.T("Va al puesto"), RadarView.StandName(ap.Parkings[d.StandIndex]), group));
                }
                else
                {
                    rows.Add(new(Tr.T("Pista"), d.RunwayIdent ?? "—", group));
                    if (_director?.LegFor(d.Callsign) is { } leg)
                        rows.Add(new(Tr.T("Destino"), leg.Destination));
                }
                rows.Add(new(Tr.T("Calles"), d.Taxiways.Count > 0 ? string.Join(", ", d.Taxiways) : Tr.T("calles sin nombre"), rows.Count == 0 ? group : null));
                rows.Add(new(Tr.T("Gases"), Tr.F("{0:0} %", d.ThrottlePercent)));
                break;
            }
            default:
                if (parked.FirstOrDefault(p => p.ObjectId == id) is { } pk)
                {
                    var a = pk.Aircraft;
                    rows.Add(new(Tr.T("Puesto"), RadarView.StandName(a.Parking) + (pk.IsParkedAtc ? Tr.T(" · con pasarela") : ""), Tr.T("Avión")));
                    rows.Add(new(Tr.T("Matrícula"), Tr.F("{0} (generada: no es un avión real)", a.TailNumber)));
                    rows.Add(new(Tr.T("Modelo"), a.Match.Model.Title));
                    if (_director?.ScheduleFor(id) is { } s)
                    {
                        var min = (s.Std - now).TotalMinutes;
                        rows.Add(new(Tr.T("Vuelo"), Tr.F("{0} a {1}", s.Callsign, s.Destination), Tr.T("Próximo vuelo")));
                        rows.Add(new(Tr.T("Salida (STD)"), Tr.F("{0:HH:mm} UTC · {1}", s.Std, (min > 0 ? Tr.F("en {0:0} min", min) : Tr.T("saliendo")))));
                        // Su plan completo, armado apenas tuvo vuelo y hora (con la pista en uso).
                        if (_director.PlanFor(id) is { } plan)
                        {
                            var procs = string.Join(" · ", new[] { plan.Sid is { } sid ? "SID " + sid : null, plan.Star is { } star ? "STAR " + star : null,
                                plan.Approach is { } app ? Tr.T("Aproximación ") + app : null }.Where(x => x is not null));
                            rows.Add(new(Tr.T("Procedimientos"), procs.Length > 0 ? procs : Tr.T("directo (sin procedimientos)")));
                            rows.Add(new(Tr.T("Ruta"), plan.RouteText));
                            rows.Add(new(Tr.T("Fuente"), RouteSourceText(plan.Source)));
                            rows.Add(new(Tr.T("Crucero"), AltBoth(plan.CruiseFt)));
                            AddPlanReasons(rows, plan);
                            for (var i = 0; i < plan.Path.Count; i++)
                            {
                                var p = plan.Path[i];
                                if (p.IsFix)
                                    fixes.Add(new WaypointRow(p.Name, ConstraintText(p), p.ProfileFt > 0 ? AltBoth(p.ProfileFt) : "—", "—", false, LegText(p.Section)));
                            }
                        }
                        else
                        {
                            rows.Add(new(Tr.T("Plan"), Tr.T("armándose (se leen los procedimientos del destino)")));
                        }
                    }
                    else
                    {
                        rows.Add(new(Tr.T("Vuelo"), _settings.AutoTraffic ? Tr.T("sin asignar todavía") : Tr.T("tráfico automático apagado"), Tr.T("Próximo vuelo")));
                    }
                }
                break;
        }
        return (rows, fixes);
    }

    /// <summary>La vista del radar del avión elegido: traza recorrida, ruta que le falta, fijos y puesto.</summary>
    private SelectedFlightView? RadarFlightView(uint id)
    {
        switch (_engine?.Find(id))
        {
            case AirborneAgent f:
            {
                var (trail, _) = f.Trail();
                var path = f.Path;
                var ahead = new List<GeoPoint> { f.Position };
                var fixes = new List<PlanPoint>();
                for (var i = Math.Max(0, f.TargetIndex); i < path.Count; i++)
                {
                    ahead.Add(path[i].Position);
                    if (path[i].IsFix)
                        fixes.Add(new PlanPoint(path[i].Position, FixLabel(path[i])));
                }
                GeoPoint? stand = null;
                var radius = 0.0;
                if (Airport is { } ap && f.StandIndex >= 0 && f.StandIndex < ap.Parkings.Count)
                {
                    stand = ap.Parkings[f.StandIndex].Position;
                    radius = ap.Parkings[f.StandIndex].RadiusM;
                }
                return new SelectedFlightView([.. trail, f.Position], ahead, fixes, stand, radius);
            }
            case DepartureAgent { Inbound: true } d when Airport is { } ap && d.StandIndex >= 0 && d.StandIndex < ap.Parkings.Count:
                return new SelectedFlightView([], [], [], ap.Parkings[d.StandIndex].Position, ap.Parkings[d.StandIndex].RadiusM);
            default:
                return null;
        }
    }

    /// <summary>"FL350 · 35.000 ft" sobre la altitud de transición; si no, "5.990 ft".</summary>
    private string AltBoth(double ft) =>
        ft > TransitionAltitudeFt ? Tr.F("{0} · {1:N0} ft", FlightLevelText(ft), ft) : $"{ft:N0} ft";

    private static string LegText(PathSection s) => s switch
    {
        PathSection.Sid => "SID",
        PathSection.Enroute => Tr.T("Ruta"),
        PathSection.Star => "STAR",
        PathSection.Approach => Tr.T("Aproximación"),
        _ => Tr.T("Final"),
    };

    /// <summary>
    /// Si está bajando y a qué ritmo; si no, dónde empieza a bajar (el punto donde su perfil cruza su altitud); y la
    /// velocidad vertical que necesita para cumplir la próxima restricción de la carta.
    /// </summary>
    private string DescentText(AirborneAgent f)
    {
        var path = f.Path;
        var here = f.RemainingM;
        var gs = Math.Max(1, f.SpeedKt);
        string now;
        if (f.VerticalSpeedFpm < -300)
            now = Tr.F("bajando a {0:0} ft/min", -f.VerticalSpeedFpm);
        else if (f.VerticalSpeedFpm > 300)
            now = Tr.F("subiendo a {0:0} ft/min", f.VerticalSpeedFpm);
        else
        {
            now = Tr.T("nivelado");
            for (var i = f.TargetIndex; i < path.Count && path.HasProfile; i++)
            {
                if (path[i].ProfileFt > 0 && path[i].ProfileFt < f.AltitudeFt - 200)
                {
                    now += Tr.F(" · empieza a bajar en {0:0} NM", Math.Max(0, (here - path[i].RemainingM) / Nm));
                    break;
                }
            }
        }
        for (var i = f.TargetIndex; i < path.Count; i++)
        {
            var p = path[i];
            var limit = p.MaxAltFt > 0 && f.AltitudeFt > p.MaxAltFt ? p.MaxAltFt : 0;
            if (limit == 0)
                continue;
            var minutes = Math.Max(0.1, (here - p.RemainingM) / Nm / gs * 60);
            return Tr.F("{0} · para {1} ({2}) necesita {3:0} ft/min", now, p.Name, AltBoth(limit), (f.AltitudeFt - limit) / minutes);
        }
        return now;
    }

    private string FixLabel(PathPoint p)
    {
        var c = ConstraintText(p);
        return c.Length > 0 ? $"{p.Name} {c}" : p.Name;
    }

    /// <summary>"≥FL120 ≤250 kt", "8000 ft", "6000–9000 ft"…</summary>
    private string ConstraintText(PathPoint p)
    {
        var alt = (p.MinAltFt, p.MaxAltFt) switch
        {
            ( > 0, > 0) when Math.Abs(p.MinAltFt - p.MaxAltFt) < 1 => FlightLevelText(p.MinAltFt),
            ( > 0, > 0) => $"{FlightLevelText(p.MinAltFt)}–{FlightLevelText(p.MaxAltFt)}",
            ( > 0, _) => "≥" + FlightLevelText(p.MinAltFt),
            (_, > 0) => "≤" + FlightLevelText(p.MaxAltFt),
            _ => "",
        };
        var speed = p.SpeedMaxKt > 0 ? Tr.F("≤{0:0} kt", p.SpeedMaxKt) : "";
        return string.Join(" ", new[] { alt, speed }.Where(x => x.Length > 0));
    }

    private static string Or(string s) => string.IsNullOrEmpty(s) ? "?" : s;

    private static DeparturePhase PhaseOf(FlightPhase p) => p switch
    {
        FlightPhase.Climb => DeparturePhase.Climb,
        FlightPhase.Cruise => DeparturePhase.Cruise,
        FlightPhase.Descent => DeparturePhase.Descent,
        FlightPhase.Approach => DeparturePhase.Approach,
        FlightPhase.Final => DeparturePhase.Final,
        FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed => DeparturePhase.Landing,
        FlightPhase.GoAround => DeparturePhase.GoAround,
        FlightPhase.Holding => DeparturePhase.Holding,
        _ => DeparturePhase.Departed,
    };

    /// <summary>De dónde salió la ruta de un IA (ADR-089), para saber si es real y verificada.</summary>
    private static string RouteSourceText(string source) => source switch
    {
        "VATSIM" => Tr.T("VATSIM: la ruta que vuelan pilotos reales en ese par (verificada: empalma con la SID y la STAR, sin rodeos)"),
        "aerovías del simulador" => Tr.T("aerovías del simulador: la más corta entre su SID y su STAR (verificada: sin rodeos)"),
        "directo" => Tr.T("directo (provisional: faltaban datos; se vuelve a buscar antes de salir)"),
        _ => source,
    };
}

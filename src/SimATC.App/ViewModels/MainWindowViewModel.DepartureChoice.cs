using CommunityToolkit.Mvvm.Input;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// La salida que recomienda LUAW frente a la del plan (ADR-113, pedido de Marco 2026-11-09 en RKSI: SimBrief daba 16L y
/// LUAW la usaba para llegadas). Antes de pedir la autorización, si difieren, el radar dibuja la recomendada (punteada
/// blanca) y la tarjeta del ATC ofrece "Usar la de LUAW". Autorizado, el radar dibuja la pista y la SID que dio el ATC.
/// </summary>
public sealed partial class MainWindowViewModel
{
    // El piloto eligió la salida de LUAW para este plan.
    private bool _useAtcDeparture;

    private string? _recKey;
    private (string Runway, string? Sid)? _rec;

    /// <summary>La pista y la SID que elegiría LUAW ahora (null si no aplica: sin plan, en otro aeropuerto o ya autorizado).</summary>
    private (string Runway, string? Sid)? RecommendedDeparture()
    {
        if (Plan is not { } p || Airport is not { } ap || _graph is not { } g || _originData is null
            || !string.Equals(p.Origin.Icao, ap.Icao, StringComparison.OrdinalIgnoreCase)
            || _session is { Clearance: not null } || DepartureEnds is not { Count: > 0 } atcEnds)
            return null;
        // La del plan si está en uso (ADR-127: aunque los IA la usen para llegar); si no, las de salida.
        IReadOnlyList<string> ends = PlanDepartureRunwayInUse() is { } inUse ? [inUse] : atcEnds;
        // Se recalcula solo si cambian las pistas en uso, el plan o el avión se movió (busca rutas de rodaje).
        var pos = UserState?.Position;
        var key = $"{string.Join(",", ends)}|{p.Origin.PlannedRunway}|{p.SidIdent}|{Math.Round((pos?.LatDeg ?? 0) * 2000)}|{Math.Round((pos?.LonDeg ?? 0) * 2000)}";
        if (key == _recKey)
            return _rec;
        var runway = DepartureChoice.Runway(g, ap, pos, fromStand: true, UserState?.HeadingTrueDeg, ends, p.Origin.PlannedRunway);
        _rec = (runway, DepartureChoice.Sid(ap, p, runway)?.Name);
        _recKey = key;
        return _rec;
    }

    /// <summary>La recomendada difiere del plan y todavía no la eligió: se dibuja y se ofrece.</summary>
    public bool ShowDepartureRecommendation =>
        !_useAtcDeparture && RecommendedDeparture() is { } r && Plan is { } p
        && (!string.Equals(r.Runway, p.Origin.PlannedRunway, StringComparison.OrdinalIgnoreCase)
            || r.Sid is not null && !string.Equals(r.Sid, SelectedSid?.Value?.Procedure?.Name, StringComparison.OrdinalIgnoreCase));

    public string DepartureRecommendationText =>
        RecommendedDeparture() is { } r && Plan is { } p
            ? Tr.F("LUAW recomienda salir por la {0}{1}: es la pista en uso para salidas. ", r.Runway, (r.Sid is { } s ? Tr.F(" con la {0}", s) : ""))
              + Tr.F("Tu plan: {0}{1}. ", p.Origin.PlannedRunway ?? Tr.T("sin pista"), (SelectedSid?.Value?.Procedure?.Name is { } ps ? Tr.F(" con la {0}", ps) : ""))
              + Tr.T("En el radar, la recomendada va punteada en blanco.")
            : "";

    /// <summary>"Usar la de LUAW": el ATC te la da en la autorización y el radar la dibuja como tu SID.</summary>
    [RelayCommand]
    private void UseAtcDeparture()
    {
        if (RecommendedDeparture() is not { } r)
            return;
        _useAtcDeparture = true;
        Log(Tr.F("Salida: usas la de LUAW ({0}{1}) en vez de la del plan.", r.Runway, (r.Sid is { } s ? $", {s}" : "")));
        OnDepartureChoiceChanged();
    }

    private void OnDepartureChoiceChanged()
    {
        _atcProceduresKey = null; // el radar vuelve a dibujar
        BuildProcedureLines();
        OnPropertyChanged(nameof(ShowDepartureRecommendation));
        OnPropertyChanged(nameof(DepartureRecommendationText));
        UpdatePlanViews();
    }

    /// <summary>La pista que el piloto eligió de LUAW, para <see cref="PilotDepartureEnds"/>; null si sigue con la del plan.</summary>
    private string? ChosenAtcRunway => _useAtcDeparture ? RecommendedDeparture()?.Runway : null;
}

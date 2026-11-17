namespace SimATC.Core.Atc;

/// <summary>Autorizaciones que da el control a un avión (ADR-021). El piloto solo hace lo autorizado.</summary>
[Flags]
public enum ClearanceKind
{
    None = 0,
    /// <summary>Ground: "pushback approved".</summary>
    Pushback = 1,
    /// <summary>Tower: "line up and wait".</summary>
    LineUp = 2,
    /// <summary>Tower: "cleared for take-off".</summary>
    Takeoff = 4,
    /// <summary>Ground: "taxi to stand…" (llegadas, ADR-065).</summary>
    Taxi = 8,
    All = Pushback | LineUp | Takeoff | Taxi,
}

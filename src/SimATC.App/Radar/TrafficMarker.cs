using SimATC.Core.Geo;

namespace SimATC.App.Radar;

/// <summary>
/// Un avión de tráfico para dibujar en el radar. <paramref name="Id"/> es su ObjectId (para seleccionarlo);
/// <paramref name="Holding"/>: detenido esperando algo (se dibuja en ámbar). <paramref name="WingspanM"/>: envergadura de su
/// modelo (m, 0 si no se conoce), para dibujarlo a escala con zoom cercano (ADR-173).
/// </summary>
public sealed record TrafficMarker(GeoPoint Position, double HeadingTrueDeg, string Label, uint Id = 0, bool Holding = false,
    double SpeedKt = 0, double WingspanM = 0, SimATC.Core.Fleet.AircraftShape Shape = SimATC.Core.Fleet.AircraftShape.Narrow);

/// <summary>Un punto de la ruta del plan de vuelo para el radar (posición y nombre).</summary>
public sealed record PlanPoint(GeoPoint Position, string Name);

/// <summary>Qué procedimiento es una línea del radar (define su color).</summary>
public enum ProcedureKind
{
    Sid,
    Star,
    Approach,
    /// <summary>La SID que recomienda LUAW cuando difiere de la del plan (ADR-113): punteada en blanco.</summary>
    SidRecommended,
}

/// <summary>Trazado de un procedimiento del simulador para el radar (referencia para el piloto).</summary>
public sealed record ProcedureLine(ProcedureKind Kind, string Name, SimATC.Core.Plan.ProcedureShape Shape);

/// <summary>
/// La ruta del avión IA elegido (prueba de Marco 2026-11-01): lo recorrido, lo que le falta, sus fijos con restricción y
/// altitud prevista, y el puesto al que va (resaltado).
/// </summary>
public sealed record SelectedFlightView(IReadOnlyList<GeoPoint> Trail, IReadOnlyList<GeoPoint> Ahead,
    IReadOnlyList<PlanPoint> Fixes, GeoPoint? Stand, double StandRadiusM);

namespace SimATC.Core.Geo;

/// <summary>Coordenada geográfica en grados decimales (norte y este positivos).</summary>
public readonly record struct GeoPoint(double LatDeg, double LonDeg);

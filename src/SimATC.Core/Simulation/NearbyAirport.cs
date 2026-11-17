using SimATC.Core.Geo;

namespace SimATC.Core.Simulation;

/// <summary>Un aeropuerto del caché del simulador cerca del piloto: ICAO, punto de referencia y altitud (m).</summary>
public sealed record NearbyAirport(string Icao, GeoPoint Position, double AltitudeM);

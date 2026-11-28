using SimATC.Core.Text;
using System.Globalization;

namespace SimATC.Core.Simulation;

/// <summary>Lo que se aplica al avión del piloto desde el OFP (ADR-079): combustible y carga en libras, y datos de ATC.</summary>
public sealed record AircraftLoadRequest(double FuelLbs, double PayloadLbs, string? AtcId, string? Airline, string? FlightNumber);

/// <summary>Cómo se reparte en el avión: galones por tanque y libras por estación (null: esa estación no se toca).</summary>
public sealed record AircraftLoadPlan(double LeftGal, double RightGal, double CenterGal, IReadOnlyList<double?> StationLbs, string Summary);

/// <summary>
/// Reparto del OFP en el avión (ADR-079), función pura: el combustible primero en las alas (iguales, hasta su capacidad) y
/// el resto al central, como en un Airbus/Boeing; la carga en partes iguales entre las estaciones que no son de la
/// tripulación (nombre con "pilot", "crew", "cockpit", "captain" u "officer": se dejan como están).
/// </summary>
public static class AircraftLoadPlanner
{
    private static readonly string[] CrewWords = ["pilot", "crew", "cockpit", "captain", "officer"];

    public static AircraftLoadPlan Plan(AircraftLoadRequest r, int stationCount, IReadOnlyList<string> names, double leftCapGal,
        double rightCapGal, double centerCapGal, double lbsPerGal)
    {
        var gal = lbsPerGal > 0 ? r.FuelLbs / lbsPerGal : 0;
        var wing = Math.Min(gal / 2, Math.Min(leftCapGal, rightCapGal));
        var center = Math.Min(Math.Max(0, gal - 2 * wing), Math.Max(0, centerCapGal));
        var count = Math.Clamp(stationCount, 0, 15);
        var cargo = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var n = i < names.Count ? names[i] ?? "" : "";
            if (!CrewWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
                cargo.Add(i);
        }
        var stations = new double?[count];
        foreach (var i in cargo)
            stations[i] = cargo.Count > 0 ? r.PayloadLbs / cargo.Count : 0;
        var inv = CultureInfo.InvariantCulture;
        var loadedLbs = (2 * wing + center) * lbsPerGal;
        var summary = string.Format(inv, Tr.T("combustible {0:N0} kg (alas {1:N0} + {1:N0} gal, central {2:N0} gal), carga {3:N0} kg en {4} de {5} estaciones"),
            loadedLbs * 0.45359237, wing, center, r.PayloadLbs * 0.45359237, cargo.Count, count);
        if (loadedLbs + 1 < r.FuelLbs)
            summary += string.Format(inv, Tr.T(" · no cabe todo el combustible: faltan {0:N0} kg"), (r.FuelLbs - loadedLbs) * 0.45359237);
        return new AircraftLoadPlan(wing, wing, center, stations, summary);
    }
}

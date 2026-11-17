using SimATC.Core.Geo;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.Core.Plan;

public enum CheckStatus
{
    Ok,
    Info,
    Warn,
}

/// <summary>Una fila de la comparación "tu avión vs OFP".</summary>
public sealed record CheckRow(string Item, string Planned, string Actual, CheckStatus Status, string Note);

/// <summary>
/// Compara el avión del piloto (combustible, pesos, tipo, matrícula y posición) con el OFP de SimBrief antes de salir.
/// Solo informa: nada de esto cambia lo que hace el ATC. Los pesos se comparan en las unidades del OFP.
/// </summary>
public static class BriefingCheck
{
    public const double KgPerLb = 0.45359237;

    /// <summary>Diferencia aceptada: el 3 % del valor planificado, y al menos 100 kg (220 lb).</summary>
    public const double TolerancePct = 0.03;
    public const double MinToleranceKg = 100;

    /// <summary>A más de esto del aeropuerto de origen (m) se avisa.</summary>
    public const double AtOriginM = 5000;

    public static IReadOnlyList<CheckRow> Compare(FlightPlan plan, UserAircraftLoad load, GeoPoint? userPosition, bool onGround = true)
    {
        var rows = new List<CheckRow>();
        if (plan.Briefing is not { } b)
            return rows;
        var kg = b.Units != "lbs";
        var unit = kg ? "kg" : "lb";
        double Conv(double lbs) => kg ? lbs * KgPerLb : lbs;
        var minTol = kg ? MinToleranceKg : MinToleranceKg / KgPerLb;
        string W(double v) => $"{v:N0} {unit}";

        var fuel = Conv(load.FuelLbs);
        var total = Conv(load.TotalWeightLbs);
        var zfw = total - fuel;
        var taxi = b.FuelTaxi ?? 0;

        // Combustible: el de rampa (antes de rodar).
        if (b.FuelRamp is { } ramp)
        {
            var min = (b.FuelMinTakeoff ?? 0) + taxi;
            CheckStatus status;
            string note;
            if (min > 0 && fuel < min)
                (status, note) = (CheckStatus.Warn, Tr.F("menos que el mínimo para despegar más el rodaje ({0})", W(min)));
            else
                (status, note) = Near(fuel, ramp, minTol, W, Tr.T("más combustible que lo planificado (más peso)"), Tr.T("menos combustible que lo planificado"));
            rows.Add(new CheckRow(Tr.T("Combustible a bordo"), W(ramp), W(fuel), status, note));
        }

        // Peso total frente al de rampa estimado (TOW + rodaje).
        if (b.Tow is { } tow)
        {
            var rampWeight = tow + taxi;
            var (status, note) = b.MaxTow is { } mtow && total - taxi > mtow
                ? (CheckStatus.Warn, Tr.F("sobre el peso máximo de despegue del OFP ({0})", W(mtow)))
                : Near(total, rampWeight, minTol, W, Tr.T("más pesado que lo planificado"), Tr.T("más liviano que lo planificado"));
            rows.Add(new CheckRow(Tr.T("Peso total"), W(rampWeight), W(total), status, note));
        }

        // Peso sin combustible: pasajeros y carga.
        if (b.Zfw is { } planZfw)
        {
            var (status, note) = b.MaxZfw is { } mzfw && zfw > mzfw
                ? (CheckStatus.Warn, Tr.F("sobre el ZFW máximo del OFP ({0})", W(mzfw)))
                : Near(zfw, planZfw, minTol, W, Tr.T("más carga que lo planificado"), Tr.T("menos carga que lo planificado"));
            rows.Add(new CheckRow(Tr.T("Peso sin combustible (ZFW)"), W(planZfw), W(zfw), status, note));
        }

        // Tipo: el simulador lo da como texto libre (o una clave de traducción); si no aparece, solo se informa.
        var typeFound = Contains(load.AtcModel, plan.AircraftIcao) || Contains(load.AtcType, plan.AircraftIcao)
                        || Contains(load.Title, plan.AircraftIcao);
        rows.Add(new CheckRow(Tr.T("Tipo de avión"), plan.AircraftIcao, load.Title.Length > 0 ? load.Title : load.AtcModel,
            typeFound ? CheckStatus.Ok : CheckStatus.Info,
            typeFound ? "" : Tr.F("no pude confirmarlo con el simulador (ATC MODEL \"{0}\"); revísalo", load.AtcModel)));

        if (plan.Registration is { } reg)
        {
            var same = Normalize(reg) == Normalize(load.AtcId);
            rows.Add(new CheckRow(Tr.T("Matrícula"), reg, load.AtcId, same ? CheckStatus.Ok : CheckStatus.Info,
                same ? "" : Tr.T("distinta: el ATC usará tu indicativo del plan")));
        }

        if (plan.Origin.Position is { } origin && userPosition is { } here)
        {
            var d = GeoMath.DistanceM(origin, here);
            if (!onGround)
                // En vuelo ya no tiene sentido avisar que no está en el aeropuerto de salida.
                rows.Add(new CheckRow(Tr.T("Posición"), plan.Origin.Icao, Tr.F("en vuelo, a {0:0} km", d / 1000), CheckStatus.Info, ""));
            else
                rows.Add(new CheckRow(Tr.T("Posición"), plan.Origin.Icao, d < AtOriginM ? $"en {plan.Origin.Icao}" : Tr.F("a {0:0} km", d / 1000),
                    d < AtOriginM ? CheckStatus.Ok : CheckStatus.Warn, d < AtOriginM ? "" : Tr.T("no estás en el aeropuerto de salida del plan")));
        }
        return rows;
    }

    private static (CheckStatus, string) Near(double actual, double planned, double minTol, Func<double, string> w, string more, string less)
    {
        var diff = actual - planned;
        var tol = Math.Max(Math.Abs(planned) * TolerancePct, minTol);
        if (Math.Abs(diff) <= tol)
            return (CheckStatus.Ok, "");
        return diff > 0 ? (CheckStatus.Info, $"{w(diff)} {more}") : (CheckStatus.Warn, $"{w(-diff)} {less}");
    }

    private static bool Contains(string text, string type) =>
        type.Length > 0 && text.Contains(type, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string s) => s.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
}

using System.Globalization;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Atc;

/// <summary>
/// Compara el METAR del OFP de SimBrief con el actual (ADR-101, bloque D: "mostrar el METAR, e incluso compararlo con el que
/// dio SimBrief"): lo que cambió y que le importa al piloto, en frases cortas en español. Función pura.
/// </summary>
public static class MetarComparison
{
    /// <summary>Diferencias que se avisan (APROXIMADO, criterio de briefing): viento ≥ 30° o ≥ 8 kt, QNH ≥ 3 hPa.</summary>
    public const double WindDirDeg = 30, WindSpeedKt = 8, QnhHpa = 3;

    public static IReadOnlyList<string> Differences(Metar ofp, Metar now)
    {
        var list = new List<string>();
        var ofpCalm = ofp.WindKt < 3 || ofp.WindVariable;
        var nowCalm = now.WindKt < 3 || now.WindVariable;
        if (!ofpCalm && !nowCalm && ofp.WindFromTrueDeg is { } a && now.WindFromTrueDeg is { } b
            && Math.Abs(GeoMath.AngleDiffDeg(a, b)) >= WindDirDeg)
            list.Add(Tr.F("el viento rotó de {0:000}° a {1:000}°", a, b));
        if (Math.Abs(now.WindKt - ofp.WindKt) >= WindSpeedKt || ofpCalm != nowCalm && Math.Abs(now.WindKt - ofp.WindKt) >= 3)
            list.Add(Tr.F("viento de {0} a {1}", Wind(ofp), Wind(now)));
        if (now.GustKt is { } g && ofp.GustKt is null)
            list.Add(Tr.F("ahora con ráfagas de {0:0} kt", g));
        if (ofp.QnhHpa is { } q0 && now.QnhHpa is { } q1 && Math.Abs(q1 - q0) >= QnhHpa)
            list.Add(Tr.F("QNH de {0:0} a {1:0} hPa", q0, q1));
        if (ofp.VisibilityM is { } v0 && now.VisibilityM is { } v1 && (v0 >= 5000) != (v1 >= 5000))
            list.Add(Tr.F("visibilidad de {0} a {1}", Vis(v0), Vis(v1)));
        if (ofp.Precipitation != now.Precipitation)
            list.Add(now.Precipitation switch
            {
                Precipitation.Rain => Tr.T("ahora llueve"),
                Precipitation.Snow => Tr.T("ahora nieva"),
                _ => Tr.T("ya no hay precipitación"),
            });
        return list;
    }

    private static string Wind(Metar m) =>
        m.WindKt < 1 ? "calma" : m.WindVariable ? Tr.F("variable {0:0} kt", m.WindKt) : Tr.F("{0:000}° {1:0} kt", m.WindFromTrueDeg, m.WindKt);

    private static string Vis(double m) =>
        m >= 10_000 ? Tr.T("10 km o más") : m >= 1000 ? $"{(m / 1000).ToString("0.#", CultureInfo.GetCultureInfo("es-CL"))} km" : $"{m:0} m";
}

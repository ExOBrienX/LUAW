using System.Globalization;
using System.Text.RegularExpressions;
using SimATC.Core.Geo;

namespace SimATC.Core.Atc;

/// <summary>
/// Un METAR leído (ADR-100): lo que el ATC necesita para elegir la pista y armar el ATIS de un aeropuerto lejano. El
/// simulador no da el clima de otro lugar (SimConnect: todas las funciones de clima obsoletas en MSFS 2024, ADR-097), y con
/// el clima en vivo del simulador el METAR real es el mismo. Viento en grados verdaderos (así se publica el METAR), nudos.
/// </summary>
public sealed record Metar(
    string Icao,
    DateTime TimeUtc,
    double? WindFromTrueDeg,
    double WindKt,
    double? GustKt,
    double? QnhHpa,
    double? VisibilityM,
    double? TemperatureC,
    Precipitation Precipitation,
    string Raw)
{
    /// <summary>"VRB" o calma: sin dirección.</summary>
    public bool WindVariable => WindFromTrueDeg is null;
}

/// <summary>Lee el texto de un METAR (formato OACI Anexo 3, con las variantes de EE. UU.: A2992, SM). Función pura.</summary>
public static partial class MetarParser
{
    /// <summary>
    /// El METAR de <paramref name="raw"/>, o null si no trae al menos el aeropuerto y el viento. <paramref name="nowUtc"/>
    /// da el mes y el año (el METAR solo trae día y hora).
    /// </summary>
    public static Metar? Parse(string raw, DateTime nowUtc)
    {
        var text = raw.Trim().TrimEnd('=', '$').Trim();
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 0 && tokens[0] is "METAR" or "SPECI")
            tokens.RemoveAt(0);
        if (tokens.Count > 0 && tokens[0] == "COR")
            tokens.RemoveAt(0);
        if (tokens.Count < 2 || !IcaoRe().IsMatch(tokens[0]))
            return null;
        var icao = tokens[0];
        var time = nowUtc;
        double? dir = null, gust = null, qnh = null, vis = null, temp = null;
        double? speed = null;
        var precipitation = Precipitation.None;
        foreach (var t in tokens.Skip(1))
        {
            if (t == "RMK" || t == "TEMPO" || t == "BECMG" || t == "NOSIG")
                break; // lo que sigue es pronóstico u observaciones
            if (TimeRe().Match(t) is { Success: true } tm)
            {
                time = TimeFrom(nowUtc, Int(tm.Groups[1].Value), Int(tm.Groups[2].Value), Int(tm.Groups[3].Value));
            }
            else if (speed is null && WindRe().Match(t) is { Success: true } wm)
            {
                var unit = wm.Groups[4].Value;
                var k = unit == "MPS" ? 1.943844 : unit == "KMH" ? 0.539957 : 1;
                dir = wm.Groups[1].Value == "VRB" ? null : Int(wm.Groups[1].Value);
                speed = Int(wm.Groups[2].Value) * k;
                gust = wm.Groups[3].Success ? Int(wm.Groups[3].Value) * k : null;
                if (speed < 0.5)
                    dir = null; // calma
            }
            else if (t == "CAVOK")
            {
                vis = 10_000;
            }
            else if (vis is null && VisMetersRe().Match(t) is { Success: true } vm)
            {
                vis = Int(vm.Groups[1].Value) == 9999 ? 10_000 : Int(vm.Groups[1].Value);
            }
            else if (vis is null && VisMilesRe().Match(t) is { Success: true } sm)
            {
                vis = Miles(sm.Groups[1].Value) * 1609.344;
            }
            else if (TempRe().Match(t) is { Success: true } te)
            {
                temp = Signed(te.Groups[1].Value);
            }
            else if (QRe().Match(t) is { Success: true } q)
            {
                qnh = Int(q.Groups[1].Value);
            }
            else if (ARe().Match(t) is { Success: true } a)
            {
                qnh = Math.Round(Int(a.Groups[1].Value) / 100.0 * 33.8638866667, 1); // pulgadas → hPa
            }
            else if (precipitation == Precipitation.None && WeatherRe().Match(t) is { Success: true } w)
            {
                precipitation = w.Groups[1].Value.Contains("SN", StringComparison.Ordinal) ? Precipitation.Snow : Precipitation.Rain;
            }
        }
        if (speed is null)
            return null;
        return new Metar(icao, time, dir, speed.Value, gust, qnh, vis, temp, precipitation, text);
    }

    /// <summary>La hora del METAR (día y hora UTC) en el mes de <paramref name="now"/>, o del anterior si el día es posterior.</summary>
    private static DateTime TimeFrom(DateTime now, int day, int hour, int minute)
    {
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (day > now.Day + 1)
            month = month.AddMonths(-1);
        day = Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month));
        return month.AddDays(day - 1).AddHours(hour).AddMinutes(minute);
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static double Signed(string s) => s.StartsWith('M') ? -Int(s[1..]) : Int(s);

    private static double Miles(string s)
    {
        // "10", "1/2", "1 1/2" no aparece junto (se separa en dos tokens); aquí "P6" o fracciones simples.
        s = s.TrimStart('P', 'M');
        var slash = s.IndexOf('/');
        return slash > 0 ? Int(s[..slash]) / (double)Int(s[(slash + 1)..]) : Int(s);
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{3}$")]
    private static partial Regex IcaoRe();

    [GeneratedRegex(@"^(\d{2})(\d{2})(\d{2})Z$")]
    private static partial Regex TimeRe();

    [GeneratedRegex(@"^(\d{3}|VRB)(\d{2,3})(?:G(\d{2,3}))?(KT|MPS|KMH)$")]
    private static partial Regex WindRe();

    [GeneratedRegex(@"^(\d{4})(?:NDV)?$")]
    private static partial Regex VisMetersRe();

    [GeneratedRegex(@"^([PM]?\d{1,2}(?:/\d{1,2})?)SM$")]
    private static partial Regex VisMilesRe();

    [GeneratedRegex(@"^(M?\d{2})/(?:M?\d{2})?$")]
    private static partial Regex TempRe();

    [GeneratedRegex(@"^Q(\d{3,4})$")]
    private static partial Regex QRe();

    [GeneratedRegex(@"^A(\d{4})$")]
    private static partial Regex ARe();

    [GeneratedRegex(@"^[+-]?(?:VC)?(?:SH|TS|FZ)?((?:RA|DZ|SN|SG|PL|GR|GS)+)$")]
    private static partial Regex WeatherRe();
}

public sealed partial record WeatherObservation
{
    /// <summary>
    /// La observación de un aeropuerto desde su METAR (ADR-100). La variación magnética no viene en el METAR: se usa la del
    /// simulador donde está el piloto (<paramref name="magVarDeg"/>), APROXIMADO para aeropuertos lejanos (solo cambia
    /// la dirección del viento que se dice en grados magnéticos, no la pista elegida). Viento variable o calma: dirección 0.
    /// </summary>
    public static WeatherObservation FromMetar(Metar m, double magVarDeg) =>
        new(m.Icao, m.TimeUtc, m.WindFromTrueDeg ?? 0, m.WindVariable ? 0 : m.WindKt, m.GustKt, m.VisibilityM ?? 10_000,
            m.TemperatureC ?? 15, m.QnhHpa ?? 1013.25, m.Precipitation, magVarDeg);
}

using System.Globalization;

using SimATC.Core.Text;

namespace SimATC.Core.Plan;

/// <summary>
/// Plan ingresado a mano (RF-29, alternativa a SimBrief). La ruta queda como texto: sin coordenadas no se dibuja en el
/// radar (buscar cada punto en las facilities del simulador queda pendiente, §9 VERIFICAR alcance de ROUTE).
/// </summary>
public static class ManualPlan
{
    /// <summary>Crea el plan o devuelve los errores (en español) de los campos que no sirven.</summary>
    public static (FlightPlan? Plan, IReadOnlyList<string> Errors) Create(string callsign, string aircraftIcao, string origin,
        string destination, string cruise, string route)
    {
        var errors = new List<string>();
        var cs = callsign.Trim().ToUpperInvariant();
        var type = aircraftIcao.Trim().ToUpperInvariant();
        var from = origin.Trim().ToUpperInvariant();
        var to = destination.Trim().ToUpperInvariant();

        if (cs.Length is < 2 or > 7 || !cs.All(char.IsAsciiLetterOrDigit))
            errors.Add(Tr.T("Indicativo: de 2 a 7 letras o números (p. ej. ABC123)."));
        if (type.Length is < 2 or > 4 || !type.All(char.IsAsciiLetterOrDigit))
            errors.Add(Tr.T("Tipo: código OACI de 2 a 4 caracteres (p. ej. A20N)."));
        if (!IsIcao(from))
            errors.Add(Tr.T("Origen: código OACI de 4 letras."));
        if (!IsIcao(to))
            errors.Add(Tr.T("Destino: código OACI de 4 letras."));
        var cruiseFt = ParseCruise(cruise);
        if (cruiseFt is null)
            errors.Add(Tr.T("Crucero: nivel (FL240 o 240) o altitud en pies (24000), entre 1000 y 60000 ft."));

        if (errors.Count > 0)
            return (null, errors);
        return (new FlightPlan(PlanSource.Manual, cs, type, new PlanAirport(from), new PlanAirport(to), cruiseFt!.Value,
            string.Join(' ', route.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant(), []), []);
    }

    private static bool IsIcao(string s) => s.Length == 4 && s.All(char.IsAsciiLetterUpper);

    /// <summary>"FL240" o "240" → 24000 ft; "24000" → 24000 ft. Fuera de 1000–60000 ft no vale.</summary>
    public static int? ParseCruise(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        var isLevel = t.StartsWith("FL", StringComparison.Ordinal);
        if (isLevel)
            t = t[2..];
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return null;
        var ft = isLevel || n < 1000 ? n * 100 : n;
        return ft is >= 1000 and <= 60000 ? ft : null;
    }
}

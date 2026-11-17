using System.Net;

using SimATC.Core.Text;

namespace SimATC.Core.Plan;

/// <summary>
/// Descarga el último plan del usuario desde la API pública de SimBrief (V-10, sin clave):
/// <c>https://www.simbrief.com/api/xml.fetcher.php?userid=…&amp;json=1</c> (o <c>username=…</c>). Con un usuario que no
/// existe responde HTTP 400 y <c>fetch.status</c> = "Error: …".
/// </summary>
public sealed class SimBriefClient(HttpClient http)
{
    public const string BaseUrl = "https://www.simbrief.com/api/xml.fetcher.php";

    /// <summary>URL del plan: si son solo dígitos es el Pilot ID; si no, el nombre de usuario.</summary>
    public static Uri UrlFor(string user)
    {
        var u = user.Trim();
        var key = u.Length > 0 && u.All(char.IsAsciiDigit) ? "userid" : "username";
        return new Uri($"{BaseUrl}?{key}={Uri.EscapeDataString(u)}&json=1");
    }

    public async Task<FlightPlan> FetchLatestAsync(string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user))
            throw new SimBriefException(Tr.T("Escribe tu usuario o Pilot ID de SimBrief."));
        string body;
        HttpStatusCode status;
        try
        {
            using var response = await http.GetAsync(UrlFor(user), ct).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SimBriefException(Tr.F("No se pudo conectar con SimBrief ({0}).", ex.Message), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SimBriefException(Tr.T("SimBrief no respondió a tiempo."), ex);
        }

        // Con error (400) el cuerpo trae fetch.status: el parser lo convierte en el mensaje.
        if (status is HttpStatusCode.OK or HttpStatusCode.BadRequest)
        {
            var plan = SimBriefParser.Parse(body);
            if (status == HttpStatusCode.OK)
                return plan;
        }
        throw new SimBriefException(Tr.F("SimBrief respondió {0}.", (int)status));
    }
}

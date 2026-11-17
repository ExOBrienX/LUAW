using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Plan;

/// <summary>Error al obtener o leer el plan de SimBrief, con un mensaje para mostrar al usuario.</summary>
public sealed class SimBriefException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Lee el OFP de SimBrief en JSON (<c>xml.fetcher.php?…&amp;json=1</c>, V-10). El JSON es una conversión del XML: un
/// campo vacío llega como objeto vacío (<c>{}</c>) en vez de texto, y una lista de un solo elemento puede llegar como
/// objeto en vez de arreglo. Por eso se lee todo con tolerancia.
/// </summary>
public static class SimBriefParser
{
    public static FlightPlan Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SimBriefException(Tr.T("SimBrief respondió algo que no es un plan (JSON inválido)."), ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (Obj(root, "fetch") is { } fetch && Str(fetch, "status") is { } status && status != "Success")
                throw new SimBriefException(Tr.F("SimBrief: {0}.", status));

            var general = Obj(root, "general") ?? throw new SimBriefException(Tr.T("El plan de SimBrief no trae la sección \"general\"."));
            var atc = Obj(root, "atc");
            var aircraft = Obj(root, "aircraft");
            var origin = Airport(Obj(root, "origin")) ?? throw new SimBriefException(Tr.T("El plan de SimBrief no trae origen."));
            var destination = Airport(Obj(root, "destination")) ?? throw new SimBriefException(Tr.T("El plan de SimBrief no trae destino."));

            var airline = Str(general, "icao_airline");
            var flightNumber = Str(general, "flight_number");
            var callsign = (atc is { } a ? Str(a, "callsign") : null) ?? $"{airline}{flightNumber}";
            if (callsign.Length == 0)
                throw new SimBriefException(Tr.T("El plan de SimBrief no trae indicativo."));

            var fixes = new List<PlanFix>();
            if (Obj(root, "navlog") is { } navlog && navlog.TryGetProperty("fix", out var fixList))
            {
                foreach (var f in Items(fixList))
                {
                    if (Str(f, "ident") is not { } ident || Num(f, "pos_lat") is not { } lat || Num(f, "pos_long") is not { } lon)
                        continue;
                    fixes.Add(new PlanFix(ident, Str(f, "type") ?? "", Str(f, "via_airway"), Str(f, "is_sid_star") == "1",
                        new GeoPoint(lat, lon), (int)(Num(f, "altitude_feet") ?? 0)));
                }
            }

            var parameters = Obj(root, "params");
            var times = Obj(root, "times");
            return new FlightPlan(PlanSource.SimBrief, callsign,
                (aircraft is { } ac ? Str(ac, "icaocode") ?? Str(ac, "icao_code") : null) ?? "",
                origin, destination, (int)(Num(general, "initial_altitude") ?? 0), Str(general, "route") ?? "", fixes)
            {
                AirlineIcao = airline,
                FlightNumber = flightNumber,
                Registration = aircraft is { } ac2 ? Str(ac2, "reg") : null,
                Alternate = Airport(Obj(root, "alternate")),
                SidIdent = Str(general, "sid_ident"),
                SidTransition = Str(general, "sid_trans"),
                StarIdent = Str(general, "star_ident"),
                StarTransition = Str(general, "star_trans"),
                Airac = parameters is { } p ? Str(p, "airac") : null,
                GeneratedUtc = parameters is { } p2 ? UnixTime(p2, "time_generated") : null,
                ScheduledOffBlockUtc = times is { } t ? UnixTime(t, "sched_out") : null,
                Briefing = ParseBriefing(root, general, parameters, times, origin, destination),
            };
        }
    }

    private static Briefing ParseBriefing(JsonElement root, JsonElement general, JsonElement? parameters, JsonElement? times,
        PlanAirport origin, PlanAirport destination)
    {
        var fuel = Obj(root, "fuel");
        var weights = Obj(root, "weights");
        int? F(string name) => fuel is { } f ? Int(f, name) : null;
        int? W(string name) => weights is { } w ? Int(w, name) : null;
        int? T(string name) => times is { } t ? Int(t, name) : null;

        var weather = new List<AirportWeather>();
        foreach (var key in new[] { "origin", "destination", "alternate" })
        {
            if (Obj(root, key) is { } a && Str(a, "icao_code") is { } icao)
                weather.Add(new AirportWeather(icao, Str(a, "metar"), Str(a, "taf")));
        }

        TakeoffPerformance? takeoff = null;
        if (Obj(root, "tlr") is { } tlr && Obj(tlr, "takeoff") is { } to && to.TryGetProperty("runway", out var runways))
        {
            foreach (var r in Items(runways))
            {
                if (Str(r, "identifier") is { } id && id == origin.PlannedRunway)
                {
                    takeoff = new TakeoffPerformance(id, Str(r, "flap_setting"), Int(r, "speeds_v1"), Int(r, "speeds_vr"),
                        Int(r, "speeds_v2"), Str(r, "thrust_setting"), Int(r, "flex_temperature"));
                }
            }
        }

        string? pdf = null;
        if (Obj(root, "files") is { } files && Str(files, "directory") is { } dir && Obj(files, "pdf") is { } pdfObj
            && Str(pdfObj, "link") is { } link)
            pdf = dir + link;

        var ofp = "";
        if (Obj(root, "text") is { } text && Str(text, "plan_html") is { } html)
            ofp = HtmlToText(html);

        var windComp = Str(general, "avg_wind_comp");
        return new Briefing((parameters is { } p ? Str(p, "units") : null) ?? "kgs")
        {
            FuelRamp = F("plan_ramp"),
            FuelTakeoff = F("plan_takeoff"),
            FuelLanding = F("plan_landing"),
            FuelTrip = F("enroute_burn"),
            FuelContingency = F("contingency"),
            FuelAlternate = F("alternate_burn"),
            FuelReserve = F("reserve"),
            FuelTaxi = F("taxi"),
            FuelExtra = F("extra"),
            FuelMinTakeoff = F("min_takeoff"),
            Zfw = W("est_zfw"),
            MaxZfw = W("max_zfw"),
            Tow = W("est_tow"),
            MaxTow = W("max_tow"),
            Ldw = W("est_ldw"),
            MaxLdw = W("max_ldw"),
            Payload = W("payload"),
            Passengers = W("pax_count"),
            Cargo = W("cargo"),
            EnrouteS = T("est_time_enroute"),
            BlockS = T("est_block"),
            TaxiOutS = T("taxi_out"),
            CostIndex = Str(general, "costindex"),
            StepClimbs = Str(general, "stepclimb_string"),
            AvgWind = windComp is null ? null : Tr.F("{0}/{1} kt (componente {2})", Str(general, "avg_wind_dir") ?? "—", Str(general, "avg_wind_spd") ?? "—", windComp),
            RouteDistanceNm = Int(general, "route_distance"),
            TransitionAltitudeFt = Obj(root, "origin") is { } o ? Int(o, "trans_alt") : null,
            TransitionLevelFt = Obj(root, "destination") is { } d ? Int(d, "trans_level") : null,
            Takeoff = takeoff,
            Weather = weather,
            OfpText = ofp,
            PdfUrl = pdf,
        };
    }

    /// <summary>El OFP de SimBrief es HTML con un &lt;pre&gt;: se quitan las etiquetas y se decodifican las entidades.</summary>
    internal static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Replace("\t", "    ").Trim('\n', '\r', ' ');
    }

    private static int? Int(JsonElement parent, string name) => Num(parent, name) is { } v ? (int)Math.Round(v) : null;

    private static PlanAirport? Airport(JsonElement? element)
    {
        if (element is not { } e || Str(e, "icao_code") is not { } icao)
            return null;
        GeoPoint? position = Num(e, "pos_lat") is { } lat && Num(e, "pos_long") is { } lon ? new GeoPoint(lat, lon) : null;
        return new PlanAirport(icao, Str(e, "name") ?? "", position, Str(e, "plan_rwy"));
    }

    /// <summary>Sección como objeto; si llega como arreglo (varios alternos) se toma el primero.</summary>
    private static JsonElement? Obj(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var e))
            return null;
        if (e.ValueKind == JsonValueKind.Array)
            return e.GetArrayLength() > 0 && e[0].ValueKind == JsonValueKind.Object ? e[0] : null;
        return e.ValueKind == JsonValueKind.Object ? e : null;
    }

    /// <summary>Elementos de una lista que puede venir como arreglo o, si tiene uno solo, como objeto.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;
            }
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            yield return e;
        }
    }

    /// <summary>Texto del campo; null si falta, está vacío o llega como <c>{}</c>.</summary>
    private static string? Str(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var e))
            return null;
        var s = e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static double? Num(JsonElement parent, string name) =>
        Str(parent, name) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTime? UnixTime(JsonElement parent, string name) =>
        Num(parent, name) is { } s ? DateTimeOffset.FromUnixTimeSeconds((long)s).UtcDateTime : null;
}

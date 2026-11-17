using SimATC.Core.Atc;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>Una interacción del manual: qué dices tú, qué responde el ATC y cómo se pronuncian, con una nota en español.</summary>
public sealed record ManualEntry(string Section, string Title, string? Pilot, string? PilotSpoken, string? Atc, string? AtcSpoken, string Note)
{
    public bool HasPilot => Pilot is not null;
    public bool HasAtc => Atc is not null;

    /// <summary>¿Coincide con lo que se busca (título, frases o nota, sin distinguir mayúsculas)?</summary>
    public bool Matches(string query) =>
        query.Length == 0 || Contains(Title, query) || Contains(Pilot, query) || Contains(Atc, query) || Contains(Note, query)
        || Contains(Section, query);

    private static bool Contains(string? text, string query) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// El vuelo con que se arman los ejemplos del manual: el plan cargado del piloto o, sin plan, uno genérico. Los nombres son
/// la base de los indicativos ("Lisboa" → "Lisboa Tower").
/// </summary>
public sealed record ManualExample(string Callsign, string Origin, string Destination, string DestinationIcao, string Sid, string Star,
    string DepartureRunway, string ArrivalRunway, string CruiseLevel)
{
    /// <summary>Sin plan: un vuelo que no es de ningún lugar real.</summary>
    public static ManualExample Generic { get; } = new("ABC123", Tr.T("City"), Tr.T("Coast"), "ZZZZ", "ALPHA1A", "BRAVO1B", "09", "27", Tr.T("flight level 240"));
}

/// <summary>
/// El manual del ATC (ADR-135): las interacciones de un vuelo, por fase, armadas con las mismas plantillas que usa el ATC
/// (`data/phraseology`, en el idioma y estilo elegidos), así nunca queda desactualizado. Los valores son de ejemplo.
/// </summary>
public static class AtcManual
{
    // Valores de ejemplo que no dependen del vuelo (frecuencias, calles, fijos): no son de ningún aeropuerto real.
    private static Dictionary<string, string> Sample(ManualExample x, AtcLanguage language)
    {
        var departure = AtcStations.Callsign(x.Origin, AtcRole.Departure, language);
        return new(StringComparer.Ordinal)
        {
            ["callsign"] = x.Callsign, ["destination"] = x.DestinationIcao, ["sid"] = x.Sid, ["star"] = x.Star,
            ["runway"] = x.DepartureRunway, ["level"] = x.CruiseLevel, ["squawk"] = "4521", ["next_station"] = departure,
            ["frequency"] = "119.7", ["after_departure"] = $", after departure contact {departure} 119.7",
            ["after_departure_readback"] = $", after departure {departure} 119.7", ["atis"] = "Bravo", ["stand"] = "14",
            ["qnh"] = "1015", ["altimeter"] = "29.97", ["route"] = "Golf, Quebec", ["taxiway"] = "Hotel", ["wind"] = "180 degrees 8 knots",
            ["fix"] = "DELTA", ["approach"] = "ILS Zulu", ["transition"] = "ECHO", ["speed"] = "180", ["heading"] = "240",
            ["direction"] = "right", ["altitude"] = "3000", ["distance"] = "6", ["traffic"] = "Airbus A320", ["cross_level"] = "flight level 170",
            ["minutes"] = "5", ["verb"] = "climbing", ["cleared"] = x.CruiseLevel, ["element"] = "squawk", ["correction"] = "squawk 4521",
            ["in_use"] = x.ArrivalRunway, ["aircraft"] = "two aircraft", ["situation"] = "on your left", ["clock"] = "2",
            ["movement"] = "opposite direction",
        };
    }

    /// <summary>Las secciones del manual, en el orden de un vuelo.</summary>
    public static readonly string[] Sections =
        [Tr.T("Antes de salir"), Tr.T("Torre (salida)"), Tr.T("Salida y ruta"), Tr.T("Llegada"), Tr.T("Torre (llegada) y rodaje al puesto"), Tr.T("En cualquier momento"), Tr.T("Si algo no avanza")];

    /// <summary>
    /// Arma el manual con <paramref name="phrases"/>. Una entrada cuya plantilla no existe en ese idioma se omite. En
    /// español, las notas siguen igual (el piloto lee en español).
    /// </summary>
    public static IReadOnlyList<ManualEntry> Build(Phraseology phrases, AirlineTelephony? airlines, AtcLanguage language,
        ManualExample? example = null)
    {
        var x = example ?? ManualExample.Generic;
        var sample = Sample(x, language);
        var arrivalSample = new Dictionary<string, string>(sample) { ["runway"] = x.ArrivalRunway };
        var list = new List<ManualEntry>();
        string At(AtcRole role) => AtcStations.Callsign(x.Origin, role, language);
        string AtDest(AtcRole role) => AtcStations.Callsign(x.Destination, role, language);
        var center = AtcStations.Callsign(x.Origin, AtcRole.Center, language);
        void Add(string section, string title, string? pilotId, string? atcId, string note, string? station = null, bool arriving = false)
        {
            station ??= arriving ? AtDest(AtcRole.Ground) : At(AtcRole.Ground);
            var fields = arriving ? arrivalSample : sample;
            var pilot = Fill(phrases, pilotId, station, fields);
            var atc = Fill(phrases, atcId, station, fields);
            if (pilotId is not null && pilot is null || atcId is not null && atc is null)
                return;
            list.Add(new ManualEntry(section, title, pilot, pilot is null ? null : SpokenForm.Of(pilot, airlines, language),
                atc, atc is null ? null : SpokenForm.Of(atc, airlines, language), note));
        }
        var s = Sections;
        Add(s[0], Tr.T("Pedir la autorización de vuelo"), "pilot.request_clearance", "atc.clearance",
            Tr.T("A Autorizaciones (Delivery), con la letra del ATIS. Te da pista, salida (SID), nivel inicial y transponder."), At(AtcRole.Delivery));
        Add(s[0], Tr.T("Colacionar la autorización"), "pilot.readback_clearance", "atc.readback_correct",
            Tr.T("Repite todo. Si te equivocas en un dato, el ATC corrige solo ese dato."), At(AtcRole.Delivery));
        Add(s[0], Tr.T("Pedir encendido (opcional)"), "pilot.request_startup", "atc.startup_approved", Tr.T("A Ground, con puesto, ATIS y QNH."));
        Add(s[0], Tr.T("Pedir pushback"), "pilot.request_pushback", "atc.pushback_approved",
            Tr.T("A Ground. Si hay tráfico detrás, te hace esperar y te llama cuando pase."));
        Add(s[0], Tr.T("Pedir rodaje"), "pilot.request_taxi", "atc.taxi", Tr.T("Con los motores en marcha. La ruta se dibuja en el radar."));
        Add(s[0], Tr.T("Colacionar el rodaje"), "pilot.readback_taxi", null, Tr.T("Pista y calles, en orden."));
        Add(s[1], Tr.T("Avisar que estás listo"), "pilot.ready_for_departure", "atc.line_up_and_wait",
            Tr.T("En el punto de espera. Según la pista: alinear y esperar, despegar, o esperar fuera."), At(AtcRole.Tower));
        Add(s[1], Tr.T("Esperar fuera de la pista"), null, "atc.hold_short", Tr.T("Hay alguien en la pista o una llegada en final. La Torre te llama."), At(AtcRole.Tower));
        Add(s[1], Tr.T("Autorización de despegue"), "pilot.readback_takeoff", "atc.cleared_for_takeoff", Tr.T("Colaciona pista y \"cleared for take-off\"."), At(AtcRole.Tower));
        Add(s[2], Tr.T("Primer contacto con Salida"), "pilot.check_in_departure", "atc.identified_via_sid",
            Tr.T("Pasados 1000 ft: indicativo, altitud y SID."), At(AtcRole.Departure));
        Add(s[2], Tr.T("Pedir otro nivel"), "pilot.request_climb", "atc.climb", Tr.T("En crucero; también para bajar a un nivel."), center);
        Add(s[2], Tr.T("Pedir directo a un fijo"), "pilot.request_direct", "atc.route_direct", Tr.T("Si el fijo está por delante en tu ruta."), center);
        Add(s[2], Tr.T("Pedir descenso antes de tiempo"), "pilot.request_descent", "atc.expect_descent_after",
            Tr.T("Lejos del punto de descenso, te dice después de qué fijo esperarlo."), center);
        Add(s[3], Tr.T("Descenso con QNH"), "pilot.readback_descend_qnh", "atc.descend_qnh", Tr.T("La Aproximación del destino, con el QNH."), AtDest(AtcRole.Approach), arriving: true);
        Add(s[3], Tr.T("Aproximación esperada"), null, "atc.expect_approach_via", Tr.T("Prepárala en el FMS: tipo, letra, pista y transición."), AtDest(AtcRole.Approach), arriving: true);
        Add(s[3], Tr.T("Autorización de aproximación"), "pilot.readback_cleared_approach_via", "atc.cleared_approach_via",
            Tr.T("Cerca del IAF o a ~18 NM por la ruta. Desde ahí puedes bajar a la altitud de la carta."), AtDest(AtcRole.Approach), arriving: true);
        Add(s[3], Tr.T("Reducir velocidad"), "pilot.readback_reduce_speed", "atc.reduce_speed",
            Tr.T("Por secuencia o en la final. Si ya vienes desacelerando, no te lo repite."), AtDest(AtcRole.Approach), arriving: true);
        Add(s[3], Tr.T("Vectores a la final"), "pilot.readback_vector_for_approach", "atc.vector_for_approach",
            Tr.T("Si no tienes un procedimiento que te lleve a la final. Colaciona el rumbo."), AtDest(AtcRole.Approach), arriving: true);
        Add(s[4], Tr.T("Avisar en final"), "pilot.arrival_final", "atc.cleared_to_land", Tr.T("A la Torre. Con la pista ocupada: \"continue approach\"."), AtDest(AtcRole.Tower), arriving: true);
        Add(s[4], Tr.T("Motor y al aire"), "pilot.going_around", "atc.missed_approach_published", Tr.T("La frustrada publicada; después te reintegran."), AtDest(AtcRole.Tower), arriving: true);
        Add(s[4], Tr.T("Pista libre"), "pilot.runway_vacated", "atc.taxi_to_stand", Tr.T("A Ground: te da puesto y ruta."), arriving: true);
        Add(s[5], Tr.T("Que te repitan"), "pilot.say_again", null, Tr.T("Repite la última instrucción de esa dependencia."));
        Add(s[5], Tr.T("Prueba de radio"), "pilot.radio_check", "atc.radio_check", Tr.T("Comprueba que te recibe."));
        Add(s[5], Tr.T("No puedes cumplir"), "pilot.unable", null, Tr.T("El ATC te da otra instrucción."));
        Add(s[6], Tr.T("¿Qué tengo autorizado?"), "pilot.confirm_clearance", null,
            Tr.T("Te dice lo vigente y vuelve a revisar lo que te tenía esperando. El copiloto lo pregunta solo si esperas de más."));
        Add(s[6], Tr.T("Pedir lo mismo otra vez"), null, null,
            Tr.T("La autorización, el pushback, el rodaje o \"listo\" otra vez: te repiten lo vigente (mismo transponder) o, si lo que te ")
            + Tr.T("tenía esperando ya pasó, te lo dan ahora. Nunca vuelve atrás."));
        Add(s[6], Tr.T("Pedir algo a la dependencia que no es"), null, null,
            Tr.F("Te dicen a quién llamar y en qué frecuencia (\"contact {0} 121.7\").", AtDest(AtcRole.Ground)));
        return list;
    }

    private static string? Fill(Phraseology phrases, string? id, string station, Dictionary<string, string> sample)
    {
        if (id is null || !phrases.Templates.ContainsKey(id))
            return null;
        var fields = new Dictionary<string, string>(sample) { ["station"] = station };
        foreach (var f in phrases.FieldsOf(id))
            fields.TryAdd(f, "…");
        return phrases.Canonical(id, fields);
    }
}

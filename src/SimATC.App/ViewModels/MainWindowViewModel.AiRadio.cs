using SimATC.Core.Atc;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Hito 4e-1: los aviones IA en la frecuencia. El motor decide y publica las líneas (<see cref="AiRadio"/>); aquí se
/// arman con la fraseología (§8) y van al chat de cada radio sintonizada en esa frecuencia (COM1 a COM3). Lo que dice el piloto ocupa la
/// frecuencia para que los IA no le hablen encima (RF-38).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Caracteres por segundo al hablar por radio, para estimar cuánto ocupa la frecuencia un mensaje.</summary>
    private const double CharsPerSecond = 14;

    /// <summary>Le da a la radio de los IA las frecuencias del aeropuerto (al crear el motor y al cambiar de aeropuerto).</summary>
    private void ConfigureAiRadio()
    {
        if (_engine is not { } engine)
            return;
        if (_stations?.For(AtcRole.Ground) is not { } ground || _stations.For(AtcRole.Tower) is not { } tower)
        {
            engine.Radio.Frequencies = null;
            return;
        }
        // Si la autorización ya dice a quién llamar después de despegar (SCEL, AIP), la Torre no lo repite.
        var departure = _atcData?.DepartureHandoffTo is null ? _stations.For(AtcRole.Departure)?.Khz ?? 0 : 0;
        // Las llegadas hablan con Aproximación (descensos, velocidad, aproximación) y después con la Torre.
        engine.Radio.Frequencies = new RadioFrequencies(ground.Khz, tower.Khz, departure, _stations.For(AtcRole.Approach)?.Khz ?? 0);
    }

    /// <summary>
    /// La frecuencia de Centro con que está el piloto en ruta (COM1 sintonizada en un Centro), para que los IA en crucero cerca
    /// hablen ahí (ADR-089); 0 en tierra o fuera de un Centro.
    /// </summary>
    private void UpdateEnrouteRadio()
    {
        if (_engine is not { } engine)
            return;
        var khz = UserState is { OnGround: false, Com1ActiveKhz: > 0 } u && _session?.StationOnFrequency(u.Com1ActiveKhz) is { Role: AtcRole.Center } c
            ? c.Khz : 0;
        engine.Radio.EnrouteKhz = khz;
    }

    /// <summary>Del reloj de 1 s: muestra lo que dijeron los IA y su ATC en la frecuencia de COM1.</summary>
    private void ShowAiRadio()
    {
        if (_engine is not { } engine)
            return;
        while (engine.Radio.Lines.TryDequeue(out var line))
        {
            // Al chat de cada radio sintonizada en esa frecuencia (COM1 a COM3).
            // Con el avión apagado no se oye nada (y no se acumula para después).
            if (!RadiosPowered || !AnyRadioOn(line.Khz) || _phrases is not { } phrases || _stations is null)
                continue;
            // En ruta (ADR-089) la dependencia es el Centro con que está el piloto en esa frecuencia.
            var station = line.Station == AtcRole.Center ? _session?.StationOnFrequency(line.Khz) : _stations.For(line.Station);
            string? text;
            try
            {
                text = AiText(line, phrases, station);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or FormatException or ArgumentException)
            {
                // Una frase mal armada de un IA se salta: nunca cierra la app (crash del 2026-11-15).
                Log(Tr.F("{0}: no se pudo armar su frase {1} ({2}).", line.Callsign, line.Kind, ex.Message));
                continue;
            }
            if (text is null)
                continue;
            var message = line.FromAtc
                ? new CommsMessage(DateTime.UtcNow, $"{station?.Callsign} {station?.FrequencyText}", Speaker.Atc, text,
                    line.Station switch { AtcRole.Ground => "GND", AtcRole.Tower => "TWR", AtcRole.Center => "CTR", _ => "APP" })
                : new CommsMessage(DateTime.UtcNow, line.Callsign, Speaker.OtherAircraft, text, AiAvatar(line.Callsign));
            var khz = line.Khz;
            // Con voz, aparece cuando empieza a sonar, y los IA esperan lo que dura de verdad (RF-38).
            _ = Speak(khz, line.FromAtc ? Voice.VoiceRole.Atc : Voice.VoiceRole.Pilot, line.FromAtc ? station?.Callsign ?? "ATC" : line.Callsign, text,
                duration =>
                {
                    Comms.AddHeard(khz, message);
                    if (duration is { } d)
                        engine.Radio.MarkBusy(khz, d + Audio.RadioVoice.GapS);
                });
        }
    }

    /// <summary>Los aviones IA en tierra (del motor), con su tipo como se dice por radio, para los avisos al piloto.</summary>
    private List<GroundTraffic> GroundTrafficNearby()
    {
        var list = new List<GroundTraffic>();
        if (_engine is not { } engine)
            return list;
        foreach (var a in engine.Snapshot())
        {
            if (a.AglFt > 5 || a.Callsign is null)
                continue;
            var type = _flights.TryGetValue(a.ObjectId, out var f) ? f.Aircraft.Type : null;
            list.Add(new GroundTraffic(a.ObjectId, a.Callsign, Core.Fleet.AircraftNames.Spoken(type, _settings.AtcLanguage), a.Position, a.HeadingDeg, a.SpeedKt));
        }
        return list;
    }

    /// <summary>El tipo del piloto como se dice por radio (del plan de SimBrief; "aircraft" sin plan).</summary>
    private string UserTypeSpoken => Core.Fleet.AircraftNames.Spoken(Plan?.AircraftIcao, _settings.AtcLanguage);

    /// <summary>El ícono de un avión IA: las letras de su aerolínea ("LAN", "SKU").</summary>
    private static string AiAvatar(string callsign) => new([.. callsign.TakeWhile(char.IsLetter).Take(3)]);

    /// <summary>La frase de esa línea, armada con las plantillas de §8.</summary>
    private string? AiText(RadioLine line, Core.Comms.Phraseology phrases, AtcStation? station)
    {
        var tower = _stations?.For(AtcRole.Tower);
        var next = line.NextKhz > 0 ? _stations?.OnFrequency(line.NextKhz) : null;
        var runway = line.Runway ?? "";
        var route = string.Join(", ", line.Taxiways);
        var f = new Dictionary<string, string> { ["callsign"] = line.Callsign, ["station"] = station?.Callsign ?? "", ["runway"] = runway };
        if (next is not null)
        {
            f["next_station"] = next.Callsign;
            f["frequency"] = next.FrequencyText;
        }
        string Say(string id) => phrases.Say(id, f);
        return line.Kind switch
        {
            RadioLineKind.RequestPushback => Say("pilot.request_pushback_no_stand"),
            RadioLineKind.PushbackApproved => Say("atc.pushback_approved"),
            RadioLineKind.Standby => Say("atc.standby"),
            RadioLineKind.ReadbackPushback => Say("pilot.readback_pushback"),
            RadioLineKind.RequestTaxi => Say("pilot.request_taxi"),
            RadioLineKind.Taxi when route.Length > 0 => phrases.Say("atc.taxi", new Dictionary<string, string>(f) { ["route"] = route }),
            RadioLineKind.Taxi => Say("atc.taxi_no_route"),
            RadioLineKind.ReadbackTaxi when route.Length > 0 => phrases.Say("pilot.readback_taxi", new Dictionary<string, string>(f) { ["route"] = route }),
            RadioLineKind.ReadbackTaxi => Say("pilot.readback_taxi_no_route"),
            RadioLineKind.ContactTower or RadioLineKind.ContactDeparture when next is not null => Say("atc.contact"),
            RadioLineKind.ReadbackContactTower or RadioLineKind.ReadbackContactDeparture when next is not null => Say("pilot.readback_contact"),
            RadioLineKind.ReadyForDeparture => phrases.Say("pilot.ready_for_departure", new Dictionary<string, string>(f) { ["station"] = tower?.Callsign ?? "" }),
            RadioLineKind.HoldShort => Say("atc.hold_short"),
            RadioLineKind.ReadbackHoldShort => Say("pilot.readback_hold_short"),
            // Condicional detrás de la llegada que aterriza (ADR-095; Detail = su tipo).
            RadioLineKind.LineUp when line.Detail is { Length: > 0 } landing => phrases.Say("atc.line_up_behind_landing",
                new Dictionary<string, string>(f) { ["traffic"] = Core.Fleet.AircraftNames.Spoken(landing, _settings.AtcLanguage) }),
            RadioLineKind.ReadbackLineUp when line.Detail is { Length: > 0 } landing => phrases.Say("pilot.readback_line_up_behind_landing",
                new Dictionary<string, string>(f) { ["traffic"] = Core.Fleet.AircraftNames.Spoken(landing, _settings.AtcLanguage) }),
            RadioLineKind.LineUp => Say("atc.line_up_and_wait"),
            RadioLineKind.ReadbackLineUp => Say("pilot.readback_line_up"),
            RadioLineKind.Takeoff when Weather is { } w => phrases.Say("atc.cleared_for_takeoff",
                new Dictionary<string, string>(f) { ["wind"] = AtcWords.Wind(w.WindFromMagneticDeg, w.WindKt, w.GustKt, _settings.AtcLanguage) }),
            RadioLineKind.Takeoff => Say("atc.cleared_for_takeoff_no_wind"),
            RadioLineKind.ReadbackTakeoff => Say("pilot.readback_takeoff"),
            // Hito 4e-2: el tráfico es el piloto, dicho por su tipo ("the A320").
            RadioLineKind.GiveWay => phrases.Say("atc.give_way", new Dictionary<string, string>(f) { ["traffic"] = UserTypeSpoken, ["direction"] = AtcWords.Direction(line.Detail != RadioLine.RightToLeft, _settings.AtcLanguage) }),
            RadioLineKind.ReadbackGiveWay => phrases.Say("pilot.readback_give_way", new Dictionary<string, string>(f) { ["traffic"] = UserTypeSpoken }),
            RadioLineKind.Follow => phrases.Say("atc.follow", new Dictionary<string, string>(f) { ["traffic"] = UserTypeSpoken }),
            RadioLineKind.ReadbackFollow => phrases.Say("pilot.readback_follow", new Dictionary<string, string>(f) { ["traffic"] = UserTypeSpoken }),
            // Llegadas (§8.3b).
            RadioLineKind.ArrivalFinal => phrases.Say("pilot.arrival_final", new Dictionary<string, string>(f) { ["station"] = tower?.Callsign ?? "" }),
            RadioLineKind.ContinueApproach => Say("atc.continue_approach"),
            RadioLineKind.ReadbackContinueApproach => Say("pilot.readback_continue_approach"),
            RadioLineKind.ClearedToLand when Weather is { } lw => phrases.Say("atc.cleared_to_land",
                new Dictionary<string, string>(f) { ["wind"] = AtcWords.Wind(lw.WindFromMagneticDeg, lw.WindKt, lw.GustKt, _settings.AtcLanguage) }),
            RadioLineKind.ClearedToLand => Say("atc.cleared_to_land_no_wind"),
            RadioLineKind.ReadbackClearedToLand => Say("pilot.readback_land"),
            RadioLineKind.GoAround => Say("atc.go_around"),
            RadioLineKind.ReadbackGoAround => Say("pilot.readback_go_around"),
            RadioLineKind.Descend or RadioLineKind.ReadbackDescend => DescendText(line, phrases, f),
            // En ruta (ADR-089): primer contacto con el Centro y "identified".
            RadioLineKind.EnrouteCheckIn when double.TryParse(line.Detail, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lvl) =>
                phrases.Say("pilot.check_in_enroute", new Dictionary<string, string>(f) { ["level"] = AtcWords.Level(lvl, lvl > TransitionAltitudeFt, _settings.AtcLanguage) }),
            RadioLineKind.EnrouteIdentified => Say("atc.identified_no_level"),
            RadioLineKind.Hold or RadioLineKind.ReadbackHold => HoldText(line, phrases, f),
            RadioLineKind.Speed or RadioLineKind.ReadbackSpeed when line.Detail is "0" =>
                Say(line.Kind == RadioLineKind.Speed ? "atc.resume_speed" : "pilot.readback_resume_speed"),
            RadioLineKind.Speed => phrases.Say("atc.reduce_speed", new Dictionary<string, string>(f) { ["speed"] = line.Detail ?? "" }),
            RadioLineKind.ReadbackSpeed => phrases.Say("pilot.readback_reduce_speed", new Dictionary<string, string>(f) { ["speed"] = line.Detail ?? "" }),
            RadioLineKind.ClearedApproach => phrases.Say("atc.cleared_approach", new Dictionary<string, string>(f) { ["approach"] = line.Detail ?? "" }),
            RadioLineKind.ExpectApproach => phrases.Say("atc.expect_approach", new Dictionary<string, string>(f) { ["approach"] = line.Detail ?? "" }),
            RadioLineKind.ReadbackExpectApproach => phrases.Say("pilot.readback_expect_approach", new Dictionary<string, string>(f) { ["approach"] = line.Detail ?? "" }),
            RadioLineKind.ReadbackClearedApproach => phrases.Say("pilot.readback_cleared_approach", new Dictionary<string, string>(f) { ["approach"] = line.Detail ?? "" }),
            // Llegada en tierra (ADR-065, CAP 413 cap. 4).
            RadioLineKind.VacateContactGround when next is not null => line.Detail is { Length: > 0 } exitWay
                ? phrases.Say("atc.vacate_contact_ground", new Dictionary<string, string>(f) { ["taxiway"] = exitWay })
                : Say("atc.when_vacated_contact_ground"),
            RadioLineKind.ReadbackVacateContactGround when next is not null => line.Detail is { Length: > 0 } exitWay2
                ? phrases.Say("pilot.readback_vacate_contact_ground", new Dictionary<string, string>(f) { ["taxiway"] = exitWay2 })
                : Say("pilot.readback_when_vacated_contact_ground"),
            RadioLineKind.RunwayVacated => Say("pilot.runway_vacated"),
            RadioLineKind.TaxiToStand when route.Length > 0 => phrases.Say("atc.taxi_to_stand",
                new Dictionary<string, string>(f) { ["stand"] = line.Detail ?? "", ["route"] = route }),
            RadioLineKind.TaxiToStand => phrases.Say("atc.taxi_to_stand_no_route", new Dictionary<string, string>(f) { ["stand"] = line.Detail ?? "" }),
            RadioLineKind.ReadbackTaxiToStand when route.Length > 0 => phrases.Say("pilot.readback_taxi_to_stand",
                new Dictionary<string, string>(f) { ["stand"] = line.Detail ?? "", ["route"] = route }),
            RadioLineKind.ReadbackTaxiToStand => phrases.Say("pilot.readback_taxi_to_stand_no_route", new Dictionary<string, string>(f) { ["stand"] = line.Detail ?? "" }),
            // Cruce de pista (ADR-068, CAP 413 §4.15).
            RadioLineKind.CrossRunway => phrases.Say("atc.cross_runway", new Dictionary<string, string>(f) { ["runway"] = line.Detail ?? "" }),
            RadioLineKind.ReadbackCrossRunway => phrases.Say("pilot.readback_cross_runway", new Dictionary<string, string>(f) { ["runway"] = line.Detail ?? "" }),
            _ => null,
        };
    }

    /// <summary>
    /// Espera (ADR-076, CAP 413 §6.11): "hold at (fijo) (nivel), expect onward clearance at time (minutos)"; el minuto es la
    /// hora UTC actual más la demora estimada.
    /// </summary>
    private string? HoldText(RadioLine line, Core.Comms.Phraseology phrases, Dictionary<string, string> f)
    {
        var parts = (line.Detail ?? "").Split('|');
        if (parts.Length < 3 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ft)
            || !int.TryParse(parts[2], out var delay))
            return null;
        var fields = new Dictionary<string, string>(f)
        {
            ["fix"] = parts[0],
            ["level"] = AtcWords.Level(ft, ft > TransitionAltitudeFt, _settings.AtcLanguage),
            ["minutes"] = DateTime.UtcNow.AddMinutes(delay).Minute.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
        };
        return phrases.Say(line.Kind == RadioLineKind.Hold ? "atc.hold" : "pilot.readback_hold", fields);
    }

    /// <summary>Descenso: nivel de vuelo sobre la altitud de transición; bajo ella, altitud con el QNH (§8.3b).</summary>
    private string? DescendText(RadioLine line, Core.Comms.Phraseology phrases, Dictionary<string, string> f)
    {
        if (!double.TryParse(line.Detail, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ft))
            return null;
        var atc = line.Kind == RadioLineKind.Descend;
        if (ft > TransitionAltitudeFt || Weather is not { } w)
            return phrases.Say(atc ? "atc.descend" : "pilot.readback_descend",
                new Dictionary<string, string>(f) { ["level"] = AtcWords.Level(ft, ft > TransitionAltitudeFt, _settings.AtcLanguage) });
        return phrases.Say(atc ? "atc.descend_qnh" : "pilot.readback_descend_qnh", new Dictionary<string, string>(f)
        {
            ["level"] = AtcWords.Level(ft, false, _settings.AtcLanguage),
            ["qnh"] = Math.Round(w.QnhHpa).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>El piloto (o el ATC que le responde) ocupa la frecuencia: los IA esperan (RF-38).</summary>
    private void MarkFrequencyBusy(int? khz, int chars)
    {
        if (khz is { } k && _engine is { } engine)
            engine.Radio.MarkBusy(k, 1 + chars / CharsPerSecond);
    }
}

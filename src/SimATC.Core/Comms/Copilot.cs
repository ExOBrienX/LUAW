using SimATC.Core.Atc;

namespace SimATC.Core.Comms;

/// <summary>Qué colaciona el copiloto (RF-34b).</summary>
public enum CopilotReadbacks
{
    Never,
    /// <summary>Solo los cambios de frecuencia ("contact Tower 118.1").</summary>
    FrequencyChanges,
    Always,
}

/// <summary>
/// Lo que el piloto le deja hacer al copiloto (RF-30, RF-34b), combinable: colacionar, iniciar los llamados de rutina
/// (autorización, pushback, rodaje, listo para salir, primer contacto con Approach), contestar si el piloto no colaciona
/// a tiempo (RF-33c, al "how do you read") y sintonizar la frecuencia nueva.
/// </summary>
/// <param name="AskClearance">Pregunta antes de pedir la autorización IFR (decisión del capitán).</param>
/// <param name="AskPushback">Pregunta antes de pedir el pushback (decisión del capitán).</param>
/// <param name="AskTaxi">Pregunta antes de pedir el rodaje; si no, lo pide solo al terminar el pushback con motores.</param>
/// <param name="AskReady">Pregunta antes de "listo para salida"; si no, lo dice al detenerse en el punto de espera.</param>
public sealed record CopilotSettings(CopilotReadbacks Readbacks, bool RoutineCalls, bool Rescue, bool Tune,
    bool AskClearance = true, bool AskPushback = true, bool AskTaxi = false, bool AskReady = false)
{
    public static CopilotSettings Off { get; } = new(CopilotReadbacks.Never, false, false, false);

    public bool AnyOn => Readbacks != CopilotReadbacks.Never || RoutineCalls || Rescue;
}

/// <summary>
/// Lo que dice el copiloto y a quién (la app lo envía como un mensaje del piloto, por esa frecuencia). Con
/// <paramref name="Ask"/> no lo transmite: es una decisión del capitán (pushback, rodaje, listo para salir…) y el copiloto
/// solo pregunta; el piloto confirma con la sugerencia (pedido de Marco, 2026-11-05).
/// </summary>
public sealed record CopilotAction(string Text, AtcStation Station, bool IsReadback, bool Ask = false);

/// <summary>
/// El copiloto automático por texto (hito 4f, RF-34b): dice lo mismo que "Qué decir ahora" (las plantillas de §8, nunca
/// una frase propia), siempre correcto y completo, después de una pausa humana de 1,5 a 4 s. Cada cosa la dice una sola
/// vez: si el ATC la rechaza, no insiste (lo resuelve el piloto). Si el piloto escribe, lo pendiente se cancela.
/// </summary>
public sealed class Copilot(Random? random = null)
{
    public const double MinDelayS = 1.5;
    public const double MaxDelayS = 4;

    private readonly Random _random = random ?? new Random();
    private string? _pendingKey;
    private double _dueS;
    private string? _doneKey;

    /// <summary>
    /// Del reloj del ATC. Devuelve lo que hay que decir ahora, o null. <paramref name="atcTalking"/>: el ATC está
    /// transmitiendo o el piloto todavía lo está leyendo (no se lo pisa: la pausa empieza después).
    /// </summary>
    public CopilotAction? Tick(PilotSession s, CopilotSettings settings, double nowS, bool atcTalking)
    {
        if (Next(s, settings, nowS) is not { } candidate)
        {
            _pendingKey = null;
            return null;
        }
        var key = Key(s, candidate);
        if (key == _doneKey)
            return null;
        if (key != _pendingKey || atcTalking)
        {
            // Nuevo (o el ATC sigue hablando): la pausa cuenta desde ahora.
            if (key != _pendingKey)
                _pendingKey = key;
            _dueS = nowS + MinDelayS + _random.NextDouble() * (MaxDelayS - MinDelayS);
            return null;
        }
        if (nowS < _dueS)
            return null;
        _doneKey = key;
        _pendingKey = null;
        return candidate;
    }

    /// <summary>El piloto habló: lo que el copiloto iba a decir se cancela (y no lo repite).</summary>
    public void PilotSpoke(PilotSession s, CopilotSettings settings)
    {
        if (Next(s, settings, double.NegativeInfinity) is { } candidate)
            _doneKey = Key(s, candidate);
        _pendingKey = null;
    }

    /// <summary>Lo que el copiloto diría ahora, según lo que el piloto le deja hacer.</summary>
    private static CopilotAction? Next(PilotSession s, CopilotSettings settings, double nowS)
    {
        // El ATC lo dejó esperando de más (ADR-135): el copiloto pregunta una vez ("confirm clearance") y el ATC vuelve a
        // mirar lo que lo tenía esperando. Solo en las esperas donde el próximo paso es del ATC.
        if ((settings.RoutineCalls || settings.Readbacks != CopilotReadbacks.Never) && !s.AwaitingReadback && !s.AwaitingCheckIn
            && s.Phase is PilotPhase.HoldShortGiven or PilotPhase.LinedUp or PilotPhase.ArrivalTower
            && !(s.Phase == PilotPhase.ArrivalTower && s.LandingCleared) // ya autorizado: lo que sigue es del piloto
            && nowS - s.LastInstructionAtS > StuckAskS
            && s.RequestMenu().FirstOrDefault(r => r.Group == PilotSession.GroupStuck) is { } stuck && s.ExpectedStation is { } waitingOn)
            return new CopilotAction(stuck.Text, waitingOn, false);

        var suggestion = s.Suggestions().FirstOrDefault();
        if (suggestion is null)
            return null;
        if (s.AwaitingReadback)
        {
            var allowed = settings.Readbacks == CopilotReadbacks.Always
                          || settings.Readbacks == CopilotReadbacks.FrequencyChanges && s.ReadbackIsFrequencyChange
                          || settings.Rescue && s.Recalls >= 2;
            // En "hold position" o un aviso de tráfico, la colación también es del rodaje: la primera sugerencia.
            return allowed && s.ReadbackStation is { } st ? new CopilotAction(suggestion.Text, st, true) : null;
        }
        if (s.NextStep().Station is not { } station)
            return null;
        // Primer contacto tras un cambio de frecuencia (Approach al despegar, Centro, destino): va con las colaciones,
        // porque es la consecuencia del "contact…" que el copiloto ya colacionó.
        if (s.Phase == PilotPhase.Airborne || s.AwaitingCheckIn)
        {
            // Recién despegado no se llama a Salida a 100 ft: al pasar 1000 ft sobre el aeropuerto, o si la Torre ya lo pasó.
            if (s.Phase == PilotPhase.Airborne && !s.HandedToDeparture && s.AglFt is < DepartureCallAglFt)
                return null;
            return settings.Readbacks != CopilotReadbacks.Never || settings.RoutineCalls ? new CopilotAction(suggestion.Text, station, false) : null;
        }
        if (!settings.RoutineCalls)
            return null;
        // Decisiones del capitán (autorización y pushback): el copiloto pregunta. El rodaje lo pide solo con el pushback
        // terminado y motores encendidos, y "listo para salida" al detenerse en el punto de espera (salvo que se le pida
        // preguntar también por esos).
        (bool Ask, bool Ready)? step = s.Phase switch
        {
            PilotPhase.Parked => (settings.AskClearance, true),
            PilotPhase.Cleared when s.PushbackExpected => (settings.AskPushback, true),
            PilotPhase.Cleared => (settings.AskTaxi, s.EnginesRunning != false),
            PilotPhase.PushbackApproved => (settings.AskTaxi, s.PushedBack && s.EnginesRunning != false),
            PilotPhase.WithTower => (settings.AskReady, settings.AskReady || s.GroundSpeedKt is null or < 1),
            _ => null,
        };
        return step is { Ready: true } st2 ? new CopilotAction(suggestion.Text, station, false, Ask: st2.Ask) : null;
    }

    /// <summary>Esperando al ATC más que esto (s) sin una instrucción nueva, el copiloto pregunta. APROXIMADO.</summary>
    public const double StuckAskS = 150;

    /// <summary>Altura sobre el aeropuerto (ft) desde la que el copiloto hace el primer contacto con Salida.</summary>
    public const double DepartureCallAglFt = 1000;

    // Sin el texto: el primer contacto cambia con la altitud ("1600 pies", "1800 pies") y no es otro llamado. Con el número
    // de instrucción y no su texto: una instrucción repetida igual es otra y se colaciona otra vez.
    private static string Key(PilotSession s, CopilotAction a) =>
        $"{s.Phase}|{s.AwaitingReadback}|{s.AwaitingCheckIn}|{s.InstructionNumber}|{a.Station.Khz}|{a.IsReadback}|{a.Ask}";
}

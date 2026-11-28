using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.Core.Flight;

/// <summary>
/// Secuencia de llegadas por pista (etapa 7, ADR-021: Approach y Tower deciden): separación en final por estela, espera
/// si no hay hueco lejos del aeropuerto, autorización de aterrizaje con la pista libre y motor y al aire si no la hay a
/// 1 NM. Sin asignaciones en el tick.
/// </summary>
/// <summary>
/// El piloto como llegada (ADR-076): pista (índice del aeropuerto activo), distancia al umbral, velocidad, altitud y
/// estela. Lo fija la app con la sesión del ATC en la llegada; los IA lo separan como a cualquier otro.
/// </summary>
public readonly record struct PilotArrival(int RunwayIndex, double RemainingM, double SpeedKt, double AltitudeFt, WakeCategory Wake)
{
    public double RemainingNm => RemainingM / UnitConversions.MetersPerNauticalMile;
}

public sealed class ArrivalSequencer
{
    /// <summary>Separación mínima en final entre llegadas (NM), sin estela especial. APROXIMADO (radar 3 NM + margen).</summary>
    public const double BaseSpacingNm = 4;

    /// <summary>Con la pista libre se autoriza el aterrizaje a esta distancia (NM).</summary>
    public const double ClearToLandNm = 6;

    /// <summary>
    /// Sin autorización a esta distancia del umbral, motor y al aire (NM)… salvo que la pista vaya a quedar libre antes de
    /// que cruce el umbral (ver <see cref="ClearInS"/>): entonces sigue hasta <see cref="LateGoAroundNm"/>.
    /// </summary>
    public const double GoAroundNm = 0.8;

    /// <summary>Con la pista por liberarse, la decisión se toma como tarde aquí (≈ 130 ft sobre el umbral). APROXIMADO.</summary>
    public const double LateGoAroundNm = 0.4;

    /// <summary>Margen (s) entre que la pista queda libre y el cruce del umbral para seguir con una autorización tardía.</summary>
    public const double LateClearanceMarginS = 4;

    /// <summary>
    /// Segundos estimados para que la pista quede libre para esa llegada (0 si ya lo está, infinito si no se sabe o quien la
    /// ocupa está detenido). Como un controlador (Doc 4444 7.10.2: se autoriza con garantía razonable de que la separación
    /// existirá cuando cruce el umbral): si el de adelante está saliendo de la pista, no se frustra a la de atrás a 0,8 NM
    /// (RKSI 34R, PLAN-PULIDO bloque 3). Lo fija el motor.
    /// </summary>
    public Func<int, AirborneAgent, double>? ClearInS { get; set; }

    /// <summary>Entre esto y <see cref="HoldingMinNm"/> se manda a esperar solo si al hueco le faltan más de NearHoldShortNm.</summary>
    public const double NearHoldMinNm = 15;
    public const double NearHoldShortNm = 1.5;

    /// <summary>Más lejos que esto se puede mandar a esperar (NM por la ruta).</summary>
    public const double HoldingMinNm = 25;

    private AirborneAgent?[] _order = new AirborneAgent?[16];

    public Action<string>? Log { get; set; }

    /// <summary>
    /// ¿Hay salidas esperando en el punto de espera de esta pista? Entonces es mixta y cada llegada deja un hueco para una
    /// salida (<see cref="DepartureGapNm"/>, ADR-093). Lo fija el motor.
    /// </summary>
    public Func<int, bool>? DeparturesWaiting { get; set; }

    /// <summary>
    /// Hueco (NM) entre dos llegadas para que despegue una salida (ver <see cref="DepartureRollS"/>), con
    /// <see cref="DepartureGapMarginNm"/> de margen (la de atrás llega más rápido que la velocidad de final supuesta). Unas 7 NM.
    /// </summary>
    public static double DepartureGapNm(AirborneAgent follower, double leaderRotS) =>
        Math.Max(leaderRotS + DepartureRollS + Atc.TowerControl.TakeoffBeforeArrivalS,
            Atc.TowerControl.LineUpBeforeArrivalS + LineUpBehindAfterS) * FinalSpeedKt(follower) / 3600 + DepartureGapMarginNm;

    /// <summary>
    /// Con la autorización condicional (ADR-095) la salida se alinea mientras la de adelante frena y despega apenas libra la
    /// pista: el hueco es lo mayor entre (su ocupación + el paso a la carrera + los 55 s que pide la Torre para despegar) y
    /// (los 130 s para alinear + lo que tarda la de adelante en pasar la entrada). Antes era ocupación + 130 s (~9 NM).
    /// </summary>
    public const double DepartureRollS = 8;
    public const double LineUpBehindAfterS = 15;

    /// <summary>Margen del hueco para una salida (NM). Con 0,5 en Gatwick quedaba unos segundos corto (ADR-093). APROXIMADO.</summary>
    public const double DepartureGapMarginNm = 1.5;

    /// <summary>Con salidas esperando, una llegada lejos entra en espera si le falta más que esto para su hueco (NM). APROXIMADO.</summary>
    public const double DepartureHoldShortNm = 2;

    /// <summary>Lo que se dice por radio (hito de radio de llegadas): (avión, qué pasó).</summary>
    public event Action<AirborneAgent, ArrivalEvent>? Event;

    /// <summary>Separación por estela detrás de <paramref name="leader"/> (NM). Valores OACI Doc 4444 §8.7.3.4 redondeados.</summary>
    public static double SpacingNm(WakeCategory leader, WakeCategory follower) => (leader, follower) switch
    {
        (WakeCategory.Super, WakeCategory.Heavy) => 6,
        (WakeCategory.Super, _) => 8,
        (WakeCategory.Heavy, WakeCategory.Heavy) => 4,
        (WakeCategory.Heavy, WakeCategory.Medium) => 5,
        (WakeCategory.Heavy, WakeCategory.Light) => 6,
        (WakeCategory.Medium, WakeCategory.Light) => 5,
        _ => BaseSpacingNm,
    };

    /// <summary>IA más cerca del umbral que el piloto en su pista, en la última vuelta (la Torre no lo autoriza antes).</summary>
    public int PilotAhead { get; private set; }

    /// <summary>La llegada IA inmediatamente delante del piloto en su pista (para "number 2, follow…", ADR-085), o null.</summary>
    public AirborneAgent? PilotLeader { get; private set; }

    /// <param name="runwayOccupied">¿La pista está ocupada (salida en la pista, otra llegada rodando, el piloto)?</param>
    /// <param name="pilot">El piloto como llegada, si está en la secuencia (ADR-076).</param>
    public void Update(List<AirborneAgent> air, Func<int, AirborneAgent, bool> runwayOccupied, PilotArrival? pilot = null)
    {
        var ahead = 0;
        AirborneAgent? pilotLeader = null;
        for (var runway = 0; runway < 16; runway++)
        {
            var n = Collect(air, runway);
            if (n == 0)
                continue;
            var p = pilot is { } pa && pa.RunwayIndex == runway ? pa : (PilotArrival?)null;
            var departureGap = DeparturesWaiting?.Invoke(runway) == true;
            for (var i = 0; i < n; i++)
            {
                var a = _order[i]!;
                var leader = i > 0 ? _order[i - 1] : null;
                // El piloto va entre el de adelante y este: él es el líder.
                var pilotLeads = p is { } pp && pp.RemainingM < a.RemainingM && (leader is null || leader.RemainingM < pp.RemainingM);
                if (pilotLeads)
                    Space(a, Tr.T("el piloto"), p!.Value.RemainingNm, p.Value.Wake, p.Value.SpeedKt, p.Value.AltitudeFt, RotSpacingNm(a, PilotRotS));
                else if (leader is not null)
                    Space(a, leader.Callsign, leader.RemainingNm, leader.Wake, leader.SpeedKt, leader.AltitudeFt,
                        departureGap ? Math.Max(RotSpacingNm(a, RotS(leader)), DepartureGapNm(a, RotS(leader))) : RotSpacingNm(a, RotS(leader)),
                        departureGap, departureGap ? (EtaS(a) - EtaS(leader)) * FinalSpeedKt(a) / 3600 : null,
                        // Para la velocidad, el hueco en tiempo hasta el umbral (ADR-120): a 20 NM y 210 kt, 81 s son 4,7 NM,
                        // pero en el umbral a 140 kt son 3,1 y el de atrás frustraba (RKSI). Para la espera sigue la distancia
                        // (con el tiempo, la espera se disparaba de más y el flujo caía a la mitad).
                        (EtaS(a) - EtaS(leader)) * FinalSpeedKt(a) / 3600);
                else
                    Space(a, null, 0, default, 0, 0);
                if (p is { } q && a.RemainingM < q.RemainingM)
                {
                    ahead++;
                    pilotLeader = a; // ordenadas de menor a mayor: la última delante es la más cercana
                }
                var first = i == 0 && !(p is { } r && r.RemainingM < a.RemainingM);
                if (first)
                    Clear(a, runwayOccupied);
                else if (a.Phase is FlightPhase.Final && a.RemainingNm < GoAroundNm && !a.LandingCleared)
                    GoAround(a, "la pista no se liberó");
            }
        }
        PilotAhead = ahead;
        PilotLeader = pilotLeader;
    }

    /// <summary>Las llegadas a esa pista en el aire, ordenadas por distancia al umbral (inserción, sin asignar).</summary>
    private int Collect(List<AirborneAgent> air, int runway)
    {
        var n = 0;
        for (var i = 0; i < air.Count; i++)
        {
            var a = air[i];
            if (!a.ControlledArrival || a.Landing?.RunwayIndex != runway || a.Phase is FlightPhase.Rollout or FlightPhase.Landed or FlightPhase.Finished
                or FlightPhase.Flare or FlightPhase.GoAround)
                continue;
            if (n == _order.Length)
                Array.Resize(ref _order, n * 2); // solo si hay más llegadas que nunca
            var j = n++;
            var d = a.RemainingM;
            while (j > 0 && _order[j - 1]!.RemainingM > d)
            {
                _order[j] = _order[j - 1];
                j--;
            }
            _order[j] = a;
        }
        for (var i = n; i < _order.Length && _order[i] is not null; i++)
            _order[i] = null;
        return n;
    }

    /// <summary>
    /// Tiempo (s) que el de adelante va a ocupar la pista: desde el umbral hasta su salida a ~35 m/s de media (toma y
    /// frenado) más el viraje y librar la línea de espera. APROXIMADO. ADR-090: en SCEL (pistas de 3700 m, salidas del
    /// lado del puesto) tardaban más de lo que daban 4 NM y los de atrás hacían motor y al aire (4 en 20 min).
    /// </summary>
    public static double RotS(AirborneAgent leader)
    {
        // ADR-120: frenado del umbral a la salida a la media entre su velocidad de aproximación y la de esa salida, y de ahí
        // el viraje hasta tener la cola fuera (~210 m) a la velocidad de la salida: una salida a 90° se toma a 15 kt y son
        // ~27 s, no los 20 fijos de antes (RKSI 34R: el de atrás llegaba encima y frustraba).
        var exitKt = leader.Landing?.ExitSpeedKt ?? AirborneAgent.ExitSpeedKt;
        var along = leader.Landing?.ExitAlongM ?? 2000;
        return along / ((leader.VappKt + exitKt) / 2 * 0.514444) + ExitClearM / (exitKt * 0.514444);
    }

    /// <summary>Del inicio del viraje de salida a tener la cola fuera de la pista (m): arco, punto de espera y largo del avión. APROXIMADO.</summary>
    public const double ExitClearM = 210;

    /// <summary>Ocupación de pista supuesta para el piloto (s): una salida a media pista.</summary>
    public const double PilotRotS = 80;

    /// <summary>
    /// Separación (NM) para que el de adelante deje la pista antes de que el de atrás llegue al punto de motor y al aire,
    /// a la velocidad de final (la separación cuenta en la final, no donde va más rápido), con medio NM de margen.
    /// </summary>
    public static double RotSpacingNm(AirborneAgent follower, double leaderRotS) =>
        leaderRotS * FinalSpeedKt(follower) / 3600 + GoAroundNm + 0.5;

    /// <summary>
    /// Tiempo estimado hasta el umbral (s) con un perfil simple: velocidad de final las últimas 4 NM, desacelerando desde
    /// 210 kt entre 12 y 4 NM, y más lejos el promedio entre la actual (hasta 250 kt) y 210. APROXIMADO (ADR-095).
    /// </summary>
    public static double EtaS(AirborneAgent a)
    {
        var d = a.RemainingNm;
        var final = FinalSpeedKt(a);
        var far = Math.Clamp(a.SpeedKt, final, 250);
        // 160 kt entre 4,5 y 10 NM, 180 kt hasta 12 NM (como vuelan los IA, ADR-120) y más lejos hacia 210.
        var hours = Math.Min(d, 4.5) / final + Math.Clamp(d - 4.5, 0, 5.5) / Math.Max(final, 160) + Math.Clamp(d - 10, 0, 2) / 180
                    + Math.Max(d - 12, 0) / ((far + 210) / 2);
        return hours * 3600;
    }

    /// <summary>Velocidad de final supuesta (kt) por categoría. APROXIMADO.</summary>
    private static double FinalSpeedKt(AirborneAgent a) => a.VappKt;

    private void Space(AirborneAgent a, string? leader, double leaderNm, WakeCategory leaderWake, double leaderKt, double leaderAltFt,
        double rotSpacingNm = 0, bool departureGap = false, double? timeGapNm = null, double? speedGapNm = null)
    {
        if (leader is null)
        {
            a.SequenceSpeedKt = 0;
            if (a.Phase == FlightPhase.Holding)
                Resume(a);
            return;
        }
        // Por estela y por el tiempo que el de adelante ocupa la pista (lo que sea mayor).
        var required = Math.Max(SpacingNm(leaderWake, a.Wake), rotSpacingNm);
        // Con salidas esperando, el hueco en tiempo hasta el umbral (en NM a la velocidad de final): a 20 NM y 240 kt un
        // hueco de 11 NM terminaba en 6–7 en el umbral (ADR-095, Manchester).
        // Nunca negativo: tras un motor y al aire el de atrás puede tener un tiempo estimado menor que su líder (decía "-22,1 NM").
        var gap = Math.Max(0, timeGapNm ?? a.RemainingNm - leaderNm);
        if (a.Phase == FlightPhase.Holding)
        {
            if (gap >= required + 4)
                Resume(a);
            return;
        }
        // A esperar si no hay hueco: lejos (por la ruta), o después de un motor y al aire aunque esté cerca (si no, vuelve a la
        // final sin hueco y frustra otra vez: prueba de Marco, 2026-11-05). ADR-076.
        var mayHold = a.RemainingNm > HoldingMinNm || a.GoArounds > 0 && a.Phase is not FlightPhase.Final and not FlightPhase.GoAround;
        // Más cerca, antes de la final, también se lo saca de la secuencia si el hueco no alcanza ni bajando la velocidad
        // (ADR-091: si no, llega a 0,8 NM con el de adelante aterrizando y hace motor y al aire).
        var shortGap = a.RemainingNm > NearHoldMinNm && a.RemainingNm <= HoldingMinNm
                       && a.Phase is not FlightPhase.Final and not FlightPhase.GoAround && gap < required - NearHoldShortNm;
        // Lejos, la espera es solo si el hueco ni se acerca al de estela (la ocupación de pista se recupera bajando la
        // velocidad durante el camino).
        // Con salidas esperando en una pista mixta (ADR-095), lejos también espera si le faltan más de
        // DepartureHoldShortNm para el hueco de una salida: si no, cuando la demanda supera la capacidad, el chorro de
        // llegadas no se corta nunca y las salidas no despegan (Manchester y México: 0 despegues).
        var holdBelow = departureGap ? required - DepartureHoldShortNm : SpacingNm(leaderWake, a.Wake) - 3;
        if (gap < holdBelow && mayHold || shortGap)
        {
            a.Hold();
            Log?.Invoke(Tr.F("{0}: en espera sobre {1}, {2:0.0} NM detrás de {3}.", a.Callsign, (a.HoldFixName.Length > 0 ? a.HoldFixName : Tr.T("un punto de su ruta")), gap, leader));
            Event?.Invoke(a, ArrivalEvent.Holding);
            return;
        }
        // Más lento que el de adelante mientras no tenga la separación (sin bajar de su velocidad de final).
        var speedGap = speedGapNm is { } sg ? Math.Min(gap, Math.Max(0, sg)) : gap;
        a.SequenceSpeedKt = speedGap < required + 1 ? Math.Max(120, leaderKt / (1 + 0.02 * leaderAltFt / 1000) - 20) : 0;
    }

    private void Resume(AirborneAgent a)
    {
        a.ResumeFromHolding();
        Log?.Invoke(Tr.F("{0}: sale de la espera.", a.Callsign));
        Event?.Invoke(a, ArrivalEvent.LeftHolding);
    }

    private void Clear(AirborneAgent a, Func<int, AirborneAgent, bool> runwayOccupied)
    {
        if (a.Phase != FlightPhase.Final || a.LandingCleared)
            return;
        var occupied = runwayOccupied(a.Landing!.RunwayIndex, a);
        if (!occupied && a.RemainingNm <= ClearToLandNm)
        {
            a.LandingCleared = true;
            Event?.Invoke(a, ArrivalEvent.ClearedToLand);
        }
        else if (occupied && a.RemainingNm < GoAroundNm)
        {
            // Si quien la ocupa la deja antes de que esta cruce el umbral, sigue ("autorización tardía") hasta el último punto.
            var toThresholdS = a.RemainingM / Math.Max(1, a.SpeedKt * 0.514444);
            if (a.RemainingNm > LateGoAroundNm && ClearInS?.Invoke(a.Landing.RunwayIndex, a) is { } clearS
                && clearS + LateClearanceMarginS < toThresholdS)
                return;
            GoAround(a, "pista ocupada");
        }
    }

    private void GoAround(AirborneAgent a, string why)
    {
        var l = a.Landing!;
        a.GoAround(FlightPlanner.GoAround(l, a.Position), l.ElevationFt + 3000);
        Log?.Invoke(Tr.F("{0}: motor y al aire ({1}).", a.Callsign, Tr.T(why)));
        Event?.Invoke(a, ArrivalEvent.GoAround);
    }

    /// <summary>A esta distancia por la ruta (NM) del umbral se autoriza la aproximación. APROXIMADO.</summary>
    public const double ClearApproachNm = 25;

    /// <summary>
    /// Approach (etapa 7, paso 7 de la prueba de Marco): autoriza a cada llegada a bajar por escalones, al nivel que su
    /// perfil pide unas 20 NM más adelante (redondeado a 1000 ft y nunca bajo la altitud mínima de la carta en ese tramo);
    /// cerca del aeropuerto la autoriza a la aproximación y desde ahí sigue las altitudes de la carta. Pide la radio con
    /// <paramref name="clear"/>(avión, nivel o NaN = aproximación); la autorización vale al terminar la colación.
    /// </summary>
    /// <summary>Velocidad mínima que Approach asigna (kt): por debajo cada avión vuela su velocidad de aproximación. APROXIMADO (práctica OACI: 160 kt hasta ~4 NM).</summary>
    public const double MinAssignedSpeedKt = 160;

    /// <summary>Como mucho una instrucción de velocidad cada tanto por avión (s).</summary>
    public const double SpeedCallEveryS = 120;

    public static void UpdateClearances(List<AirborneAgent> air, Action<AirborneAgent, double> clear,
        Action<AirborneAgent, double>? speed = null, double now = 0)
    {
        for (var i = 0; i < air.Count; i++)
        {
            var a = air[i];
            // Velocidad (paso 6 de la prueba de Marco): Approach asigna la que pide la secuencia (redondeada a 10 kt) y la
            // libera ("resume normal speed") cuando ya no hace falta. En final ya no se cambia.
            if (speed is not null && a.ControlledArrival && a.Landing is not null && !a.ClearancePending
                && a.Phase is FlightPhase.Descent or FlightPhase.Approach or FlightPhase.Cruise)
            {
                // Sin ir y venir (prueba de Marco: "baje, mantenga, suba, baje"): solo se pide reducir (20 kt o más bajo lo
                // asignado), una vez cada 2 min por avión; la velocidad normal se devuelve una sola vez, con holgura.
                var want = a.SequenceSpeedKt > 0 ? Math.Floor(a.SequenceSpeedKt / 10) * 10 : 0;
                // Nunca menos que la mínima que el ATC asigna en la aproximación (ADR-117, prueba de Marco en RKPK: "reduzca a
                // 120 nudos" a un A330). Si la secuencia pide menos, se pide la mínima y el resto lo resuelve la espera.
                if (want > 0)
                    want = Math.Max(want, MinAssignedSpeedKt);
                var quiet = now - a.LastSpeedCallS >= SpeedCallEveryS;
                if (quiet && want > 0 && (a.AssignedSpeedKt == 0 || want <= a.AssignedSpeedKt - 20))
                {
                    a.ClearancePending = true;
                    a.LastSpeedCallS = now;
                    speed(a, want);
                    continue;
                }
                if (quiet && want == 0 && a.AssignedSpeedKt > 0 && a.Phase == FlightPhase.Descent)
                {
                    a.ClearancePending = true;
                    a.LastSpeedCallS = now;
                    speed(a, 0);
                    continue;
                }
            }
            if (!a.ControlledArrival || a.Landing is null || a.ApproachCleared || a.ClearancePending || double.IsNaN(a.ClearedAltitudeFt)
                || a.Phase is FlightPhase.Climb or FlightPhase.Holding or FlightPhase.GoAround or FlightPhase.Flare
                    or FlightPhase.Rollout or FlightPhase.Landed or FlightPhase.Finished)
                continue;
            if (a.RemainingNm <= ClearApproachNm && a.Path.ApproachWord is not null && a.Path.HasProfile)
            {
                a.ClearancePending = true;
                clear(a, double.NaN);
                continue;
            }
            var (profile, floor) = a.ProfileAhead(30);
            if (double.IsNaN(profile))
                continue;
            var level = Math.Max(Math.Floor(profile / 1000) * 1000, Math.Ceiling(floor / 1000) * 1000);
            if (level <= a.ClearedAltitudeFt - 1000)
            {
                a.ClearancePending = true;
                clear(a, level);
            }
        }
    }

    /// <summary>Segundos hasta el umbral de una llegada (para que la Torre no deje entrar a la pista).</summary>
    public static double SecondsToThreshold(AirborneAgent a) =>
        a.Phase is FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed ? 0 : a.RemainingM / Math.Max(1, a.SpeedKt * 0.514444);

    public static double Nm(double m) => m / UnitConversions.MetersPerNauticalMile;
}

/// <summary>Lo que pasa con una llegada y se dice por radio.</summary>
public enum ArrivalEvent
{
    ClearedToLand,
    GoAround,
    Holding,
    LeftHolding,
}

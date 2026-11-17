using SimATC.Core.Engine;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Atc;

/// <summary>
/// Ground (ADR-021): controla el movimiento en plataformas y calles. Autoriza pushbacks cuando no hay tráfico que lo
/// impida y resuelve los bloqueos que le informa la seguridad (ceder el paso o ruta nueva). La ruta inicial de cada
/// avión es hoy la más corta (A*); en la etapa 4 la asignará Ground con nombres de calle ("taxi via…") y lo dirá por
/// su frecuencia.
/// </summary>
public sealed class GroundControl(GroundCoordinator safety)
{
    /// <summary>Distancia mínima entre fines de pushback simultáneos (m).</summary>
    public const double PushEndSpacingM = 80;
    /// <summary>Tráfico cuya ruta pasa a menos de esto del fin del pushback lo impide (m).</summary>
    public const double PushTrafficClearM = 40;

    /// <summary>
    /// Un pushback con tráfico que viene se autoriza si ese tráfico está a más de esto (m, más su media envergadura) y llega
    /// cuando el pushback ya terminó de moverse. Aproximado.
    /// </summary>
    public const double PushMinTrafficDistanceM = 150;

    /// <summary>Si el rumbo del rodaje del que empuja y el del que viene difieren más que esto, quedarían de frente (°).</summary>
    public const double PushOpposingAngleDeg = 120;

    private readonly GroundCoordinator _safety = safety;

    /// <summary>Cada cuántos ticks se revisa un pedido de pushback pendiente (1 s: como un controlador, no cada cuadro).</summary>
    public const int PushReviewTicks = 10;

    private readonly Dictionary<uint, (bool Granted, HoldKind Hold, uint HoldFor)> _pushDecision = [];

    // Bloqueos ya anotados en el log (por los aviones del ciclo) y cuándo (en ticks de Ground, 10 por segundo).
    private readonly Dictionary<string, long> _reported = [];
    private const long ReportEveryTicks = 300;
    private long _tick;

    /// <summary>Un tick: autorizaciones de pushback y resolución de bloqueos confirmados.</summary>
    public void Update(IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        _tick++;
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.Phase != DeparturePhase.BeaconOn)
            {
                _pushDecision.Remove(a.ObjectId);
                continue;
            }
            // Una vez autorizado y dicho por radio no se le quita; los pendientes (o autorizados que la radio todavía no dijo,
            // hito 4e-1) se revisan cada segundo, escalonados por avión: si mientras tanto viene alguien de frente, se
            // retira antes de transmitirse.
            var said = (a.RadioClearances & ClearanceKind.Pushback) != 0; // sin HasFlag: en Debug hace boxing y asigna en el tick
            if (_pushDecision.TryGetValue(a.ObjectId, out var d) && ((d.Granted && said) || (_tick + a.ObjectId) % PushReviewTicks != 0))
            {
                a.SetClearance(ClearanceKind.Pushback, d.Granted);
                if (!d.Granted)
                {
                    a.Hold = d.Hold;
                    a.HoldFor = d.HoldFor;
                }
                continue;
            }
            var granted = PushClear(a, agents, user);
            a.SetClearance(ClearanceKind.Pushback, granted);
            _pushDecision[a.ObjectId] = (granted, a.Hold, a.HoldFor);
        }
    }

    /// <summary>Después de la seguridad: resuelve los bloqueos que detectó en este tick.</summary>
    public void ResolveDeadlocks(IReadOnlyList<DepartureAgent> agents)
    {
        var cycles = _safety.ConfirmedCycles;
        for (var i = 0; i < cycles.Count; i++) // sin foreach sobre la interfaz: no asigna en el tick
        {
            var cycle = cycles[i];
            if (Resolve(cycle, agents))
                _safety.DeadlocksResolved++;
        }
    }

    /// <summary>
    /// Resuelve un bloqueo que informó la seguridad: cede el de menor prioridad que pueda. Si tiene un cruce que otro del
    /// ciclo espera, lo devuelve y espera un rato ("give way"); si está de frente, se le da una ruta nueva (desvío).
    /// En la etapa 4 esto se transmite: "hold position, give way to…" o "taxi via … instead".
    /// </summary>
    private bool Resolve(DepartureAgent[] cycle, IReadOnlyList<DepartureAgent> agents)
    {
        var order = cycle.OrderByDescending(_safety.PriorityOf).ToArray();
        var names = string.Join(", ", cycle.Select(c => c.Callsign));
        foreach (var v in order)
        {
            var holdsContested = false;
            foreach (var m in cycle)
                holdsContested |= m.Hold == HoldKind.Intersection && m.HoldFor == v.ObjectId;
            if (holdsContested && _safety.GiveWay(v, GroundCoordinator.YieldS) > 0)
            {
                _safety.Report(Tr.F("{0}: bloqueo con {1}; Ground le indica ceder el paso.", v.Callsign, names));
                return true;
            }
            var other = agents.FirstOrDefault(o => o.ObjectId == v.HoldFor);
            var oldMarks = v.Marks;
            if (v.Hold is HoldKind.Traffic or HoldKind.Intersection && other is not null
                && v.TryReroute(other.Position, GroundCoordinator.AheadOfAgent(other), (v.WingspanM + other.WingspanM) / 2 + GroundCoordinator.RerouteClearanceM))
            {
                _safety.RouteChanged(v, oldMarks);
                _safety.Report(Tr.F("{0}: bloqueo de frente con {1}; Ground le da una ruta nueva vía {2}.", v.Callsign, other.Callsign, string.Join(", ", v.Taxiways)));
                return true;
            }
        }
        // El mismo bloqueo, una vez cada 30 s (ADR-091: en LEMD se anotó 596 veces y tapó el resto del log).
        var key = string.Join(",", cycle.Select(c => c.ObjectId).Order());
        if (!_reported.TryGetValue(key, out var at) || _tick - at >= ReportEveryTicks)
        {
            _reported[key] = _tick;
            _safety.Report(Tr.F("Bloqueo entre {0}: ninguno puede ceder todavía.", string.Join(", ", cycle.Select(c => Tr.F("{0} ({1}, {2}, recorrido {3:0}/{4:0} m)", c.Callsign, c.Phase, c.Hold, c.TaxiProgress, c.HoldAt)))));
        }
        return false;
    }

    /// <summary>Posición del avión y lo que le queda de rodaje por delante (400 m, cada 20 m): lo que un desvío evita.</summary>
    private static List<LocalPoint> AheadOf(DepartureAgent o)
    {
        var points = new List<LocalPoint> { o.Position };
        if (o.OnTaxiPath)
        {
            for (var s = o.TaxiProgress; s < Math.Min(o.TaxiPath.Length, o.TaxiProgress + 400); s += 20)
                points.Add(o.TaxiPath.PointAt(s));
        }
        return points;
    }


    /// <summary>
    /// true si <paramref name="o"/> llega al fin del pushback de <paramref name="a"/> recién cuando este terminó de moverse,
    /// con margen, y después los dos siguen en el mismo sentido (o se cruzan): entonces puede esperar detrás.
    /// <paramref name="s"/> es el punto de la ruta de o más cercano al fin del pushback. Si o todavía está en su pushback
    /// (o lo tiene autorizado), su llegada es lo que le falta de pushback más el rodaje hasta ese punto: antes se lo
    /// trataba como "justo ahí" y un avión esperaba a otro que empujaba a cientos de metros (prueba de Marco, 2026-10-31).
    /// </summary>
    internal static bool ArrivesAfterPush(DepartureAgent a, DepartureAgent o, double s)
    {
        var taxiing = o.Phase == DeparturePhase.Taxi;
        var distance = s - (taxiing ? o.TaxiProgress : 0);
        if (distance < PushMinTrafficDistanceM + o.WingspanM / 2)
            return false;
        // Tiene que llegar cuando el pushback ya terminó de moverse (a su velocidad máxima: la estimación más corta); después
        // se detiene detrás ("give way") y lo sigue. Antes se pedían además 20 s de margen y no se estimaba la llegada de
        // los que todavía empujaban: los aviones de una misma calle se esperaban en cadena varios minutos (prueba de
        // Marco, 2026-10-31). Headless scel-busy-morning: 0 pérdidas de separación, 85 despegues en 2 h (antes 79).
        var eta = (taxiing ? 0 : o.PushbackRemainingS) + distance / Math.Max(o.TaxiSpeedMps, 1);
        if (eta < a.PushbackRemainingS)
            return false;
        // De frente: el que empuja saldría contra el que viene por la misma calle.
        var mine = a.TaxiPath.HeadingAt(0);
        var theirs = o.TaxiPath.HeadingAt(s);
        return Math.Abs(GeoMath.AngleDiffDeg(mine, theirs)) < PushOpposingAngleDeg;
    }

    /// <summary>
    /// Mi rodaje pasa por donde <paramref name="o"/> termina su pushback: espero si quedaríamos de frente, o si yo llegaría
    /// a ese punto antes de que él haya terminado de empujar y se haya alejado una envergadura.
    /// </summary>
    internal static bool MustWaitForPushAhead(DepartureAgent a, DepartureAgent o)
    {
        var clear = PushTrafficClearM + o.WingspanM / 2;
        var mineS = a.TaxiPath.ProjectWithin(o.PushEnd, 0, a.TaxiPath.Length, out var lateral);
        if (lateral >= clear)
            return false;
        var mine = a.TaxiPath.HeadingAt(mineS);
        var theirs = o.TaxiPath.HeadingAt(0);
        // De frente: espero.
        if (Math.Abs(GeoMath.AngleDiffDeg(mine, theirs)) >= PushOpposingAngleDeg)
            return true;
        // En el mismo sentido: salgo si llego a ese punto cuando él ya terminó de empujar y se alejó una envergadura.
        var myArrival = a.PushbackRemainingS + mineS / Math.Max(a.TaxiSpeedMps, 1);
        var theyLeave = o.PushbackRemainingS + 2 * o.WingspanM / Math.Max(o.TaxiSpeedMps, 1);
        return myArrival < theyLeave;
    }

    /// <summary>Margen (m) entre donde puede detenerse el que viene y su punto de parada, para darle "give way".</summary>
    public const double GiveWayMarginM = 60;

    /// <summary>Los "give way" que se dan si el pushback termina autorizándose (en <see cref="PushClear"/>).</summary>
    private readonly List<(DepartureAgent Agent, double Stop)> _pendingGiveWay = [];

    /// <summary>
    /// Dónde detener a <paramref name="o"/> (m de avance) para que <paramref name="a"/> empuje delante, o null si no se puede:
    /// solo rodando, en el mismo sentido (de frente quedarían enfrentados), y con distancia para frenar más un margen.
    /// </summary>
    internal static double? GiveWayStop(DepartureAgent a, DepartureAgent o, double s, double clear)
    {
        if (o.Phase != DeparturePhase.Taxi)
            return null;
        if (Math.Abs(GeoMath.AngleDiffDeg(a.TaxiPath.HeadingAt(0), o.TaxiPath.HeadingAt(s))) >= PushOpposingAngleDeg)
            return null;
        var stop = s - clear - o.WingspanM / 2;
        var speedMps = o.SpeedKt * 0.514444;
        var brake = speedMps * speedMps / (2 * 0.3); // frenando suave, a 0,3 m/s² (el de atrás también tiene que frenar)
        return stop - o.TaxiProgress >= brake + GiveWayMarginM ? stop : null;
    }

    /// <summary>Hasta esta distancia detrás (m), un avión en la misma ruta impide darle "give way" al de adelante.</summary>
    public const double GiveWayFollowerM = 250;

    /// <summary>
    /// Si alguien rueda detrás de <paramref name="o"/> por su misma ruta: entonces no se le da "give way" (el de atrás
    /// tendría que frenar de golpe y se acercaría de más; en el Headless, una pérdida de separación).
    /// </summary>
    private static bool HasFollower(DepartureAgent o, IReadOnlyList<DepartureAgent> agents)
    {
        for (var i = 0; i < agents.Count; i++)
        {
            var b = agents[i];
            if (b == o || b.Phase is not (DeparturePhase.Taxi or DeparturePhase.PushbackComplete or DeparturePhase.Pushback))
                continue;
            var from = Math.Max(0, o.TaxiProgress - GiveWayFollowerM);
            var s = o.TaxiPath.ProjectWithin(b.Position, from, o.TaxiProgress, out var lateral);
            if (lateral < GroundCoordinator.PathLateralM && s < o.TaxiProgress)
                return true;
        }
        return false;
    }

    private bool PushClear(DepartureAgent a, IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        _pendingGiveWay.Clear();
        if (!PushClearCore(a, agents, user))
            return false;
        // Autorizado: los que vienen ceden el paso.
        for (var i = 0; i < _pendingGiveWay.Count; i++)
            _safety.GiveWay(_pendingGiveWay[i].Agent, _pendingGiveWay[i].Stop, a);
        return true;
    }

    /// <summary>Tramo final del rodaje de una llegada hacia su puesto que se compara con el recorrido de un pushback (m).</summary>
    public const double ArrivalNearStandM = 400;

    /// <summary>¿La llegada <paramref name="o"/> pasa, en lo que le queda, cerca de algún punto del recorrido del empuje de <paramref name="a"/>?</summary>
    private static bool ArrivalPassesPushback(DepartureAgent a, DepartureAgent o)
    {
        var push = a.PushbackPath;
        var clear = PushTrafficClearM + o.WingspanM / 2;
        for (var d = 0.0; d <= push.Length; d += 10)
        {
            var p = o.Projection.ToLocal(a.Projection.ToGeo(push.PointAt(d)));
            o.TaxiPath.ProjectWithin(p, o.TaxiProgress, o.TaxiPath.Length, out var lateral);
            if (lateral < clear)
                return true;
        }
        return false;
    }

    private bool PushClearCore(DepartureAgent a, IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        var end = a.PushEnd;
        // Si alguien que salió antes va a recorrer en sentido contrario parte de mi ruta, espero en el puesto (no en la
        // calle): Ground no autoriza el pushback hasta que pase.
        if (_safety.OncomingOnRoute(a, agents, out var first))
        {
            a.Hold = HoldKind.PushbackTraffic;
            a.HoldFor = first;
            return false;
        }
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (ReferenceEquals(o, a) || !o.OnGround)
                continue;
            // Otro pushback (en curso, recién terminado o ya autorizado antes) que usa el mismo lugar.
            var otherPushing = o.Phase is DeparturePhase.Pushback or DeparturePhase.PushbackComplete
                || (o.Phase == DeparturePhase.BeaconOn && o.IsCleared(ClearanceKind.Pushback));
            if (otherPushing && LocalPoint.Distance(o.PushEnd, end) < PushEndSpacingM)
            {
                a.Hold = HoldKind.PushbackTraffic;
                a.HoldFor = o.ObjectId;
                return false;
            }
            // Tráfico rodando, o que va a rodar (empujando o con el pushback autorizado), cuya ruta que le queda (hasta el
            // punto de espera) pasa por donde terminaría este pushback. Como un controlador real: si el que rueda está
            // lejos y llega cuando el pushback ya terminó de moverse, y no van a quedar de frente, se autoriza: el que
            // viene se detiene detrás (la seguridad en tierra lo frena) y después lo sigue. Si está cerca, o los dos
            // usarían la calle en sentidos contrarios, el pushback espera a que pase.
            if (o.Phase is DeparturePhase.Taxi or DeparturePhase.PushbackComplete || otherPushing)
            {
                var s = o.TaxiPath.ProjectWithin(end, o.TaxiProgress, o.TaxiPath.Length, out var lateral);
                var clear = PushTrafficClearM + o.WingspanM / 2;
                if ((lateral < clear || LocalPoint.Distance(o.Position, end) < clear) && !ArrivesAfterPush(a, o, s))
                {
                    // Si el que viene rueda en el mismo sentido y todavía puede detenerse con margen, Ground le dice que
                    // ceda el paso ("give way") y el pushback se autoriza sin esperar a que pase (hito 4e-2).
                    if (!HasFollower(o, agents) && GiveWayStop(a, o, s, clear) is { } stop)
                    {
                        _pendingGiveWay.Add((o, stop));
                        continue;
                    }
                    a.Hold = HoldKind.PushbackTraffic;
                    a.HoldFor = o.ObjectId;
                    return false;
                }
            }
            // Una llegada que entra a su puesto (últimos ArrivalNearStandM) y pasa cerca de cualquier punto del recorrido del
            // empuje, no solo del final: espera a que se estacione (ADR-096, Bruselas, Roma, Atatürk: empujaban al lado de
            // la que entraba al puesto vecino y pasaban a 20–45 m).
            if (o.Inbound && o.Phase == DeparturePhase.Taxi && o.TaxiPath.Length - o.TaxiProgress < ArrivalNearStandM
                && ArrivalPassesPushback(a, o))
            {
                a.Hold = HoldKind.PushbackTraffic;
                a.HoldFor = o.ObjectId;
                return false;
            }
            // Y al revés: si mi rodaje pasa por donde el otro está empujando (o va a empujar), espero a que salga si
            // quedaríamos de frente en la misma calle, o si yo llegaría ahí antes de que él se haya ido. Si vamos en el mismo
            // sentido y llego después, salgo y lo sigo (la seguridad en tierra frena al que alcanza a otro).
            if (otherPushing && MustWaitForPushAhead(a, o))
            {
                a.Hold = HoldKind.PushbackTraffic;
                a.HoldFor = o.ObjectId;
                return false;
            }
        }
        if (user is { } u)
        {
            var at = a.Projection.ToLocal(u.Position);
            var s = a.PushbackPath.Project(at);
            if (LocalPoint.Distance(a.PushbackPath.PointAt(s), at) < PushTrafficClearM + u.WingspanM / 2)
            {
                a.Hold = HoldKind.UserAircraft;
                return false;
            }
        }
        return true;
    }
}

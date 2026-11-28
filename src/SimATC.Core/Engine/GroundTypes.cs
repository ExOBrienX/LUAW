namespace SimATC.Core.Engine;

/// <summary>
/// Un cruce del rodaje: nodo del grafo, distancia sobre la ruta y los tramos del grafo (dirigidos, en el sentido en que
/// los recorre) hasta el cruce siguiente. Dos aviones que van en sentido contrario por la misma calle tienen los mismos
/// tramos al revés, aunque cada uno vea cruces distintos.
/// </summary>
public readonly record struct TaxiMark(int Node, double S, long[] Edges)
{
    /// <summary>Clave de un tramo dirigido a → b (la del sentido contrario es SegmentKey(b, a)).</summary>
    public static long SegmentKey(int from, int to) => ((long)from << 32) | (uint)to;

    public static long Reverse(long segment) => ((long)(uint)segment << 32) | (uint)(segment >> 32);
}

/// <summary>Por qué un avión en tierra está esperando (RF-18).</summary>
public enum HoldKind
{
    None,
    /// <summary>El cruce siguiente o su tramo está reservado por otro.</summary>
    Intersection,
    /// <summary>Otro avión adelante en la ruta: mantiene la distancia.</summary>
    Traffic,
    /// <summary>El avión del piloto está en la ruta.</summary>
    UserAircraft,
    /// <summary>No empieza el pushback: hay tráfico detrás o cerca del lugar donde termina.</summary>
    PushbackTraffic,
    /// <summary>Pista ocupada.</summary>
    Runway,
    /// <summary>Separación con la salida anterior (distancia por la pista).</summary>
    DepartureSeparation,
    /// <summary>Separación por estela detrás de un pesado.</summary>
    Wake,
}

/// <summary>Por qué espera la pista (<see cref="HoldKind.Runway"/>), para la tarjeta del avión.</summary>
public enum RunwayHold
{
    None,
    /// <summary>Otro avión en la pista, cruzándola o autorizado a alinearse.</summary>
    Occupied,
    /// <summary>Una llegada a esa pista dentro de poco.</summary>
    Arrival,
    /// <summary>El avión del piloto está en la pista.</summary>
    UserOnRunway,
    /// <summary>El piloto tiene la pista (autorizado) o llegó antes a la fila.</summary>
    PilotTurn,
    /// <summary>Le queda poca pista (después de abortar).</summary>
    ShortRunway,
    /// <summary>Espera para cruzar una pista.</summary>
    Crossing,
}

/// <summary>Aviso de llegada (ADR-094): tramos dirigidos de su rodaje previsto y los cruces del comienzo (al salir de la pista).</summary>
public sealed record ArrivalRoute(long[] Keys, int[] ExitNodes);

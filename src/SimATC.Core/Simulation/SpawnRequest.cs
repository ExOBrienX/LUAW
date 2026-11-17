using SimATC.Core.Geo;

namespace SimATC.Core.Simulation;

/// <summary>
/// Pedido para crear un avión estacionado: en tierra, en la posición y rumbo del puesto, con motores apagados y
/// luces de estacionamiento (V-09).
/// </summary>
/// <param name="Title">Título del contenedor (modelo + livery).</param>
/// <param name="TailNumber">Matrícula o identificador (máx. 12 caracteres).</param>
/// <param name="ElevationFt">Elevación del terreno; con OnGround el simulador lo apoya en el suelo.</param>
/// <param name="EngineCount">Motores a apagar (1-4); 0 = no se sabe, se apagan los 4 índices.</param>
public sealed record ParkedSpawnRequest(
    string Title,
    string TailNumber,
    GeoPoint Position,
    double ElevationFt,
    double HeadingTrueDeg,
    int EngineCount);

/// <summary>
/// Pedido para crear un avión en vuelo (etapa 6, §5.3): un vuelo virtual que se acerca al piloto aparece en su posición,
/// altura, rumbo y velocidad, con motores en marcha. Sin ATC del simulador.
/// </summary>
public sealed record AirborneSpawnRequest(
    string Title,
    string TailNumber,
    GeoPoint Position,
    double AltitudeFt,
    double HeadingTrueDeg,
    double AirspeedKt,
    int EngineCount);

/// <summary>
/// Pedido para crear un avión estacionado "Parked ATC" (V-15): el simulador le elige y reserva un puesto en
/// <paramref name="AirportIcao"/> y le conecta la pasarela. Sin plan de vuelo: el ATC nativo no lo maneja.
/// </summary>
public sealed record ParkedAtcRequest(string Title, string TailNumber, string AirportIcao, int EngineCount);

/// <summary>Avión Parked ATC creado y dónde lo dejó el simulador.</summary>
public sealed record ParkedAtcResult(uint ObjectId, GeoPoint Position, double HeadingTrueDeg);

/// <summary>Remolcador de pushback enganchado a la rueda de nariz de un avión (en tierra).</summary>
public readonly record struct TugPose(GeoPoint Position, double HeadingTrueDeg, double AltitudeFt);

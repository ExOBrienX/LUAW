namespace SimATC.Core.Simulation;

/// <summary>
/// Carga e identidad del avión del piloto, para compararlas con el OFP antes de salir. SimVars verificadas en el SDK
/// 1.8.1 (ADR-026): <c>FUEL TOTAL QUANTITY WEIGHT</c>, <c>TOTAL WEIGHT</c>, <c>EMPTY WEIGHT</c>, <c>MAX GROSS WEIGHT</c>
/// (libras), y <c>ATC ID</c>, <c>ATC MODEL</c>, <c>ATC TYPE</c>, <c>TITLE</c> (texto). Los pesos quedan en libras,
/// como llegan; se convierten al mostrar.
/// </summary>
public readonly record struct UserAircraftLoad(
    double FuelLbs,
    double TotalWeightLbs,
    double EmptyWeightLbs,
    double MaxGrossWeightLbs,
    string AtcId,
    string AtcModel,
    string AtcType,
    string Title);

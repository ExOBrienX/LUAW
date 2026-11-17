using SimATC.App.ViewModels;
using SimATC.Core;
using SimATC.Core.Airports;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.App.Views;

/// <summary>
/// Modo demo del planificador de pushback (RF-21c), para probar la ventana y el mouse sin el simulador:
/// <c>dotnet run --project src/SimATC.App -- --pushback-demo SCEL</c>. Usa el aeropuerto que ya está en la caché y pone
/// el avión en la primera puerta mediana; "Iniciar" no mueve nada (no hay simulador).
/// </summary>
public static class PushbackDemo
{
    public static PushbackWindow? TryCreate(string[]? args)
    {
        var i = args is null ? -1 : Array.IndexOf(args, "--pushback-demo");
        if (i < 0)
            return null;
        var icao = i + 1 < args!.Length ? args[i + 1] : "SCEL";
        var file = Path.Combine(AppPaths.AirportCache, $"{icao.ToUpperInvariant()}.json");
        if (!File.Exists(file) || AirportCache.Deserialize(File.ReadAllText(file))?.Airport is not { } airport)
            return null;
        var graph = TaxiGraph.Build(airport);
        var stand = airport.Parkings.FirstOrDefault(p => p.Type == ParkingType.GateMedium) ?? airport.Parkings[0];
        var vm = PushbackViewModel.ForStand(graph, airport, stand.Position, stand.HeadingTrueDeg);
        vm.StartRequested += _ => vm.CrewStatus = Tr.T("Modo demo: sin simulador no se mueve nada.");
        return new PushbackWindow { DataContext = vm, Title = Tr.F("LUAW · pushback (demo {0})", airport.Icao) };
    }
}

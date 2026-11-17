using SimATC.Sim;

/// <summary>
/// Lista los títulos de aviones que el simulador tiene instalados (EnumerateSimObjectsAndLiveries, tipo AIRCRAFT) y los
/// guarda en un archivo, para elegir la decoración de aviación general con nombres reales (ADR-109).
/// Uso: <c>dotnet run --project tools/SimATC.Probe -- --titles [archivo]</c>, con MSFS abierto.
/// </summary>
internal static class TitlesCommand
{
    public static async Task<int> RunAsync(string? outPath)
    {
        using var bridge = new SimConnectBridge(_ => { });
        bridge.Start();
        for (var i = 0; i < 40 && !bridge.IsConnected; i++)
            await Task.Delay(250);
        if (!bridge.IsConnected)
        {
            Console.WriteLine("Sin conexión con MSFS en 10 s.");
            return 1;
        }
        var titles = await bridge.EnumerateAircraftTitlesAsync(CancellationToken.None);
        var sorted = titles.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"{sorted.Count} títulos.");
        if (outPath is not null)
        {
            File.WriteAllLines(outPath, sorted);
            Console.WriteLine($"Guardados en {outPath}.");
        }
        return 0;
    }
}

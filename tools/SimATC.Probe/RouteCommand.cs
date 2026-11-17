using System.Diagnostics;
using SimATC.Core.Engine;
using SimATC.Core.Flight;
using SimATC.Core.World;
using SimATC.Sim;

/// <summary>
/// <c>--route ORIGEN DESTINO [pista de salida]</c>: arma el plan completo como lo hace el director del tráfico (ADR-062):
/// ruta de VATSIM o por aerovías del simulador, SID, STAR y aproximación para cada pista de llegada, e imprime los fijos.
/// Con MSFS abierto. Usa el índice de fijos guardado (o lo pide al simulador la primera vez).
/// </summary>
internal static class RouteCommand
{
    private const double Nm = 1852;

    public static async Task<int> RunAsync(string from, string to, string? runway)
    {
        using var bridge = new SimConnectBridge(_ => { });
        bridge.Start();
        for (var i = 0; i < 40 && !bridge.IsConnected; i++)
            await Task.Delay(250);
        if (!bridge.IsConnected)
        {
            Console.WriteLine("Sin conexión con MSFS.");
            return 1;
        }
        var sw = Stopwatch.StartNew();
        var navPath = NavIndex.PathFor(SimATC.Core.AppPaths.Root);
        var nav = NavIndex.Load(navPath);
        if (nav is null)
        {
            nav = new NavIndex(await bridge.RequestAllNavaidsAsync(CancellationToken.None));
            nav.Save(navPath);
        }
        Console.WriteLine($"Índice de fijos: {nav.Count} ({sw.Elapsed.TotalSeconds:0.0} s)");

        var origin = await bridge.LoadAirportAsync(from, CancellationToken.None);
        var dest = await bridge.LoadAirportAsync(to, CancellationToken.None);
        var db = new AirwayDatabase(bridge.RequestWaypointAsync) { TypedFetch = bridge.RequestWaypointAsync };
        Console.WriteLine($"Aerovías guardadas: {db.Load(AirwayDatabase.PathFor(SimATC.Core.AppPaths.Root))} fijos");
        db.Nav = nav;
        Console.WriteLine($"Fijos sin posición corregidos: {db.RepairPositions()}");
        var depRunway = runway ?? origin.Runways[0].Primary.Ident;

        var store = VatsimRouteStore.Load(VatsimRouteStore.PathFor(SimATC.Core.AppPaths.Root));
        await store.RefreshAsync(Console.WriteLine, CancellationToken.None);
        store.Save(VatsimRouteStore.PathFor(SimATC.Core.AppPaths.Root));
        var routes = store.RoutesFor(from, to);
        Console.WriteLine($"VATSIM {from}-{to}: {routes.Count} rutas guardadas{(routes.Count > 0 ? $"; la más vista: {routes[0]}" : "")}");

        ResolvedRoute? route = null;
        foreach (var r in dest.Runways)
        {
            foreach (var end in new[] { r.Primary.Ident, r.Secondary.Ident })
            {
                if (FlightPlanner.LandingFor(dest, null, end, PerformanceCategory.MediumJet) is not { } landing)
                    continue;
                var ends = new FlightEnds(from, origin.Reference, origin.ElevationFt, origin, depRunway, to, dest.Reference, dest.ElevationFt, dest, landing);
                // Una ruta por pista de llegada (como el director): termina donde empieza su STAR.
                route = null;
                {
                    sw.Restart();
                    route = await FlightPlanBuilder.ResolveRouteAsync(ends, db, nav, Environment.GetCommandLineArgs().Contains("--no-vatsim") ? null : store,
                        Console.WriteLine, CancellationToken.None);
                    Console.WriteLine($"Ruta ({route.Source}, {sw.Elapsed.TotalSeconds:0.0} s, {db.Count} fijos pedidos): {route.Text}");
                    Console.WriteLine($"  fijos: {string.Join(" ", route.Fixes.Select(f => f.Airway.Length > 0 ? $"{f.Ident}({f.Airway})" : f.Ident))}");
                }
                var plan = FlightPlanBuilder.Assemble(ends, route, PerformanceCategory.MediumJet);
                var named = new List<string>();
                for (var i = 0; i < plan.Path.Count; i++)
                {
                    if (plan.Path[i].IsFix)
                        named.Add(plan.Path[i].Name);
                }
                Console.WriteLine($"{from} {depRunway} → {to} {end}: SID {plan.Sid ?? "—"} · STAR {plan.Star ?? "—"} · {plan.Approach ?? "final recta"} · " +
                                  $"FL{plan.CruiseFt / 100:000} · {plan.Path.LengthM / Nm:0} NM");
                Console.WriteLine($"  {string.Join(" ", named)}");
                foreach (var why in plan.Reasons)
                    Console.WriteLine($"  · {why}");
                foreach (var i in plan.Issues)
                    Console.WriteLine($"  ⚠ {i}");
            }
        }
        return 0;
    }
}

using System.Diagnostics;
using System.Globalization;
using SimATC.Core;
using SimATC.Core.Airports;
using SimATC.Sim;

/// <summary>
/// <c>SimATC.Probe --airport SCEL [--refresh]</c>: carga un aeropuerto (caché o simulador), imprime un resumen y deja
/// el JSON en la caché.
/// </summary>
internal static class AirportCommand
{
    public static async Task<int> RunAsync(string icao, bool refresh)
    {
        var inv = CultureInfo.InvariantCulture;
        using var connected = new ManualResetEventSlim(false);
        using var bridge = new SimConnectBridge(m => Console.WriteLine($"  [sim] {m}"));
        bridge.ConnectionChanged += c => { if (c) connected.Set(); };
        bridge.Start();

        Console.WriteLine("Esperando conexión con MSFS (máx. 30 s)...");
        if (!connected.Wait(TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine("No se pudo conectar con MSFS.");
            return 1;
        }

        var cache = new AirportCache(AppPaths.AirportCache);
        var provider = new AirportProvider(bridge, cache);
        var sw = Stopwatch.StartNew();
        AirportData a;
        bool fromCache;
        try
        {
            (a, fromCache) = await provider.GetAsync(icao, refresh, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"{a.Icao} — {a.Name}  ({(fromCache ? "desde caché" : "desde el simulador")}, {sw.ElapsedMilliseconds} ms)");
        Console.WriteLine($"Referencia {a.Reference.LatDeg.ToString("0.000000", inv)}, {a.Reference.LonDeg.ToString("0.000000", inv)}" +
                          $"  elevación {a.ElevationFt.ToString("0", inv)} ft  MAGVAR crudo {a.MagVarRawDeg.ToString("0.#", inv)}" +
                          $"  TA {a.TransitionAltitudeFt.ToString("0", inv)} ft  TL {a.TransitionLevelFt.ToString("0", inv)} ft");
        Console.WriteLine($"Caché: {cache.PathFor(a.Icao)}");

        Console.WriteLine();
        Console.WriteLine("Pistas:");
        foreach (var r in a.Runways)
            Console.WriteLine($"  {r.Name,-8} rumbo {r.HeadingTrueDeg.ToString("0.00", inv)}°V  {r.LengthM.ToString("0", inv)} x {r.WidthM.ToString("0", inv)} m" +
                              $"  {r.Primary.Ident} en {Fmt(r.Primary.Position)}  {r.Secondary.Ident} en {Fmt(r.Secondary.Position)}");

        Console.WriteLine("Frecuencias:");
        foreach (var f in a.Frequencies.OrderBy(f => f.Type))
            Console.WriteLine($"  {f.Type,-10} {(f.Khz / 1000.0).ToString("0.000", inv)}  {f.Name}");

        var holds = a.TaxiPoints.Count(p => p.IsHoldShort);
        Console.WriteLine($"Rodaje: {a.TaxiPoints.Count} puntos ({holds} de espera), {a.TaxiPaths.Count} tramos, " +
                          $"{a.TaxiNames.Count(n => n.Length > 0)} calles: {string.Join(" ", a.TaxiNames.Where(n => n.Length > 0))}");
        Console.WriteLine("  Tramos por tipo: " + string.Join(", ", a.TaxiPaths.GroupBy(p => p.Type).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine($"Puestos: {a.Parkings.Count} (" + string.Join(", ", a.Parkings.GroupBy(p => p.Type).Select(g => $"{g.Key} {g.Count()}")) + ")");
        var withAirlines = a.Parkings.Where(p => p.Airlines.Count > 0).ToList();
        if (withAirlines.Count > 0)
            Console.WriteLine($"  Con aerolínea: {withAirlines.Count} (" + string.Join(" ", withAirlines.SelectMany(p => p.Airlines).Distinct()) + ")");

        int Legs(Procedure p) => p.Legs.Count + p.RunwayTransitions.Sum(t => t.Legs.Count) + p.EnrouteTransitions.Sum(t => t.Legs.Count);
        Console.WriteLine($"SID ({a.Departures.Count}, {a.Departures.Sum(Legs)} tramos): " + string.Join(" ", a.Departures.Select(p => p.Name)));
        Console.WriteLine($"STAR ({a.Arrivals.Count}, {a.Arrivals.Sum(Legs)} tramos): " + string.Join(" ", a.Arrivals.Select(p => p.Name)));
        Console.WriteLine($"Aproximaciones ({a.Approaches.Count}): " + string.Join(" ",
            a.Approaches.Select(x => $"{x.Type}{(x.Suffix.Length > 0 ? " " + x.Suffix : "")} {RunwayEnd.FormatIdent(x.RunwayNumber, x.RunwayDesignator)}")));

        // Muestra de un procedimiento para revisar a ojo contra la carta.
        if (a.Departures.FirstOrDefault(p => p.RunwayTransitions.Count > 0) is { } sid)
        {
            var t = sid.RunwayTransitions[0];
            Console.WriteLine();
            Console.WriteLine($"Muestra: SID {sid.Name}, pista {RunwayEnd.FormatIdent(t.RunwayNumber, t.RunwayDesignator)}:");
            foreach (var l in t.Legs.Concat(sid.Legs))
                Console.WriteLine($"  {l.Type} {l.FixIcao,-6}" +
                                  (HasCourse(l.Type) ? $" curso {l.CourseDeg.ToString("000", inv)}{(l.CourseIsTrue ? "V" : "M")}" : "") +
                                  $"  alt {l.AltitudeConstraint} {l.Altitude1Ft.ToString("0", inv)}" +
                                  (l.AltitudeConstraint == AltitudeConstraint.Between ? $"-{l.Altitude2Ft.ToString("0", inv)}" : "") +
                                  (l.SpeedConstraint != SpeedConstraint.None ? $"  vel {l.SpeedConstraint} {l.SpeedLimitKt.ToString("0", inv)}" : ""));
        }
        Console.WriteLine();
        Console.WriteLine("Para ver tu avión en vivo, ejecuta el Probe sin argumentos.");
        return 0;

        // Solo los tramos de curso o rumbo usan el campo COURSE (ARINC 424); en DF, TF, IF, RF… no aplica.
        static bool HasCourse(LegType t) => t is LegType.CA or LegType.CD or LegType.CF or LegType.CI or LegType.CR
            or LegType.FA or LegType.FC or LegType.FD or LegType.FM or LegType.VA or LegType.VD or LegType.VI
            or LegType.VM or LegType.VR;

        string Fmt(SimATC.Core.Geo.GeoPoint p) => $"{p.LatDeg.ToString("0.00000", inv)},{p.LonDeg.ToString("0.00000", inv)}";
    }
}

using System.Net;
using System.Net.WebSockets;
using System.Text;

/// <summary>
/// Prueba V-40 (ADR-073): servidor local en http://127.0.0.1:47110 para la app "LUAW Radar" del EFB.
/// GET /ping responde texto; /traffic acepta WebSocket y manda un mensaje JSON por segundo. Ctrl+C para salir.
/// Registra cada conexión, para saber si el EFB llegó (y con qué Origin).
/// </summary>
internal static class EfbTestCommand
{
    public const int Port = 47110;

    public static async Task<int> RunAsync()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Prefixes.Add($"http://localhost:{Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"No se pudo abrir el puerto {Port}: {ex.Message}");
            return 1;
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            listener.Stop();
        };
        Console.WriteLine($"Servidor de prueba del EFB en http://127.0.0.1:{Port} (/ping y WebSocket /traffic). Ctrl+C para salir.");
        Console.WriteLine("Abre la app \"RadarApp\" en el EFB del simulador y mira si aparecen conexiones aquí.");
        while (!cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                break;
            }
            var origin = ctx.Request.Headers["Origin"] ?? "(sin Origin)";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath} desde {ctx.Request.RemoteEndPoint} · Origin {origin} · WebSocket {ctx.Request.IsWebSocketRequest}");
            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/traffic")
                _ = ServeSocketAsync(ctx, cts.Token);
            else
            {
                var body = Encoding.UTF8.GetBytes($"pong LUAW {DateTime.Now:HH:mm:ss}");
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(body, cts.Token);
                ctx.Response.Close();
            }
        }
        return 0;
    }

    private static async Task ServeSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            using var ws = wsCtx.WebSocket;
            Console.WriteLine("  WebSocket ABIERTO con el EFB.");
            var feed = new SimATC.Core.Efb.EfbRadarFeed();
            var scel = new SimATC.Core.Geo.GeoPoint(-33.393, -70.786);
            for (var n = 1; ws.State == WebSocketState.Open && !ct.IsCancellationRequested; n++)
            {
                // TEST DATA: el piloto en SCEL y tres aviones inventados que se mueven (formato real del radar, ADR-073).
                var me = new SimATC.Core.Efb.EfbOwnship(scel, 170, 1600, 0, true);
                SimATC.Core.Efb.EfbTarget At(uint id, string cs, double brg, double nm, double hdg, double alt, double gs, bool gnd) =>
                    new(id, cs, "A20N", SimATC.Core.Geo.GeoMath.Destination(scel, brg, nm * 1852), hdg, alt, gs, gnd, false, "SCEL", "SCIE");
                var traffic = new[]
                {
                    At(1, "LAN205", 200, 3 + n * 0.05, 200, 3000 + n * 40, 220, false),
                    At(2, "SKU101", 20, 25 - n * 0.06, 200, 9000 - n * 20, 250, false),
                    At(3, "JAT123", 160, 0.8, 170, 1600, 15, true),
                };
                var json = feed.Build(me, traffic, n);
                await ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
                await Task.Delay(1000, ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpListenerException)
        {
            Console.WriteLine($"  WebSocket cerrado ({ex.GetType().Name}).");
        }
    }
}

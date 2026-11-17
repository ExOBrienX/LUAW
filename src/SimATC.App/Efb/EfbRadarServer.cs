using System.Net;
using System.Net.WebSockets;

using SimATC.Core.Text;

namespace SimATC.App.Efb;

/// <summary>
/// Servidor local para la app "LUAW Radar" del EFB (ADR-073, V-40): WebSocket en ws://127.0.0.1:47110/traffic.
/// La UI publica el último mensaje (<see cref="Publish"/>) y cada cliente recibe el más reciente cada
/// <see cref="SendIntervalMs"/> ms. Solo escucha en este PC (127.0.0.1). Si el puerto está ocupado, se avisa y no hay radar.
/// </summary>
public sealed class EfbRadarServer : IDisposable
{
    public const int Port = 47110;
    public const int SendIntervalMs = 500;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _log;
    private byte[] _latest = [];
    private int _version;
    private int _clients;

    public EfbRadarServer(Action<string> log) => _log = log;

    /// <summary>Clientes conectados ahora (para el panel Diagnóstico).</summary>
    public int Clients => Volatile.Read(ref _clients);

    public bool Running { get; private set; }

    public void Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            Running = true;
            _ = Task.Run(AcceptLoopAsync);
            _log(Tr.F("Radar del EFB: escuchando en ws://127.0.0.1:{0}/traffic.", Port));
        }
        catch (HttpListenerException ex)
        {
            _log(Tr.F("Radar del EFB sin servidor: el puerto {0} está ocupado o bloqueado ({1}).", Port, ex.Message));
        }
    }

    /// <summary>El mensaje más reciente (se copia: el que llega puede ser un búfer reutilizado).</summary>
    public void Publish(ReadOnlyMemory<byte> json)
    {
        if (!Running || Clients == 0)
            return;
        Volatile.Write(ref _latest, json.ToArray());
        Interlocked.Increment(ref _version);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/traffic")
                _ = Task.Run(() => ServeAsync(ctx));
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref _clients);
        _log(Tr.T("Radar del EFB: tablet conectada."));
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            using var ws = wsCtx.WebSocket;
            var sent = -1;
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var version = Volatile.Read(ref _version);
                if (version != sent && Volatile.Read(ref _latest) is { Length: > 0 } msg)
                {
                    await ws.SendAsync(msg, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                    sent = version;
                }
                await Task.Delay(SendIntervalMs, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            // La tablet se cerró o cambió de app: normal.
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            _log(Tr.T("Radar del EFB: tablet desconectada."));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _cts.Dispose();
    }
}

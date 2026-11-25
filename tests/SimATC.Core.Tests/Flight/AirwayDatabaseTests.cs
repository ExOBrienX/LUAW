using SimATC.Core.Geo;
using SimATC.Core.World;

namespace SimATC.Core.Tests.Flight;

/// <summary>
/// La caché de aerovías (ADR-165, KMIA 2026-11-25): una ruta que agotaba su tiempo cancelaba sus consultas, la consulta
/// cancelada quedaba guardada y toda ruta por ese fijo fallaba al instante hasta cerrar la app. TEST DATA: fijos inventados.
/// </summary>
public class AirwayDatabaseTests
{
    private static readonly AirwayFix Fix = new("AAAAA", "K7", new GeoPoint(25, -80), []);

    [Fact]
    public async Task A_cancelled_caller_does_not_spoil_the_fix_for_the_next_one()
    {
        var answer = new TaskCompletionSource<AirwayFix?>();
        var db = new AirwayDatabase(async (_, _, ct) => await answer.Task.WaitAsync(ct));
        using var budget = new CancellationTokenSource();
        var first = db.GetAsync("AAAAA", "K7", budget.Token);
        budget.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        answer.SetResult(Fix);
        Assert.Equal(Fix, await db.GetAsync("AAAAA", "K7", CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_request_is_asked_again()
    {
        var calls = 0;
        var db = new AirwayDatabase((_, _, _) => ++calls == 1
            ? Task.FromException<AirwayFix?>(new InvalidOperationException("sin conexión"))
            : Task.FromResult<AirwayFix?>(Fix));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.GetAsync("AAAAA", "K7", CancellationToken.None));
        Assert.Equal(Fix, await db.GetAsync("AAAAA", "K7", CancellationToken.None));
    }

    /// <summary>Con el simulador ocupado una consulta puede vencer: ese fijo no queda "inexistente" para siempre.</summary>
    [Fact]
    public async Task A_fix_that_did_not_arrive_is_asked_again_later()
    {
        var calls = 0;
        Task<AirwayFix?> Fetch(string i, string r, CancellationToken ct) => Task.FromResult(++calls == 1 ? null : Fix);
        var db = new AirwayDatabase(Fetch) { MissRetry = TimeSpan.Zero };
        Assert.Null(await db.GetAsync("AAAAA", "K7", CancellationToken.None));
        Assert.Equal(Fix, await db.GetAsync("AAAAA", "K7", CancellationToken.None));
        // Antes de ese tiempo no se insiste.
        calls = 0;
        var patient = new AirwayDatabase(Fetch);
        Assert.Null(await patient.GetAsync("AAAAA", "K7", CancellationToken.None));
        Assert.Null(await patient.GetAsync("AAAAA", "K7", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Routes_waiting_are_counted_so_the_background_load_gives_way()
    {
        var answer = new TaskCompletionSource<AirwayFix?>();
        var db = new AirwayDatabase((_, _, _) => answer.Task);
        using var budget = new CancellationTokenSource();
        _ = db.GetAsync("BBBBB", "K7", CancellationToken.None); // carga de fondo: no cuenta
        Assert.Equal(0, db.Waiting);
        var route = db.GetAsync("AAAAA", "K7", budget.Token);
        Assert.Equal(1, db.Waiting);
        answer.SetResult(Fix);
        await route;
        Assert.Equal(0, db.Waiting);
    }
}

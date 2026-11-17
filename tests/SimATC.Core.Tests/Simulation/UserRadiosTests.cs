using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Simulation;

/// <summary>Las radios COM del piloto: por cuál transmite y qué escucha.</summary>
public class UserRadiosTests
{
    [Fact]
    public void Transmits_on_the_marked_radio_and_hears_the_receiving_ones()
    {
        var radios = new UserRadios(
        [
            new ComRadio(1, true, 122200, true, false),
            new ComRadio(2, true, 132700, true, true),   // transmite por COM2
            new ComRadio(3, false, 0, false, false),     // no existe
        ]);
        Assert.Equal(2, radios.Transmitting!.Value.Index);
        Assert.Equal(1, radios.Hearing(122200)!.Value.Index);
        Assert.Equal(2, radios.Hearing(132701)!.Value.Index);
        Assert.Null(radios.Hearing(118100));
    }

    /// <summary>Sin energía (COM STATUS 2): no se transmite ni se escucha nada, y el ATC no funciona (prueba de Marco, 2026-10-31).</summary>
    [Fact]
    public void Without_power_nothing_is_heard_or_transmitted()
    {
        var off = new UserRadios([new ComRadio(1, true, 122200, true, true, Powered: false), new ComRadio(2, true, 132700, true, false, Powered: false)]);
        Assert.False(off.HasPower);
        Assert.Null(off.Transmitting);
        Assert.Empty(off.Listening);
        var on = new UserRadios([new ComRadio(1, true, 122200, true, true)]);
        Assert.True(on.HasPower);
    }

    [Fact]
    public void Without_a_transmit_mark_it_is_com1_and_com1_is_always_heard()
    {
        var radios = new UserRadios([new ComRadio(1, true, 122200, false, false), new ComRadio(2, true, 132700, false, false)]);
        Assert.Equal(1, radios.Transmitting!.Value.Index);
        Assert.Equal(1, radios.Hearing(122200)!.Value.Index); // la de transmitir siempre se escucha
        Assert.Null(radios.Hearing(132700));                   // COM2 sin recibir
    }
}

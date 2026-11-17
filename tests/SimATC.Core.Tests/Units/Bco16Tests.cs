using SimATC.Core.Units;

namespace SimATC.Core.Tests.Units;

public class Bco16Tests
{
    [Theory]
    [InlineData(0x4721u, 4721)]
    [InlineData(0x7000u, 7000)]
    [InlineData(0x0000u, 0)]
    [InlineData(0x7777u, 7777)]
    [InlineData(0x1200u, 1200)]
    public void TryDecode_ValidCode_ReturnsDigits(uint raw, int expected)
    {
        Assert.True(Bco16.TryDecode(raw, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(0x4781u)]   // nibble 8: no es octal
    [InlineData(0x000Fu)]
    [InlineData(0x10000u)]  // más de 4 dígitos
    public void TryDecode_InvalidValue_ReturnsFalse(uint raw)
    {
        Assert.False(Bco16.TryDecode(raw, out _));
    }

    [Theory]
    [InlineData(4721, 0x4721u)]
    [InlineData(2042, 0x2042u)]
    [InlineData(7, 0x0007u)]
    public void Encode_is_the_inverse_of_decode(int code, uint raw)
    {
        Assert.Equal(raw, Bco16.Encode(code));
        Assert.True(Bco16.TryDecode(Bco16.Encode(code), out var back));
        Assert.Equal(code, back);
    }

    [Fact]
    public void Encode_rejects_non_octal_digits() => Assert.Throws<ArgumentOutOfRangeException>(() => Bco16.Encode(4781));
}

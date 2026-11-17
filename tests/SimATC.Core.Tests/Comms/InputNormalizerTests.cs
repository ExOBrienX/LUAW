using SimATC.Core.Comms;

namespace SimATC.Core.Tests.Comms;

/// <summary>Paso 1 de §8.8: normalizar lo que escribe o dice el piloto.</summary>
public class InputNormalizerTests
{
    [Theory]
    [InlineData("Squawk fower seven two one", "squawk 4721")]
    [InlineData("climb flight level two fower zero", "climb flight level 240")]
    [InlineData("contact tower one one eight decimal one", "contact tower 118.1")]
    [InlineData("Contact Tower 118.1, LAN123", "contact tower 118.1 lan123")]
    [InlineData("climb five thousand", "climb 5000")]
    [InlineData("one one thousand five hundred", "11500")]
    [InlineData("niner thousand", "9000")]
    [InlineData("runway one seven left", "runway 17l")]
    [InlineData("runway 17 right", "runway 17r")]
    [InlineData("information alfa", "information a")]
    [InlineData("Eh, request taxi please", "request taxi")]
    [InlineData("requst taxi", "request taxi")]           // tipeo: falta una letra
    [InlineData("cleard to SCIE", "cleared to scie")]       // tipeo
    [InlineData("holdng short runway 17R", "holding short runway 17r")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, InputNormalizer.Normalize(input));

    [Fact]
    public void Short_words_and_callsigns_are_not_corrected()
    {
        Assert.Equal("lan205 gelu5b", InputNormalizer.Normalize("LAN205 GELU5B"));
        Assert.Equal("ready", InputNormalizer.Normalize("ready"));
    }

    [Theory]
    [InlineData("taxi", "taxi", true)]
    [InlineData("runway", "runwy", true)]
    [InlineData("runway", "rumway", true)]
    [InlineData("runway", "rnwy", false)]
    public void Edit_distance(string a, string b, bool expected) =>
        Assert.Equal(expected, InputNormalizer.EditDistanceAtMostOne(a, b));
}

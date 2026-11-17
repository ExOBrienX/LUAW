using SimATC.Core.Fleet;

namespace SimATC.Core.Tests.Fleet;

public class ModelMatcherTests
{
    // TEST DATA: catálogo y reglas inventados.
    private static AircraftModel M(string title, string type, string airline, WakeCategory wake = WakeCategory.Medium) =>
        new(title, ModelSource.Fsltl, type, airline, wake, 2, 34, ["GATE"], []);

    private static readonly AircraftModel XyzA320A = M("X_A320_XYZ_1", "A320", "XYZ");
    private static readonly AircraftModel XyzA320B = M("X_A320_XYZ_2", "A320", "XYZ");
    private static readonly AircraftModel RuleOnly = M("X_A20N_XYZ_RULE", "A20N", "XYZ");
    private static readonly AircraftModel GenA320 = M("X_A320_ZZZZ", "A320", "");
    private static readonly AircraftModel GenA319 = M("X_A319_ZZZZ", "A319", "");
    private static readonly AircraftModel GenB738 = M("X_B738_ZZZZ", "B738", "");
    private static readonly AircraftModel GenB788 = M("X_B788_ZZZZ", "B788", "", WakeCategory.Heavy);

    private static ModelMatcher Matcher() => new(
        [XyzA320A, XyzA320B, RuleOnly, GenA320, GenA319, GenB738, GenB788],
        [
            new FsltlRule("XYZ", "A21N", ["X_A20N_XYZ_RULE", "NO_EXISTE"]),
            new FsltlRule("", "A318", ["X_A319_ZZZZ"]),
        ]);

    [Fact]
    public void AirlineLivery_OfRequestedType_Wins()
    {
        var m = Matcher().Match("xyz", "a320", "CC-ABC");

        Assert.Equal(MatchKind.AirlineLivery, m!.Kind);
        Assert.Contains(m.Model, new[] { XyzA320A, XyzA320B });
    }

    [Fact]
    public void AirlineLivery_FromFsltlRule_IgnoresMissingModels()
    {
        var m = Matcher().Match("XYZ", "A21N", "k");

        Assert.Equal(MatchKind.AirlineLivery, m!.Kind);
        Assert.Equal(RuleOnly, m.Model);
    }

    [Fact]
    public void NoAirlineLivery_FallsBackToGenericOfSameType()
    {
        var m = Matcher().Match("QQQ", "B738", "k");

        Assert.Equal(MatchKind.GenericSameType, m!.Kind);
        Assert.Equal(GenB738, m.Model);
    }

    [Fact]
    public void TypeWithoutModel_UsesGenericRuleEquivalent()
    {
        var m = Matcher().Match("QQQ", "A318", "k"); // A318 → genérico A319 según la regla

        Assert.Equal(MatchKind.GenericSameType, m!.Kind);
        Assert.Equal(GenA319, m.Model);
    }

    [Fact]
    public void UnknownType_UsesSimilarTypeOfSameFamily()
    {
        var m = Matcher().Match("QQQ", "B78X", "k");

        Assert.Equal(MatchKind.SimilarType, m!.Kind);
        Assert.Equal(GenB788, m.Model);
    }

    [Fact]
    public void NothingSimilar_NeverFails()
    {
        var m = Matcher().Match("QQQ", "ZZ99", "k");

        Assert.NotNull(m);
        Assert.Equal(MatchKind.AnySameCategory, m.Kind);
        Assert.Equal(WakeCategory.Medium, m.Model.Wake);
    }

    [Fact]
    public void Choice_IsStableForTheSameKey()
    {
        var a = Matcher().Match("XYZ", "A320", "CC-ABC")!.Model;
        var b = Matcher().Match("XYZ", "A320", "CC-ABC")!.Model;

        Assert.Equal(a, b);
    }

    [Fact]
    public void EmptyCatalog_ReturnsNull()
    {
        Assert.Null(new ModelMatcher([], []).Match("XYZ", "A320", "k"));
    }
}

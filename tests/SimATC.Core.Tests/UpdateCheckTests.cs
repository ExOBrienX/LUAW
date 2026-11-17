namespace SimATC.Core.Tests;

/// <summary>ADR-161: aviso de versión nueva al abrir.</summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.9.3-beta.1", "0.9.2-beta.1", true)]
    [InlineData("v0.9.3-beta.1", "0.9.2-beta.1", true)]
    [InlineData("0.9.2-beta.2", "0.9.2-beta.1", true)]
    [InlineData("0.9.2-beta.10", "0.9.2-beta.9", true)]
    [InlineData("0.9.2", "0.9.2-beta.1", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.9.2-beta.1", "0.9.2-beta.1", false)]
    [InlineData("0.9.1-beta.1", "0.9.2-beta.1", false)]
    [InlineData("0.9.2-beta.1", "0.9.2", false)]
    [InlineData("0.9.2-alpha.1", "0.9.2-beta.1", false)]
    [InlineData("basura", "0.9.2-beta.1", false)]
    [InlineData("0.9.3-beta.1", "?", false)]
    public void Newer_versions_are_detected(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(latest, current));

    [Fact]
    public void The_version_comes_from_the_release_tag()
    {
        Assert.Equal("0.9.3-beta.1", UpdateCheck.LatestVersion("""{"tag_name":"v0.9.3-beta.1","name":"LUAW 0.9.3-beta.1"}"""));
        Assert.Null(UpdateCheck.LatestVersion("""{"message":"Not Found"}"""));
        Assert.Null(UpdateCheck.LatestVersion("<html>"));
    }

    [Fact]
    public void It_checks_at_most_about_once_a_day()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateCheck.Due(null, now));
        Assert.False(UpdateCheck.Due(now.AddHours(-2), now));
        Assert.True(UpdateCheck.Due(now.AddHours(-21), now));
        Assert.True(UpdateCheck.Due(now.AddDays(1), now)); // reloj cambiado hacia atrás
    }

    [Fact]
    public void The_state_survives_a_restart_and_a_broken_file_is_ignored()
    {
        var dir = Directory.CreateTempSubdirectory("luaw-update-").FullName;
        try
        {
            var path = Path.Combine(dir, "update-check.json");
            var state = new UpdateState(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), "0.9.3-beta.1", "0.9.3-beta.1");
            state.Save(path);
            Assert.Equal(state, UpdateState.Load(path));
            File.WriteAllText(path, "{roto");
            Assert.Equal(new UpdateState(), UpdateState.Load(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

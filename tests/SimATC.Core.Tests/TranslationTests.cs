using System.Text.Json;
using System.Text.RegularExpressions;
using SimATC.Core.Text;

namespace SimATC.Core.Tests;

/// <summary>Cambia el idioma global de la interfaz: corre solo, sin otros tests en paralelo.</summary>
[CollectionDefinition("Tr", DisableParallelization = true)]
public sealed class TrCollection;

/// <summary>Interfaz en inglés (ADR-143): cada texto del XAML tiene su traducción y el traductor cae al español si falta.</summary>
[Collection("Tr")]
public partial class TranslationTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimATC.slnx")) && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    [GeneratedRegex("""\{app:T '([^']*)'\}""")]
    private static partial Regex Marker();

    [Fact]
    public void Every_xaml_text_has_an_english_translation()
    {
        var repo = Repo();
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(repo, "data", "i18n", "en.json")))!;
        var files = Directory.GetFiles(Path.Combine(repo, "src", "SimATC.App"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        var missing = files.SelectMany(f => Marker().Matches(File.ReadAllText(f)).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)))
            .Where(t => !table.ContainsKey(t)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Sin traducir: " + string.Join(" | ", missing));
    }

    [Fact]
    public void Missing_translations_fall_back_to_spanish_and_formats_are_filled()
    {
        try
        {
            Tr.Use("en", new Dictionary<string, string> { ["Pista {0} en uso"] = "Runway {0} in use" });
            Assert.Equal("Runway 27 in use", Tr.F("Pista {0} en uso", "27"));
            Assert.Equal("Sin traducción", Tr.T("Sin traducción"));
        }
        finally
        {
            Tr.Use("es", new Dictionary<string, string>());
        }
    }
}

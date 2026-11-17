using SimATC.Core;

namespace SimATC.Core.Tests;

/// <summary>Configuración del usuario (RF-41): se guarda, se recupera y un archivo dañado no rompe el arranque.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simatc-settings-" + Guid.NewGuid().ToString("N"));
    private string PathInDir => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        Assert.Equal(new AppSettings(), AppSettings.Load(PathInDir));
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var s = new AppSettings
        {
            Mode = CommsMode.Copilot,
            Strictness = Strictness.Permissive,
            CopilotTunesCom1 = false,
            MaxGround = 12,
            RadarFps = 30,
            SimBriefUser = "marco",
            CompactAlwaysOnTop = false,
        };
        s.Save(PathInDir);
        Assert.Equal(s, AppSettings.Load(PathInDir));
        Assert.Contains("\"Copilot\"", File.ReadAllText(PathInDir)); // enums legibles en el JSON
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_logs()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathInDir, "{ esto no es json");
        string? logged = null;
        Assert.Equal(new AppSettings(), AppSettings.Load(PathInDir, m => logged = m));
        Assert.NotNull(logged);
    }

    [Fact]
    public void Limits_follow_settings()
    {
        var s = new AppSettings { MaxGround = 7, MaxAir = 3, MaxTotal = 9 };
        Assert.Equal(7, s.Limits.MaxGround);
        Assert.Equal(3, s.Limits.MaxAir);
        Assert.Equal(9, s.Limits.MaxTotal);
    }

    // 2026-11-14: el idioma de la interfaz no se guardaba porque Equals (escrito a mano) no comparaba UiLanguage y el
    // cambio se tomaba como "sin cambios". Ahora cada propiedad guardada tiene que contar en la igualdad.
    [Fact]
    public void Every_saved_setting_counts_in_equality()
    {
        var baseline = new AppSettings();
        var missing = new List<string>();
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanWrite || prop.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length > 0)
                continue;
            object? other = prop.PropertyType switch
            {
                var t when t == typeof(string) => "otro valor",
                var t when t == typeof(bool) => !(bool)prop.GetValue(baseline)!,
                var t when t == typeof(int) => (int)prop.GetValue(baseline)! + 1,
                var t when t == typeof(double) => (double)prop.GetValue(baseline)! + 1,
                var t when t == typeof(float) => (float)prop.GetValue(baseline)! + 1,
                var t when t.IsEnum => NextEnum(t, prop.GetValue(baseline)!),
                var t when t == typeof(IReadOnlyList<string>) => new[] { "x" },
                var t when Nullable.GetUnderlyingType(t) is { } u && u.IsEnum => NextEnum(u, Enum.GetValues(u).GetValue(0)!),
                var t when Nullable.GetUnderlyingType(t) == typeof(int) => 12345,
                _ => null,
            };
            if (other is null)
            {
                missing.Add(prop.Name + " (tipo sin probar)");
                continue;
            }
            var copy = (AppSettings)typeof(AppSettings).GetMethod("<Clone>$")!.Invoke(baseline, null)!;
            prop.SetValue(copy, other);
            if (copy.Equals(baseline))
                missing.Add(prop.Name);
        }
        Assert.True(missing.Count == 0, "No cuentan en Equals: " + string.Join(", ", missing));
    }

    private static object NextEnum(Type t, object current)
    {
        var values = Enum.GetValues(t);
        return values.Length > 1 && Equals(values.GetValue(0), current) ? values.GetValue(1)! : values.GetValue(0)!;
    }
}

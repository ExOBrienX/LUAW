using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Una sección del manual con sus entradas (las que coinciden con la búsqueda).</summary>
public sealed record ManualSection(string Name, IReadOnlyList<ManualEntry> Entries);

/// <summary>
/// La página "Manual del ATC" (ADR-135): las interacciones de un vuelo por fase, con lo que dices tú, lo que responde el ATC,
/// cómo se pronuncia y un botón para oírlo. Sale de las plantillas del idioma y estilo elegidos.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public bool IsManual => Panel == "manual";

    private IReadOnlyList<ManualEntry> _manual = [];
    private string? _manualFor;

    [ObservableProperty]
    public partial string ManualQuery { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<ManualSection> ManualSections { get; private set; } = [];

    [ObservableProperty]
    public partial string ManualStatus { get; set; } = "";

    partial void OnManualQueryChanged(string value) => FilterManual();

    /// <summary>Arma el manual si cambió el idioma o el estilo de la fraseología (o todavía no está).</summary>
    private void EnsureManual()
    {
        var example = ManualExampleNow();
        var key = $"{_settings.AtcLanguage}|{_phrasesFaa}|{example}";
        if (_phrases is not { } phrases || _manualFor == key)
            return;
        _manualFor = key;
        _manual = AtcManual.Build(phrases, _airlines, _settings.AtcLanguage, example);
        FilterManual();
    }

    /// <summary>Los ejemplos del manual con el vuelo del piloto (su plan cargado); sin plan, un vuelo genérico.</summary>
    private ManualExample ManualExampleNow()
    {
        if (Plan is not { } p)
            return ManualExample.Generic;
        var g = ManualExample.Generic;
        var origin = Airport?.Icao == p.Origin.Icao ? _stations : null;
        var dest = _arrivalAirport?.Icao == p.Destination.Icao ? _arrivalStations : null;
        return new ManualExample(p.Callsign.ToUpperInvariant(), RadioBase(origin, p.Origin), RadioBase(dest, p.Destination),
            p.Destination.Icao, p.SidIdent ?? g.Sid, p.StarIdent ?? g.Star, p.Origin.PlannedRunway ?? g.DepartureRunway,
            p.Destination.PlannedRunway ?? g.ArrivalRunway,
            Core.Atc.RadioFormat.Level(p.CruiseAltitudeFt, p.CruiseAltitudeFt >= 10_000, _settings.AtcLanguage));
    }

    /// <summary>"Brussels Tower" → "Brussels"; sin dependencias cargadas, el nombre del plan o su código.</summary>
    private static string RadioBase(Core.Atc.AtcStations? stations, Core.Plan.PlanAirport airport)
    {
        if (stations?.For(Core.Atc.AtcRole.Tower)?.Callsign is { } cs && cs.LastIndexOf(' ') is > 0 and var i)
            return cs[..i];
        return airport.Name.Length > 0
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(airport.Name.ToLowerInvariant())
            : airport.Icao;
    }

    private void FilterManual()
    {
        var q = ManualQuery.Trim();
        ManualSections = [.. AtcManual.Sections
            .Select(s => new ManualSection(s, [.. _manual.Where(e => e.Section == s && e.Matches(q))]))
            .Where(s => s.Entries.Count > 0)];
        ManualStatus = ManualSections.Count == 0 ? Tr.T("Nada coincide con la búsqueda.") : "";
    }

    /// <summary>Oye la frase con la voz del ATC (o la del copiloto, para lo que dice el piloto), si la voz está instalada.</summary>
    [RelayCommand]
    private async Task ListenAsync(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken))
            return;
        if (_voice is null || !_voice.ReadyFor(_settings.AtcLanguage))
        {
            ManualStatus = Tr.T("Para oír las frases, instala la voz en Configuración → Voz.");
            return;
        }
        var id = _settings.VoiceAtc ?? _voice.Catalog.For(_settings.AtcLanguage)[0].Id;
        await _voice.DemoAsync(id, spoken, _settings.VoiceVolume / 100f);
    }
}

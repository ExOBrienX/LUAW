using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using SimATC.Core.Plan;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Una fila de "tu avión vs OFP" para la vista.</summary>
public sealed record CheckView(string Item, string Planned, string Actual, string Note, bool Ok, bool Warn)
{
    public bool Info => !Ok && !Warn;
}

/// <summary>Un bloque del briefing (Tiempos, Combustible, Pesos…).</summary>
public sealed record BriefingSection(string Title, IReadOnlyList<DiagItem> Items);

/// <summary>Panel Briefing: los datos importantes del OFP de SimBrief y el OFP completo.</summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    public partial IReadOnlyList<BriefingSection> BriefingSections { get; set; } = [];

    [ObservableProperty]
    public partial string OfpText { get; set; } = "";

    [ObservableProperty]
    public partial string BriefingNote { get; set; } = Tr.T("Carga un plan de SimBrief para ver su briefing.");

    public bool HasBriefing => BriefingSections.Count > 0;

    private UserAircraftLoad? _userLoad;

    /// <summary>Tu avión frente al OFP (se actualiza cada ~2 s con el simulador conectado).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CheckView> BriefingChecks { get; set; } = [];

    [ObservableProperty]
    public partial string BriefingCheckNote { get; set; } = Tr.T("Conecta MSFS para comparar tu avión con el OFP.");

    /// <summary>Llega desde el hilo del puente: se pasa al de la UI.</summary>
    private void OnUserLoad(UserAircraftLoad load) => Dispatcher.UIThread.Post(() =>
    {
        _userLoad = load;
        UpdateBriefingChecks();
    });

    private void UpdateBriefingChecks()
    {
        if (Plan?.Briefing is null)
        {
            BriefingChecks = [];
            BriefingCheckNote = Tr.T("Sin OFP de SimBrief no hay con qué comparar.");
            return;
        }
        if (_userLoad is not { } load || !_bridge.IsConnected)
        {
            BriefingChecks = [];
            BriefingCheckNote = Tr.T("Conecta MSFS para comparar tu avión con el OFP.");
            return;
        }
        BriefingChecks = [.. BriefingCheck.Compare(Plan, load, UserState?.Position, UserState?.OnGround ?? true).Select(r =>
            new CheckView(r.Item, r.Planned, r.Actual, r.Note, r.Status == CheckStatus.Ok, r.Status == CheckStatus.Warn))];
        BriefingCheckNote = Tr.T("Datos del simulador cada 2 s. Tolerancia: 3 % (mínimo 100 kg). Solo informativo.");
    }
    public bool HasPdf => Plan?.Briefing?.PdfUrl is not null;

    /// <summary>
    /// "Aplicar OFP al avión" (ADR-079, V-44): combustible de rampa y carga del OFP, matrícula, aerolínea y número de vuelo,
    /// con SimVars oficiales. La comparación de abajo (cada 2 s) muestra si el avión los aceptó o los cambió.
    /// </summary>
    [RelayCommand]
    private async Task ApplyOfpAsync()
    {
        if (Plan is not { Briefing: { } b } p || !_bridge.IsConnected)
            return;
        var toLbs = string.Equals(b.Units, "kgs", StringComparison.OrdinalIgnoreCase) ? 1 / 0.45359237 : 1;
        if (b.FuelRamp is not { } fuel || b.Payload is not { } payload)
        {
            BriefingCheckNote = Tr.T("El OFP no trae combustible de rampa o carga: no hay qué aplicar.");
            return;
        }
        // "SKU414" → aerolínea "SKU" y vuelo "414".
        var cs = p.Callsign;
        var split = cs.TakeWhile(char.IsLetter).Count();
        var request = new Core.Simulation.AircraftLoadRequest(fuel * toLbs, payload * toLbs, p.Registration,
            split > 0 ? cs[..split] : null, split < cs.Length ? cs[split..] : null);
        try
        {
            var summary = await _bridge.ApplyLoadAsync(request, CancellationToken.None);
            BriefingCheckNote = Tr.F("Aplicado: {0}. Revisa abajo si el avión lo mantiene.", summary);
            Log(Tr.F("OFP aplicado al avión: {0}.", summary));
        }
        catch (Exception ex)
        {
            BriefingCheckNote = Tr.F("No se pudo aplicar el OFP ({0}).", ex.Message);
            Log(BriefingCheckNote);
        }
    }

    partial void OnBriefingSectionsChanged(IReadOnlyList<BriefingSection> value) => OnPropertyChanged(nameof(HasBriefing));

    /// <summary>Abre el PDF del OFP en el navegador (enlace que da la API de SimBrief).</summary>
    [RelayCommand]
    private void OpenPdf()
    {
        if (Plan?.Briefing?.PdfUrl is { } url && url.StartsWith("https://www.simbrief.com/", StringComparison.Ordinal))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void UpdateBriefing()
    {
        OnPropertyChanged(nameof(HasPdf));
        UpdateBriefingChecks();
        if (Plan is not { } p || p.Briefing is not { } b)
        {
            BriefingSections = [];
            OfpText = "";
            BriefingNote = Plan is null ? Tr.T("Carga un plan de SimBrief para ver su briefing.")
                : Plan.Source == PlanSource.Manual ? Tr.T("Un plan manual no tiene briefing.")
                : Tr.T("Este plan se guardó antes de que existiera el briefing: vuelve a cargarlo de SimBrief.");
            return;
        }
        var u = b.Units == "lbs" ? "lb" : "kg";
        string W(int? v) => v is { } x ? $"{x.ToString("N0", Es)} {u}" : "—";
        DiagItem Weight(string name, int? value, int? max) =>
            new(name, max is { } m ? Tr.F("{0}  (máx. {1})", W(value), W(m)) : W(value), value > max);

        var sections = new List<BriefingSection>
        {
            new(Tr.T("Vuelo"),
            [
                new(Tr.T("Indicativo"), $"{p.Callsign} · {p.AircraftIcao}{(p.Registration is { } r ? $" · {r}" : "")}"),
                new(Tr.T("Ruta"), $"{p.Origin.Icao} {p.Origin.PlannedRunway} → {p.Destination.Icao} {p.Destination.PlannedRunway}" +
                            $"{(p.Alternate is { } a ? $" · alterno {a.Icao}" : "")}"),
                new(Tr.T("Distancia"), b.RouteDistanceNm is { } d ? $"{d} NM" : "—"),
                new(Tr.T("Tiempo de vuelo"), Duration(b.EnrouteS)),
                new(Tr.T("Tiempo de bloque"), Duration(b.BlockS)),
                new(Tr.T("Rodaje de salida"), Duration(b.TaxiOutS)),
                new(Tr.T("Salida programada"), p.ScheduledOffBlockUtc is { } off ? off.ToString("HH:mm", CultureInfo.InvariantCulture) + " Z" : "—"),
            ]),
            new(Tr.T("Crucero"),
            [
                new(Tr.T("Nivel inicial"), p.CruiseAltitudeFt > 0 ? $"FL{p.CruiseAltitudeFt / 100:000}" : "—"),
                new(Tr.T("Escalones"), b.StepClimbs ?? "—"),
                new(Tr.T("Cost index"), b.CostIndex ?? "—"),
                new(Tr.T("Viento medio"), b.AvgWind ?? "—"),
                new(Tr.T("Transición"), Tr.F("altitud {0} ft (salida) · nivel {1} (llegada)", b.TransitionAltitudeFt?.ToString("N0", Es) ?? "—", (b.TransitionLevelFt is { } tl ? $"FL{tl / 100:000}" : "—"))),
            ]),
            new(Tr.T("Combustible"),
            [
                new(Tr.T("En rampa"), W(b.FuelRamp)),
                new(Tr.T("Al despegue"), W(b.FuelTakeoff)),
                new(Tr.T("Mínimo al despegue"), W(b.FuelMinTakeoff)),
                new(Tr.T("Viaje"), W(b.FuelTrip)),
                new(Tr.T("Contingencia"), W(b.FuelContingency)),
                new(Tr.T("Al alterno"), W(b.FuelAlternate)),
                new(Tr.T("Reserva final"), W(b.FuelReserve)),
                new(Tr.T("Extra"), W(b.FuelExtra)),
                new(Tr.T("Rodaje"), W(b.FuelTaxi)),
                new(Tr.T("Al aterrizar"), W(b.FuelLanding)),
            ]),
            new(Tr.T("Pesos"),
            [
                Weight("ZFW", b.Zfw, b.MaxZfw),
                Weight("TOW", b.Tow, b.MaxTow),
                Weight("LDW", b.Ldw, b.MaxLdw),
                new(Tr.T("Carga útil"), W(b.Payload)),
                new(Tr.T("Pasajeros"), b.Passengers?.ToString(Es) ?? "—"),
                new(Tr.T("Carga"), W(b.Cargo)),
            ]),
        };
        if (b.Takeoff is { } t)
        {
            sections.Add(new(Tr.T("Despegue"), [
                new(Tr.T("Pista"), t.Runway),
                new(Tr.T("Flaps"), t.Flaps ?? "—"),
                new("V1 / VR / V2", Tr.F("{0} / {1} / {2} kt", t.V1?.ToString(Es) ?? "—", t.Vr?.ToString(Es) ?? "—", t.V2?.ToString(Es) ?? "—")),
                new(Tr.T("Empuje"), t.Thrust is "FLEX" && t.FlexTempC is { } flex ? $"FLEX {flex} °C" : t.Thrust ?? "—"),
            ]));
        }
        foreach (var w in b.Weather)
            sections.Add(new(Tr.F("Meteorología {0}", w.Icao), WeatherItems(w)));
        BriefingSections = sections;
        OfpText = b.OfpText;
        BriefingNote = p.GeneratedUtc is { } g
            ? Tr.F("OFP de SimBrief generado el {0} Z. Solo informativo: el ATC no lo usa.", g.ToString(Tr.T("dd-MM-yyyy HH:mm"), CultureInfo.InvariantCulture))
            : Tr.T("OFP de SimBrief. Solo informativo: el ATC no lo usa.");
    }

    /// <summary>
    /// El METAR y el TAF del OFP, el METAR actual de internet y lo que cambió desde el OFP (ADR-101, bloque D), con la pista
    /// en uso según cada uno si el aeropuerto está cargado.
    /// </summary>
    private List<DiagItem> WeatherItems(Core.Plan.AirportWeather w)
    {
        var items = new List<DiagItem> { new(Tr.T("METAR del OFP"), w.Metar ?? "—"), new(Tr.T("TAF del OFP"), w.Taf ?? "—") };
        if (MetarFor(w.Icao) is not { } now)
        {
            items.Add(new(Tr.T("METAR actual"), _metarFailed ? Tr.T("sin internet") : Tr.T("todavía no llega")));
            return items;
        }
        var age = (int)Math.Max(0, (DateTime.UtcNow - now.TimeUtc).TotalMinutes);
        items.Add(new(Tr.T("METAR actual"), Tr.F("{0} (hace {1} min)", now.Raw, age)));
        var ofp = w.Metar is { } raw ? Core.Atc.MetarParser.Parse(raw, DateTime.UtcNow) : null;
        if (ofp is null)
            return items;
        var diff = Core.Atc.MetarComparison.Differences(ofp, now);
        items.Add(diff.Count == 0
            ? new(Tr.T("Cambios desde el OFP"), Tr.T("sin cambios importantes"))
            : new(Tr.T("Cambios desde el OFP"), string.Join("; ", diff), Warn: true));
        // La pista en uso con cada viento (mismas reglas que el ATC), si el aeropuerto está cargado.
        var airport = _arrivalAirport?.Icao == w.Icao ? _arrivalAirport : Airport?.Icao == w.Icao ? Airport : null;
        if (airport is not null)
        {
            string RunwayWith(Core.Atc.Metar m)
            {
                var sel = new Core.Atc.ActiveRunwaySelector(airport);
                sel.Update(m.WindFromTrueDeg ?? 0, m.WindVariable ? 0 : m.WindKt, 0);
                return sel.Current?.Label ?? "—";
            }
            var before = RunwayWith(ofp);
            var after = RunwayWith(now);
            items.Add(new(Tr.T("Pista en uso"), before == after ? Tr.F("{0} (igual que con el METAR del OFP)", after) : Tr.F("{0} (con el METAR del OFP era {1})", after, before),
                Warn: before != after));
        }
        return items;
    }

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-CL");

    private static string Duration(int? seconds) =>
        seconds is { } s ? $"{s / 3600}:{s / 60 % 60:00} h" : "—";
}

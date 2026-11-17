using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.Core;
using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Aviso de versión nueva (ADR-161): al abrir, como mucho una vez al día, se pregunta a GitHub cuál es la última versión. Si
/// es más nueva, un aviso invita a bajarla desde flightsim.to (una vez por versión; "Acerca de LUAW" lo sigue mostrando).
/// Sin internet no pasa nada.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>La versión nueva disponible, o null si esta es la última (o no se sabe).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    public partial string? UpdateVersion { get; set; }

    public bool HasUpdate => UpdateVersion is not null;

    public string UpdateText => UpdateVersion is { } v ? Tr.F("Hay una versión nueva: LUAW {0}.", v) : "";

    public async Task CheckForUpdateAsync()
    {
        var path = UpdateState.DefaultPath;
        var state = UpdateState.Load(path);
        var now = DateTime.UtcNow;
        var latest = state.Latest;
        if (UpdateCheck.Due(state.LastCheckUtc, now))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"LUAW/{AppVersion}");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var json = await http.GetStringAsync(UpdateCheck.LatestReleaseApi);
                latest = UpdateCheck.LatestVersion(json) ?? latest;
                state = state with { LastCheckUtc = now, Latest = latest };
                state.Save(path);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                Log(Tr.F("No se pudo buscar una versión nueva ({0}).", ex.Message));
            }
        }
        if (latest is null || !UpdateCheck.IsNewer(latest, AppVersion))
            return;
        OfferUpdate(latest, showNotice: state.Dismissed != latest);
    }

    /// <summary>Muestra que hay una versión nueva: en "Acerca de LUAW" y, si <paramref name="showNotice"/>, con un aviso.</summary>
    public void OfferUpdate(string latest, bool showNotice = true)
    {
        UpdateVersion = latest;
        Log(Tr.F("Hay una versión nueva de LUAW: {0} (tienes la {1}).", latest, AppVersion));
        if (!showNotice || Notice is not null)
            return;
        Notice = new NoticeDialog(Tr.F("LUAW {0} disponible", latest),
            Tr.F("Tienes la versión {0}. La nueva trae correcciones y mejoras. Bájala desde flightsim.to y descomprímela donde quieras: tu configuración, tus vuelos y las voces instaladas se mantienen.", AppVersion),
            Tr.T("Ver en flightsim.to"), () =>
            {
                OpenUrl(UpdateCheck.DownloadPage);
                RememberDismissed(latest);
            }, Tr.T("Ahora no"))
        {
            OnSecondary = () => RememberDismissed(latest),
        };
    }

    /// <summary>No volver a mostrar el aviso de esta versión.</summary>
    private static void RememberDismissed(string version)
    {
        var path = UpdateState.DefaultPath;
        (UpdateState.Load(path) with { Dismissed = version }).Save(path);
    }

    [RelayCommand]
    private void OpenUpdatePage() => OpenUrl(UpdateCheck.DownloadPage);
}

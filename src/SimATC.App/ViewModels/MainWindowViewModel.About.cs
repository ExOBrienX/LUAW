using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// "Acerca de LUAW" en Configuración (ADR-150): versión, licencia, aviso de que no está afiliado a nadie y acceso a
/// los avisos de terceros y al código fuente (la GPL-3.0 pide que el código esté disponible).
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Repositorio público del proyecto.</summary>
    public const string SourceUrl = "https://github.com/ExOBrienX/LUAW";

    /// <summary>Donación voluntaria (ADR-155): la app es y seguirá siendo gratis, sin funciones de pago.</summary>
    public const string DonateUrl = "https://ko-fi.com/exo_brienx";

    /// <summary>
    /// Formulario anónimo de comentarios y bugs (Google Forms de Marco, ADR-156). Se abre con la versión ya escrita
    /// (<c>entry.1298392085</c> es la pregunta "LUAW version").
    /// </summary>
    public const string FeedbackUrl = "https://forms.gle/voKhCbMmKeNZTsLP9";

    private const string FeedbackPrefillUrl =
        "https://docs.google.com/forms/d/e/1FAIpQLSe3OQWs2Ar9ZjNNvyeY6_Wtt3yiUOq419IZAllJd-0VODQZ3w/viewform?usp=pp_url&entry.1298392085=";

    /// <summary>La versión de Directory.Build.props (sin el hash del commit que agrega el SDK).</summary>
    public static string AppVersion { get; } =
        (typeof(MainWindowViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        .Split('+')[0];

    public string AboutTitle => Tr.F("LUAW {0}", AppVersion);

    /// <summary>
    /// Los avisos de terceros: en el paquete están junto a la carpeta de la app (THIRD-PARTY-NOTICES.txt); en desarrollo,
    /// en la raíz del repositorio (.md). Se buscan subiendo desde el ejecutable.
    /// </summary>
    private static string? FindUpwards(params string[] names)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir.FullName, name);
                if (File.Exists(path) || Directory.Exists(path))
                    return path;
            }
        }
        return null;
    }

    [RelayCommand]
    private void OpenNotices()
    {
        if (FindUpwards("THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.md") is { } path)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            OpenUrl(SourceUrl + "/blob/main/THIRD-PARTY-NOTICES.md");
    }

    [RelayCommand]
    private void OpenLicenseFolder()
    {
        if (FindUpwards("licenses") is { } dir && Directory.Exists(dir))
            OpenFolder(dir);
        else
            OpenUrl(SourceUrl + "/blob/main/LICENSE");
    }

    [RelayCommand]
    private void OpenSource() => OpenUrl(SourceUrl);

    [RelayCommand]
    private void OpenDonate() => OpenUrl(DonateUrl);

    [RelayCommand]
    private void OpenFeedback() => OpenUrl(FeedbackPrefillUrl + Uri.EscapeDataString(AppVersion));

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

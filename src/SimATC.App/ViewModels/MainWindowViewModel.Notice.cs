using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Un aviso modal sobre toda la app: confirmar algo que no se puede deshacer (quitar el plan, limpiar el tráfico) o avisar
/// de algo que el piloto tiene que saber sí o sí (se perdió el simulador en vuelo).
/// </summary>
/// <param name="Danger">La acción principal borra algo: el botón va en rojo.</param>
public sealed record NoticeDialog(string Title, string Text, string Primary, Action? OnPrimary = null, string? Secondary = null, bool Danger = false)
{
    /// <summary>Lo que hace el botón secundario además de cerrar (p. ej. "mantener mi pista"), o null.</summary>
    public Action? OnSecondary { get; init; }

    public bool HasSecondary => Secondary is not null;
}

public sealed partial class MainWindowViewModel
{
    /// <summary>El aviso modal que se está mostrando, o null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial NoticeDialog? Notice { get; set; }

    public bool HasNotice => Notice is not null;

    [RelayCommand]
    private void NoticePrimary()
    {
        var notice = Notice;
        Notice = null;
        notice?.OnPrimary?.Invoke();
    }

    [RelayCommand]
    private void NoticeSecondary()
    {
        var notice = Notice;
        Notice = null;
        notice?.OnSecondary?.Invoke();
    }

    /// <summary>"Quitar plan": pide confirmación, porque la conversación con el ATC empieza de nuevo.</summary>
    [RelayCommand]
    private void AskClearPlan()
    {
        if (Plan is not { } p)
            return;
        Notice = new NoticeDialog(Tr.F("¿Quitar el plan {0}?", p.Callsign),
            Tr.F("Se quita el vuelo {0} → {1} y su ruta del radar. La conversación con el ATC empieza de nuevo ", p.Origin.Icao, p.Destination.Icao)
            + Tr.T("y este vuelo ya no se podrá retomar."),
            Tr.T("Quitar plan"), ClearPlan, Tr.T("Cancelar"), Danger: true);
    }

    /// <summary>"Limpiar todo el tráfico": pide confirmación antes de borrar los aviones del simulador.</summary>
    private void AskClearTraffic() =>
        Notice = new NoticeDialog(Tr.T("¿Limpiar todo el tráfico?"),
            Tr.T("Se borran del simulador todos los aviones creados por LUAW (estacionados, rodando y en vuelo). ")
            + Tr.T("Puedes volver a llenarlo con «Rellenar ahora» o «Iniciar tráfico»."),
            Tr.T("Limpiar tráfico"), ClearTrafficCore, Tr.T("Cancelar"), Danger: true);

    /// <summary>Se cortó la conexión con el simulador estando en vuelo (no por «Reconectar»).</summary>
    private void NoticeConnectionLost() =>
        Notice = new NoticeDialog(Tr.T("Se perdió la conexión con el simulador"),
            Tr.T("LUAW sigue abierto y se conecta solo cuando MSFS vuelva. Los aviones que había creado ya no están. ")
            + Tr.T("Si tenías un vuelo en curso, al reconectar te ofrecerá retomarlo donde quedó."),
            Tr.T("Entendido"));
}

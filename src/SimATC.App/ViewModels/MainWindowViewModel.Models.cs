using CommunityToolkit.Mvvm.Input;
using SimATC.Core.Fleet;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Configuración → Modelos de tráfico: carpetas detectadas y las que agrega el usuario.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Carpetas Community que se detectan solas (UserCfg.opt de MSFS 2024).</summary>
    public IReadOnlyList<string> DetectedModelFolders => MsfsInstall.FindCommunityFolders();

    public IReadOnlyList<string> ExtraModelFolders => _settings.ExtraModelFolders;

    public bool HasExtraModelFolders => _settings.ExtraModelFolders.Count > 0;

    /// <summary>Agrega una carpeta (la elige la vista con el selector de carpetas) y vuelve a revisar los modelos.</summary>
    public void AddModelFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (_settings.ExtraModelFolders.Contains(full, StringComparer.OrdinalIgnoreCase))
            return;
        Change(_settings with { ExtraModelFolders = [.. _settings.ExtraModelFolders, full] }, nameof(ExtraModelFolders), nameof(HasExtraModelFolders));
        Log(Tr.F("Carpeta de modelos agregada: {0}", full));
        if (ReloadCatalogCommand.CanExecute(null))
            ReloadCatalogCommand.Execute(null);
    }

    [RelayCommand]
    private void RemoveModelFolder(string path)
    {
        Change(_settings with { ExtraModelFolders = [.. _settings.ExtraModelFolders.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))] },
            nameof(ExtraModelFolders), nameof(HasExtraModelFolders));
        Log(Tr.F("Carpeta de modelos quitada: {0}", path));
        if (ReloadCatalogCommand.CanExecute(null))
            ReloadCatalogCommand.Execute(null);
    }
}

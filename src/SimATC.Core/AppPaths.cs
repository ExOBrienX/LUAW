namespace SimATC.Core;

/// <summary>Carpetas locales de LUAW (en %LOCALAPPDATA%\LUAW).</summary>
public static class AppPaths
{
    /// <summary>
    /// La carpeta de datos. La variable de entorno <c>SIMATC_HOME</c> la cambia: así las herramientas que abren la app sin
    /// simulador (capturas de la interfaz) no tocan la configuración, los vuelos ni los logs de verdad.
    /// </summary>
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("SIMATC_HOME") is { Length: > 0 } home
            ? home
            : LocalRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// <c>LUAW</c> dentro de <paramref name="localAppData"/>. La app se llamaba SimATC (ADR-154): si existe la carpeta vieja
    /// y no la nueva, se renombra para no perder la configuración, los vuelos ni la caché. Si no se puede (archivo en uso),
    /// se sigue usando la vieja.
    /// </summary>
    public static string LocalRoot(string localAppData)
    {
        var root = Path.Combine(localAppData, "LUAW");
        var old = Path.Combine(localAppData, "SimATC");
        if (Directory.Exists(root) || !Directory.Exists(old))
            return root;
        try
        {
            Directory.Move(old, root);
            return root;
        }
        catch (IOException)
        {
            return old;
        }
        catch (UnauthorizedAccessException)
        {
            return old;
        }
    }

    public static string AirportCache { get; } = Path.Combine(Root, "cache", "airports");

    public static string FleetCache { get; } = Path.Combine(Root, "cache");
}

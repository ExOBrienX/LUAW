namespace SimATC.App.ViewModels;

/// <summary>
/// Una fila de la ficha de un avión en el radar: qué ("Fase") y su valor ("rodando"). <paramref name="Group"/> abre un grupo
/// nuevo con ese título ("Ahora", "Llegada", "Ruta"…) encima de la fila.
/// </summary>
public sealed record InspectorRow(string Label, string Value, string? Group = null)
{
    public bool HasGroup => Group is not null;
}

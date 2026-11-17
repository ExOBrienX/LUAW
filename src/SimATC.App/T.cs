using Avalonia.Markup.Xaml;
using SimATC.Core.Text;

namespace SimATC.App;

/// <summary>Texto traducido en el XAML: <c>Text="{app:T 'Comunicaciones'}"</c>. Se resuelve al cargar la ventana.</summary>
public sealed class T(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => Tr.T(Text);
}

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

using SimATC.Core.Fleet;
using SimATC.Core.Geo;

namespace SimATC.App.Radar;

/// <summary>
/// Siluetas de avión a escala con los bocetos de Marco (ADR-173): avioneta, turbohélice, jet privado, fuselaje estrecho,
/// fuselaje ancho y cuatrimotor, recortados por <c>tools/data-gen/plane_sprites.py</c> como máscaras blancas. Cada imagen va
/// de punta de ala a punta de ala y de nariz a cola: se escala a la envergadura real (el largo sigue la proporción del
/// boceto) y se pinta del color que toca (verde el piloto, azul los IA, ámbar esperando, celeste el elegido).
/// </summary>
public sealed partial class RadarView
{
    // En el orden de AircraftShape.
    private static readonly Lazy<Bitmap[]> Sprites = new(() =>
        [Load("light"), Load("turboprop"), Load("bizjet"), Load("narrow"), Load("wide"), Load("quad")]);

    private static readonly Lazy<ImageBrush[]> SpriteMasks = new(() =>
        [.. Sprites.Value.Select(b => new ImageBrush(b) { Stretch = Stretch.Fill })]);

    private static Bitmap Load(string name) =>
        new(AssetLoader.Open(new Uri($"avares://LUAW/Assets/Planes/{name}.png")));

    /// <summary>Envergadura en pantalla (px) desde la que se dibuja a escala en vez de la flecha.</summary>
    private const double MinScaledSpanPx = 16;

    /// <summary>
    /// Dibuja el avión a su tamaño real, centrado en su posición y girado a su rumbo, si con este zoom queda más grande que
    /// la flecha; si no, devuelve false (se dibuja la flecha de siempre).
    /// </summary>
    private bool DrawToScale(DrawingContext ctx, Point p, double headingDeg, double wingspanM, AircraftShape shape, IBrush fill)
    {
        var spanPx = wingspanM * _scale;
        if (wingspanM <= 0 || spanPx < MinScaledSpanPx)
            return false;
        var kind = (int)shape;
        var sprite = Sprites.Value[kind];
        var lengthPx = spanPx * sprite.PixelSize.Height / sprite.PixelSize.Width;
        var rect = new Rect(-spanPx / 2, -lengthPx / 2, spanPx, lengthPx);
        var m = Matrix.CreateRotation(headingDeg * GeoMath.DegToRad) * Matrix.CreateTranslation(p.X, p.Y);
        using (ctx.PushTransform(m))
        {
            if (Tinted(kind, fill) is { } image)
                ctx.DrawImage(image, rect);
            else
                using (ctx.PushOpacityMask(SpriteMasks.Value[kind], rect))
                    ctx.FillRectangle(fill, rect);
        }
        return true;
    }

    // Siluetas ya pintadas de cada color (piloto, IA, esperando, elegido): dibujar una imagen es mucho más barato que una
    // máscara de opacidad por avión, que en cada cuadro arma una capa aparte (tirones al hacer zoom con muchos aviones).
    private static readonly Dictionary<(int Kind, Color Color), RenderTargetBitmap> TintCache = [];

    private static RenderTargetBitmap? Tinted(int kind, IBrush fill)
    {
        if (fill is not ISolidColorBrush solid)
            return null;
        var key = (kind, solid.Color);
        if (TintCache.TryGetValue(key, out var image))
            return image;
        if (TintCache.Count > 64)
            TintCache.Clear();
        var sprite = Sprites.Value[kind];
        image = new RenderTargetBitmap(sprite.PixelSize);
        using (var c = image.CreateDrawingContext())
        {
            var rect = new Rect(0, 0, sprite.PixelSize.Width, sprite.PixelSize.Height);
            using (c.PushOpacityMask(SpriteMasks.Value[kind], rect))
                c.FillRectangle(new ImmutableSolidColorBrush(solid.Color, solid.Opacity), rect);
        }
        TintCache[key] = image;
        return image;
    }
}

using Color = Microsoft.Maui.Graphics.Color;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
#if ANDROID
using Android.Graphics;
#elif WINDOWS
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
#elif MACCATALYST
using System.Runtime.CompilerServices;
using Foundation;
using UIKit;
#endif

namespace Canora.Controls;

// Le icone del Figma sono maschere nere su trasparente (nel design le colora il riempimento sottostante).
// Tint.Color le colora a runtime con il ColorFilter dell'ImageView: una sola icona per bianco, grigio e verde attivo.
public static class Tint
{
	public static readonly BindableProperty ColorProperty = BindableProperty.CreateAttached(
		"Color", typeof(Color), typeof(Tint), null,
		propertyChanged: (view, _, _) => (view as Image)?.Handler?.UpdateValue(nameof(ColorProperty)));

	public static Color? GetColor(BindableObject view) => (Color?)view.GetValue(ColorProperty);

	public static void SetColor(BindableObject view, Color? value) => view.SetValue(ColorProperty, value);

#if ANDROID
	// Da MauiProgram: la chiave del mapper viene applicata anche alla prima connessione dell'handler.
	public static void Register() =>
		ImageHandler.Mapper.AppendToMapping(nameof(ColorProperty), (handler, image) =>
		{
			if (image is BindableObject view && GetColor(view) is { } color)
				handler.PlatformView.SetColorFilter(color.ToPlatform(), PorterDuff.Mode.SrcIn!);
			else
				handler.PlatformView.ClearColorFilter();
		});
#elif WINDOWS
	// L'Image di WinUI non ha un filtro colore: sopra l'icona nera c'è uno sprite di Composition del colore voluto, con
	// la stessa immagine come maschera e grande quanto l'elemento. Si rifà quando cambiano colore o sorgente (play↔pausa)
	// e non dipende da quando l'immagine finisce di caricarsi: IconTintColorBehavior del toolkit, che aspetta ImageOpened,
	// sulle viste nate nascoste (il player) lasciava le icone nere, cioè invisibili sul fondo scuro.
	public static void Register()
	{
		ImageHandler.Mapper.AppendToMapping(nameof(ColorProperty), (handler, image) => Apply(handler.PlatformView, image));
		ImageHandler.Mapper.AppendToMapping(nameof(Microsoft.Maui.IImage.Source), (handler, image) => Apply(handler.PlatformView, image));
	}

	static void Apply(Microsoft.UI.Xaml.Controls.Image platform, Microsoft.Maui.IImage image)
	{
		if (image is not BindableObject view || GetColor(view) is not { } color || image.Source is not IFileImageSource { File.Length: > 0 } source)
		{
			ElementCompositionPreview.SetElementChildVisual(platform, null);
			return;
		}
		var compositor = ElementCompositionPreview.GetElementVisual(platform).Compositor;
		// Stretch Uniform e centrata come l'Image (AspectFit): la maschera combacia con l'icona disegnata sotto.
		// Dal file accanto all'exe e non da ms-appx:///: senza pacchetto quell'URI passa da MRT, che a volte non lo
		// risolve, e allora lo sprite resta vuoto e si vede l'icona nera. Resizetizer scrive solo i file con la scala.
		if (Mask(source.File) is not { } mask)
			return;
		var shape = compositor.CreateSurfaceBrush(mask);
		shape.Stretch = CompositionStretch.Uniform;
		var tinted = compositor.CreateMaskBrush();
		tinted.Source = compositor.CreateColorBrush(color.ToWindowsColor());
		tinted.Mask = shape;
		var sprite = compositor.CreateSpriteVisual();
		sprite.Brush = tinted;
		sprite.RelativeSizeAdjustment = Vector2.One;
		ElementCompositionPreview.SetElementChildVisual(platform, sprite);
	}

	// Una superficie per icona, condivisa dagli sprite: si carica una volta (in memoria: lo stream deve restare aperto
	// finché il caricamento, asincrono, non finisce). "ic_nav_home.png" → la versione più nitida fra quelle generate.
	static readonly Dictionary<string, LoadedImageSurface?> Masks = [];

	static LoadedImageSurface? Mask(string file)
	{
		if (Masks.TryGetValue(file, out var mask))
			return mask;
		var name = Path.GetFileNameWithoutExtension(file);
		foreach (var scale in (string[])[".scale-400", ".scale-200", ".scale-150", ".scale-100", ""])
			if (Path.Combine(AppContext.BaseDirectory, name + scale + Path.GetExtension(file)) is var path && File.Exists(path))
			{
				mask = LoadedImageSurface.StartLoadFromStream(new MemoryStream(File.ReadAllBytes(path)).AsRandomAccessStream());
				break;
			}
		return Masks[file] = mask;
	}
#elif MACCATALYST
	// UIKit colora da sé le immagini "template" con il TintColor dell'UIImageView. L'immagine arriva dopo, quando la
	// sorgente finisce di caricarsi (e cambia con play↔pausa): un osservatore KVO su "image" la rifà template ogni volta.
	public static void Register() =>
		ImageHandler.Mapper.AppendToMapping(nameof(ColorProperty), (handler, image) =>
		{
			var view = handler.PlatformView;
			if (image is BindableObject bindable && GetColor(bindable) is { } color)
			{
				view.TintColor = color.ToPlatform();
				Template(view);
				if (!Observers.TryGetValue(view, out _))
					Observers.Add(view, view.AddObserver("image", NSKeyValueObservingOptions.New, _ => Template(view)));
			}
			else if (Observers.TryGetValue(view, out var observer))
			{
				observer.Dispose();
				Observers.Remove(view);
				if (view.Image is { } tinted)
					view.Image = tinted.ImageWithRenderingMode(UIImageRenderingMode.Automatic);
			}
		});

	static readonly ConditionalWeakTable<UIImageView, IDisposable> Observers = new();

	// Rimettere Image fa scattare di nuovo l'osservatore: il controllo sul RenderingMode ferma il giro.
	static void Template(UIImageView view)
	{
		if (view.Image is { RenderingMode: not UIImageRenderingMode.AlwaysTemplate } image)
			view.Image = image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysTemplate);
	}
#endif
}

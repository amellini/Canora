using System.Reflection;
using Microsoft.UI.Input;

namespace Canora;

// Testo cliccabile come un link (album e artista nelle righe brano): manina sopra e sottolineato al passaggio del
// mouse. MAUI non ha il cursore: è ProtectedCursor dell'elemento WinUI, protetto, quindi impostato per riflessione.
static class LinkStyle
{
	static readonly PropertyInfo? Cursor = typeof(Microsoft.UI.Xaml.UIElement).GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic);

	public static void Apply(Label label)
	{
		label.HandlerChanged += (_, _) =>
		{
			if (label.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement element)
				Cursor?.SetValue(element, InputSystemCursor.Create(InputSystemCursorShape.Hand));
		};
		var hover = new PointerGestureRecognizer();
		hover.PointerEntered += (_, _) => label.TextDecorations = TextDecorations.Underline;
		hover.PointerExited += (_, _) => label.TextDecorations = TextDecorations.None;
		label.GestureRecognizers.Add(hover);
	}
}

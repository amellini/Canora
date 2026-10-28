using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Canora;

// I caroselli orizzontali (ScrollView e CollectionView) si trascinano col tasto sinistro del mouse come col dito sul
// telefono: WinUI fa scorrere gli ScrollViewer trascinando solo col touch. Un solo gestore sulla radice della finestra,
// con handledEventsToo, per tutti gli ScrollViewer con spazio in orizzontale. Superata la soglia lo ScrollViewer cattura
// il puntatore: la copertina sotto perde il tocco e l'album non si apre a fine trascinamento.
static class MouseDrag
{
	const double Threshold = 8; // px prima che un clic diventi un trascinamento

	static ScrollViewer? scroller;
	static double startX, startOffset;
	static bool dragging;

	public static void Attach(Microsoft.UI.Xaml.Window window)
	{
		var root = window.Content;
		root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
		root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
		root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
		// Solo la cattura dello ScrollViewer: quella che la copertina perde a inizio trascinamento non lo interrompe.
		root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
		{
			if (e.OriginalSource == scroller)
				Reset();
		}), true);
	}

	static void OnPressed(object sender, PointerRoutedEventArgs e)
	{
		var point = e.GetCurrentPoint(null);
		if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed)
			return;
		scroller = null;
		for (var d = e.OriginalSource as DependencyObject; d is not null && scroller is null; d = VisualTreeHelper.GetParent(d))
			if (d is ScrollViewer { ScrollableWidth: > 0 } horizontal)
				scroller = horizontal;
		(startX, startOffset, dragging) = (point.Position.X, scroller?.HorizontalOffset ?? 0, false);
	}

	static void OnMoved(object sender, PointerRoutedEventArgs e)
	{
		if (scroller is null)
			return;
		var point = e.GetCurrentPoint(null);
		if (!point.Properties.IsLeftButtonPressed) // rilasciato fuori dalla finestra prima della soglia
		{
			Reset();
			return;
		}
		var dx = point.Position.X - startX;
		if (!dragging && (Math.Abs(dx) < Threshold || !(dragging = scroller.CapturePointer(e.Pointer))))
			return;
		scroller.ChangeView(startOffset - dx, null, null, disableAnimation: true);
		e.Handled = true;
	}

	static void OnReleased(object sender, PointerRoutedEventArgs e)
	{
		if (dragging)
		{
			scroller?.ReleasePointerCapture(e.Pointer);
			e.Handled = true;
		}
		Reset();
	}

	static void Reset() => (scroller, dragging) = (null, false);
}

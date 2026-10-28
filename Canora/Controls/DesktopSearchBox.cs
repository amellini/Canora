using System.Windows.Input;

namespace Canora.Controls;

// Campo di ricerca compatto per Windows, sul modello dell'AutoSuggestBox di WinUI ma alto 44 (sta accanto a un titolo
// da 42): angoli 4, bordo sottile che prende l'accento col focus, lente di Segoe Fluent Icons a destra (toccarla cerca,
// come Invio). Prende il posto, su Cerca e Podcast, della barra bianca alta 63 del Figma, pensata per il telefono.
public class DesktopSearchBox : Border
{
	public static readonly BindableProperty TextProperty = BindableProperty.Create(nameof(Text), typeof(string), typeof(DesktopSearchBox),
		defaultBindingMode: BindingMode.TwoWay);
	public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(nameof(Placeholder), typeof(string), typeof(DesktopSearchBox));
	public static readonly BindableProperty SearchCommandProperty = BindableProperty.Create(nameof(SearchCommand), typeof(ICommand), typeof(DesktopSearchBox));

	public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
	public string? Placeholder { get => (string?)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
	public ICommand? SearchCommand { get => (ICommand?)GetValue(SearchCommandProperty); set => SetValue(SearchCommandProperty, value); }

	public DesktopSearchBox()
	{
		HeightRequest = 44;
		Padding = new Thickness(4, 0, 0, 0);
		StrokeThickness = 1;
		StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 };
		SetDynamicResource(BackgroundColorProperty, "Card");
		SetDynamicResource(StrokeProperty, "SeekRail");

		var entry = new Entry
		{
			FontFamily = "MontserratMedium",
			FontSize = 16,
			ReturnType = ReturnType.Search,
			ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
			VerticalOptions = LayoutOptions.Center,
		};
		entry.SetBinding(Entry.TextProperty, static (DesktopSearchBox b) => b.Text, BindingMode.TwoWay, source: this);
		entry.SetBinding(Entry.PlaceholderProperty, static (DesktopSearchBox b) => b.Placeholder, source: this);
		entry.SetBinding(Entry.ReturnCommandProperty, static (DesktopSearchBox b) => b.SearchCommand, source: this);
		entry.SetDynamicResource(Entry.TextColorProperty, "TextPrimary");
		entry.SetDynamicResource(Entry.PlaceholderColorProperty, "TextSecondary");
		entry.Focused += (_, _) => SetDynamicResource(StrokeProperty, "Accent");
		entry.Unfocused += (_, _) => SetDynamicResource(StrokeProperty, "SeekRail");

		var lens = new Label
		{
			Text = "", // Search
			FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets",
			FontSize = 16,
			WidthRequest = 44,
			HorizontalTextAlignment = TextAlignment.Center,
			VerticalTextAlignment = TextAlignment.Center,
			BackgroundColor = Colors.Transparent, // su WinUI senza sfondo il clic passerebbe solo sul glifo
		};
		lens.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
		lens.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => SearchCommand?.Execute(null)) });
		SemanticProperties.SetDescription(lens, "Cerca");

		var row = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
		row.Add(entry);
		row.Add(lens, 1);
		Content = row;
	}
}

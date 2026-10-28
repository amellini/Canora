using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Canora.Theming;

namespace Canora.Controls;

// Popup scuro dell'app al posto dei dialog di sistema, come l'AlertService di ChuckWalker (CommunityToolkit.Maui):
// titolo, messaggio facoltativo, una pillola bordata per azione (come "Segui") e "Annulla".
// ShowAsync restituisce l'azione scelta; PromptAsync aggiunge un campo di testo e ne restituisce il contenuto.
// null se si annulla, si tocca fuori o si preme Back.
public sealed class ActionPopup : Popup<string?>
{
	// Nuove a ogni apertura: il velo segue il tema attivo.
	static PopupOptions Options => new()
	{
		PageOverlayColor = Themes.Color("Scrim"),
		Shape = new RoundRectangle { CornerRadius = 12, StrokeThickness = 0 },
		Shadow = null,
	};

	readonly Entry? entry;

	public static Task<string?> ShowAsync(string title, string? message, params string[] actions) =>
		OpenAsync(new ActionPopup(title, message, actions, input: null));

	// Il nome (o null) se si conferma con un testo non vuoto.
	public static async Task<string?> PromptAsync(string title, string initial, string accept) =>
		(await OpenAsync(new ActionPopup(title, null, [accept], initial)))?.Trim() is { Length: > 0 } text ? text : null;

	static async Task<string?> OpenAsync(ActionPopup popup)
	{
		if (Application.Current?.Windows.FirstOrDefault()?.Page is not { } root)
			return null;
		// Sopra l'eventuale pagina modale aperta (la scelta della playlist), non sotto.
		var page = root.Navigation.ModalStack.LastOrDefault() ?? root;
		var result = await page.ShowPopupAsync<string?>(popup, Options);
		return result.Result;
	}

	ActionPopup(string title, string? message, string[] actions, string? input)
	{
		BackgroundColor = Themes.Color("Card");
		Padding = new Thickness(24, 24, 24, 12);
		WidthRequest = 320;

		var content = new VerticalStackLayout { Spacing = 10 };
		content.Add(new Label
		{
			Text = title,
			FontFamily = "MontserratBold",
			FontSize = 18,
			TextColor = Themes.Color("TextPrimary"),
			MaxLines = 2,
			LineBreakMode = LineBreakMode.TailTruncation,
		});
		if (message is not null)
			content.Add(new Label { Text = message, FontFamily = "MontserratMedium", FontSize = 14, TextColor = Themes.Color("TextSecondary") });

		if (input is not null)
		{
			entry = new Entry
			{
				Text = input,
				Placeholder = "Nome della playlist",
				MaxLength = 60,
				FontFamily = "MontserratMedium",
				FontSize = 16,
				TextColor = Themes.Color("TextPrimary"),
				PlaceholderColor = Themes.Color("TextSecondary"),
				ReturnType = ReturnType.Done,
			};
			entry.Completed += async (_, _) => await CloseAsync(entry.Text);
			// Campo già selezionato: si scrive sopra il nome proposto, o si tocca per correggerlo.
			Opened += (_, _) =>
			{
				entry.Focus();
				entry.CursorPosition = 0;
				entry.SelectionLength = entry.Text.Length;
			};
			content.Add(new Border
			{
				Content = entry,
				BackgroundColor = Themes.Color("SeekRail"),
				StrokeThickness = 0,
				StrokeShape = new RoundRectangle { CornerRadius = 6 },
				Padding = new Thickness(12, 2),
				Margin = new Thickness(0, 6, 0, 0),
			});
		}

		content.Add(new BoxView { HeightRequest = 6, Color = Colors.Transparent });
		foreach (var action in actions)
			content.Add(MakeButton(action, action, bordered: true));
		content.Add(MakeButton("Annulla", null, bordered: false));
		Content = content;
	}

	Button MakeButton(string text, string? result, bool bordered)
	{
		var button = new Button
		{
			Text = text,
			FontFamily = "MontserratBold",
			FontSize = 14,
			HeightRequest = 44,
			CornerRadius = 22,
			BackgroundColor = Colors.Transparent,
			TextColor = Themes.Color(bordered ? "TextPrimary" : "TextSecondary"),
			BorderColor = bordered ? Themes.Color("TextPrimary") : Colors.Transparent,
			BorderWidth = bordered ? 1 : 0,
		};
		// Con il campo di testo l'azione restituisce il testo scritto.
		button.Clicked += async (_, _) => await CloseAsync(result is not null && entry is not null ? entry.Text : result);
		return button;
	}

}

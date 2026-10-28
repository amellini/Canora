using Canora.Controls;
using Canora.Models;
using Canora.Services;

namespace Canora.Views;

// Si apre modale sopra la pagina radice (che resta viva, MediaElement compreso) e restituisce la playlist scelta.
public partial class PlaylistPickerPage : ContentPage
{
	readonly LibraryStore library;
	readonly TaskCompletionSource<Playlist?> result = new();

	PlaylistPickerPage(LibraryStore library)
	{
		InitializeComponent();
		this.library = library;
		BindingContext = library;
	}

	// null se l'utente chiude senza scegliere.
	public static async Task<Playlist?> PickAsync(INavigation navigation, LibraryStore library)
	{
		var page = new PlaylistPickerPage(library);
		await navigation.PushModalAsync(page);
		return await page.result.Task;
	}

	async void Close(Playlist? playlist)
	{
		if (result.TrySetResult(playlist))
			await Navigation.PopModalAsync();
	}

	protected override bool OnBackButtonPressed()
	{
		Close(null);
		return true;
	}

	void OnCloseTapped(object? sender, TappedEventArgs e) => Close(null);

	void OnPlaylistTapped(object? sender, TappedEventArgs e) => Close((Playlist)e.Parameter!);

	// Nuova playlist: si crea e il brano ci finisce subito dentro.
	async void OnNewTapped(object? sender, TappedEventArgs e)
	{
		if (await ActionPopup.PromptAsync("Nuova playlist", $"La mia playlist n. {library.Playlists.Count + 1}", "Crea") is { } name)
			Close(library.CreatePlaylist(name));
	}
}

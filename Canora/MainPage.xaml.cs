using Canora.Controls;
using Canora.Services;
using Canora.ViewModels;
using Canora.Views;

namespace Canora;

public partial class MainPage : ContentPage
{
	const int HomeTab = 0;
	const int SearchTab = 1;
	const int PodcastTab = 2;
	const int LibraryTab = 3;

	readonly LibraryViewModel library;
	readonly PodcastsViewModel podcasts;
	readonly CatalogViewModel catalog;
	readonly View[] tabs;
	readonly (Image Icon, Label Label)[] navItems;
	readonly (Image Icon, Label Label, BoxView Bar)[] sideItems;
	int current = -1;
	CancellationTokenSource? toast;

	public MainPage(PlayerViewModel player, SearchViewModel search, LibraryViewModel library, PodcastsViewModel podcasts, CatalogViewModel catalog)
	{
		InitializeComponent();
		this.library = library;
		this.podcasts = podcasts;
		this.catalog = catalog;
		ArtistDetail.BindingContext = catalog;
		AlbumDetail.BindingContext = catalog;
		BindingContext = player;
		Home.BindingContext = library;
		Search.BindingContext = search;
		Podcasts.BindingContext = podcasts;
		Library.BindingContext = library;
		PodcastDetail.BindingContext = podcasts;

		tabs = [Home, Search, Podcasts, Library];
		navItems = [(NavHomeIcon, NavHomeLabel), (NavSearchIcon, NavSearchLabel), (NavPodcastIcon, NavPodcastLabel), (NavLibraryIcon, NavLibraryLabel)];
		sideItems = [(SideHomeIcon, SideHomeLabel, SideHomeBar), (SideSearchIcon, SideSearchLabel, SideSearchBar),
			(SidePodcastIcon, SidePodcastLabel, SidePodcastBar), (SideLibraryIcon, SideLibraryLabel, SideLibraryBar)];
		ExpandSideBar(Preferences.Default.Get(SideBarExpandedKey, false));
		SelectTab(HomeTab);

		player.Attach(Media);
#if ANDROID
		Media.HandlerChanged += (_, _) => PlayerTuning.Apply(Media, player.OnPlayerRepeatModeChanged, () => player.NextCommand.Execute(null), () => player.PreviousCommand.Execute(null));
#endif
		player.Message += ShowToast;
		Home.SearchRequested += (_, _) => SelectTab(SearchTab);
		// Una playlist aperta dalla Home (o appena creata) si mostra nel tab Libreria.
		library.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(LibraryViewModel.Selected) && library.IsDetailOpen)
				SelectTab(LibraryTab);
		};
		// Nuovi episodi dei podcast seguiti, per il conteggio in Libreria.
		_ = podcasts.RefreshFollowedAsync();
	}

	// Primo avvio: prima la presentazione (nome e gusti), poi il resto. OnAppearing torna quando si chiude una pagina
	// modale (presentazione, Impostazioni, scelta della playlist): il saluto riprende il nome.
	// Notifiche: servono a "Tocca per ascoltare" quando l'assistente vocale non riesce ad aprire l'app (vedi
	// AssistantFunctions). Android mostra la richiesta solo finché non viene negata. Una volta per avvio, come il
	// controllo delle nuove versioni (dopo la presentazione, che non va interrotta).
	protected override void OnAppearing()
	{
		base.OnAppearing();
		library.Refresh();
		if (!Profile.IsOnboarded)
		{
			if (!onboarding)
			{
				onboarding = true;
				Dispatcher.Dispatch(() => _ = OnboardingPage.ShowAsync(Navigation, animated: false));
			}
			return;
		}
		if (!checkedUpdates)
		{
			checkedUpdates = true;
			_ = OfferUpdateAsync();
		}
#if ANDROID
		if (askedNotifications)
			return;
		askedNotifications = true;
		_ = Permissions.RequestAsync<Permissions.PostNotifications>();
#endif
	}

	bool onboarding, checkedUpdates;
#if ANDROID
	bool askedNotifications;
#endif

	// Una versione più recente su GitHub: si apre la pagina della release, oppure la si salta fino alla prossima;
	// "Annulla" la ripropone al prossimo avvio.
	static async Task OfferUpdateAsync()
	{
		if (await UpdateCheck.NewerAsync() is not { } release)
			return;
		const string download = "Scarica", skip = "Salta questa versione";
		switch (await ActionPopup.ShowAsync($"È uscita Can'ora {release.TagName.TrimStart('v')}",
			"La trovi su GitHub, con le novità. Playlist, ascolti e podcast restano.", download, skip))
		{
			case download:
				await Launcher.OpenAsync(UpdateCheck.ReleasesUrl);
				break;
			case skip:
				UpdateCheck.Skip(release);
				break;
		}
	}

	// Back di Android: chiude il player, poi il podcast, l'album/artista o la playlist aperti, poi torna alla Home, poi manda l'app
	// in background invece di chiuderla, così la riproduzione continua (come Spotify).
	protected override bool OnBackButtonPressed()
	{
		if (NowPlaying.IsVisible)
			_ = NowPlaying.HideAsync();
		else if (podcasts.IsDetailOpen)
			podcasts.Close();
		else if (catalog.IsOpen)
			catalog.Back();
		else if (current == LibraryTab && library.IsDetailOpen)
			library.ClosePlaylist();
		else if (current != HomeTab)
			SelectTab(HomeTab);
#if ANDROID
		else
			Platform.CurrentActivity?.MoveTaskToBack(true);
#endif
		return true;
	}

	void OnNavTapped(object? sender, TappedEventArgs e)
	{
		var index = int.Parse((string)e.Parameter!);
		// Un tocco sulla nav esce dai dettagli (podcast, artista, album); un secondo tocco su Libreria torna all'elenco,
		// come Spotify.
		podcasts.Close();
		catalog.Close();
		if (index == LibraryTab && current == LibraryTab)
			library.ClosePlaylist();
		SelectTab(index);
	}

	void OnMiniPlayerTapped(object? sender, TappedEventArgs e) => _ = NowPlaying.ShowAsync();

	// Barra laterale (solo Windows): compressa mostra le icone, espansa anche le etichette.
	const string SideBarExpandedKey = "SideBarExpanded";

	void OnSideBarToggleTapped(object? sender, TappedEventArgs e)
	{
		var expanded = SideBar.WidthRequest <= CollapsedSideBar;
		Preferences.Default.Set(SideBarExpandedKey, expanded);
		ExpandSideBar(expanded);
	}

	void ExpandSideBar(bool expanded)
	{
		SideBar.WidthRequest = expanded ? ExpandedSideBar : CollapsedSideBar;
		foreach (var item in sideItems)
			item.Label.IsVisible = expanded;
	}

	const double CollapsedSideBar = 57; // 56 di icone + 1 di bordo
	const double ExpandedSideBar = 220;

	async void SelectTab(int index)
	{
		if (index == current)
			return;
		current = index;
		for (var i = 0; i < tabs.Length; i++)
		{
			var active = i == index;
			// Risorsa dinamica e non il colore: segue il cambio tema.
			var color = active ? "TextPrimary" : "NavInactive";
			// Nav in basso e barra laterale insieme: se ne vede una sola (per piattaforma), restano allineate.
			foreach (var (icon, label) in new[] { navItems[i], (sideItems[i].Icon, sideItems[i].Label) })
			{
				icon.SetDynamicResource(Tint.ColorProperty, color);
				label.SetDynamicResource(Label.TextColorProperty, color);
			}
			sideItems[i].Bar.IsVisible = active;
			if (!active)
				tabs[i].IsVisible = false;
		}
		NavSearchLens.IsVisible = SideSearchLens.IsVisible = index == SearchTab;
		if (index == HomeTab)
			library.Refresh(); // il saluto segue l'ora a ogni ritorno sulla Home
		if (index is PodcastTab or LibraryTab)
			_ = podcasts.RefreshFollowedAsync();

		var tab = tabs[index];
		tab.Opacity = 0;
		tab.IsVisible = true;
		await tab.FadeToAsync(1, 150);
	}

	async void ShowToast(string message)
	{
		toast?.Cancel();
		var cts = toast = new CancellationTokenSource();
		ToastText.Text = message;
		ToastView.IsVisible = true;
		try
		{
			await ToastView.FadeToAsync(1, 150, null, cts.Token);
			await Task.Delay(2500, cts.Token);
			await ToastView.FadeToAsync(0, 250, null, cts.Token);
			ToastView.IsVisible = false;
		}
		catch (OperationCanceledException)
		{
			// Un messaggio più recente ha preso il posto di questo.
		}
	}
}

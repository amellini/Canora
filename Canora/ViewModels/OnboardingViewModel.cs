using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Canora.Models;
using Canora.Services;
using Sentry;

namespace Canora.ViewModels;

// Presentazione al primo avvio (Views/OnboardingPage), sul modello di Apple Music: il nome, poi fino a 5 generi, poi
// per ogni genere gli artisti che piacciono, quanti si vuole. Generi e artisti che non sono fra quelli proposti si
// cercano. Ci si può fermare a ogni passo: si salva quello scelto fin lì (Profile).
public sealed partial class OnboardingViewModel : ObservableObject
{
	public const int MaxChoices = 5;

	// Passi: il nome, i generi, poi uno per ogni genere scelto con i suoi artisti.
	const int NameStep = 0;
	const int GenreStep = 1;
	const int FirstArtistStep = 2;

	readonly CatalogService catalog;

	// Gli artisti di ogni genere, chiesti tutti appena scelti i generi: dal secondo genere in poi sono già pronti.
	readonly Dictionary<string, Task<List<CatalogArtist>>> loads = [];

	// Gli artisti scelti in tutti i generi, nell'ordine dei tocchi: uno che sta in più generi si sceglie una volta e
	// resta scelto anche se poi si toglie il suo genere.
	// ponytail: riaperta dalle Impostazioni, un artista scelto con la ricerca non compare nelle griglie (si ritrova
	// cercandolo); salvare il genere di ogni artista se serve rivederli tutti.
	readonly List<CatalogArtist> chosen;

	// Il genere in cui è stato scelto ogni artista: ne fa le compilation di "Per te" in Home.
	readonly Dictionary<long, string> genreOf;

	// Cambia a ogni passo: un caricamento che finisce quando si è già andati oltre non tocca più la griglia.
	int shown;
	bool finished;
	CancellationTokenSource? typing;

	// Rifatta dalle Impostazioni riparte dalle scelte salvate, generi cercati compresi.
	public OnboardingViewModel(CatalogService catalog)
	{
		this.catalog = catalog;
		var genres = Profile.Genres;
		Genres =
		[
			.. CatalogService.Genres.Select(g => new GenreChoice(g.Name, g.Query) { IsSelected = genres.Contains(g.Name) }),
			.. genres.Where(name => !CatalogService.Genres.Any(g => g.Name == name)).Select(name => new GenreChoice(name, name) { IsSelected = true }),
		];
		chosen = Profile.Artists;
		genreOf = Profile.ArtistGenres;
		Name = Profile.Name;
		UpdateGenres();
	}

	// Presentazione finita o saltata: la pagina si chiude.
	public event Action? Finished;

	// Facoltativo: vuoto si passa ai generi lo stesso.
	public string Name { get; set; }

	public ObservableCollection<GenreChoice> Genres { get; }
	public ObservableCollection<ArtistChoice> Artists { get; } = [];
	public ObservableCollection<ArtistChoice> Results { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNameStep), nameof(IsGenreStep), nameof(IsArtistStep), nameof(CanGoBack), nameof(CanContinue),
		nameof(ContinueText), nameof(SkipText), nameof(GenreTitle), nameof(GenreProgress))]
	public partial int Step { get; private set; }

	[ObservableProperty]
	public partial bool IsLoading { get; private set; }

	[ObservableProperty]
	public partial bool LoadFailed { get; private set; }

	public bool IsNameStep => Step == NameStep;
	public bool IsGenreStep => Step == GenreStep;
	public bool IsArtistStep => Step >= FirstArtistStep;
	public bool CanGoBack => Step > NameStep;

	List<GenreChoice> SelectedGenres => [.. Genres.Where(g => g.IsSelected)];
	GenreChoice? Genre => IsArtistStep ? SelectedGenres[Step - FirstArtistStep] : null;
	bool IsLastStep => IsArtistStep && Step - FirstArtistStep == SelectedGenres.Count - 1;

	// Senza artisti scelti si passa al genere dopo; senza generi non c'è un passo dopo (resta "Salta").
	public bool CanContinue => !IsGenreStep || SelectedGenres.Count > 0;

	public string ContinueText => IsLastStep ? "Fine" : "Continua";

	// In alto a destra: salta la presentazione; fra gli artisti la chiude tenendo quelli già scelti.
	public string SkipText => IsArtistStep ? "Fine" : "Salta";

	public string GenreTitle => Genre?.Name ?? "";
	public string GenreProgress => IsArtistStep ? $"GENERE {Step - FirstArtistStep + 1} DI {SelectedGenres.Count}" : "";

	// --- Ricerca fra i generi: filtra le pillole; se non ne resta nessuna, "+ …" aggiunge il genere cercato ---

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanAddGenre), nameof(NewGenreText))]
	public partial string GenreQuery { get; set; } = "";

	public bool CanAddGenre => NewGenre.Length > 0 && !Genres.Any(g => g.IsMatch);
	public string NewGenreText => $"+ {NewGenre}";

	// "techno" → "Techno", come le pillole.
	string NewGenre => GenreQuery.Trim() is { Length: > 0 } name ? string.Concat(name[..1].ToUpper(), name[1..]) : "";

	partial void OnGenreQueryChanged(string value)
	{
		foreach (var genre in Genres)
			genre.IsMatch = value.Trim().Length == 0 || LibraryStore.Matches(genre.Name, value);
	}

	// Il genere cercato entra in fondo alle pillole, scelto se c'è posto.
	[RelayCommand]
	void AddGenre()
	{
		if (!CanAddGenre)
			return;
		var genre = new GenreChoice(NewGenre, NewGenre) { IsSelected = SelectedGenres.Count < MaxChoices };
		Genres.Add(genre);
		GenreQuery = "";
		UpdateGenres();
	}

	// --- Ricerca fra gli artisti di Deezer, al posto della griglia del genere finché c'è testo ---

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSearchingArtists), nameof(ShowsGenreArtists))]
	public partial string ArtistQuery { get; set; } = "";

	public bool IsSearchingArtists => ArtistQuery.Trim().Length > 0;
	public bool ShowsGenreArtists => !IsSearchingArtists;

	[ObservableProperty]
	public partial bool IsSearching { get; private set; }

	// "Nessun artista trovato" o l'errore di rete, sotto la barra.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSearchMessage))]
	public partial string SearchMessage { get; private set; } = "";

	public bool HasSearchMessage => SearchMessage.Length > 0;

	partial void OnArtistQueryChanged(string value) => _ = SearchArtistsAsync(value);

	// Mentre si scrive, come nel tab Cerca: si cerca solo dopo una pausa.
	async Task SearchArtistsAsync(string query)
	{
		typing?.Cancel();
		var cts = typing = new CancellationTokenSource();
		Results.Clear();
		SearchMessage = "";
		IsSearching = query.Trim().Length > 0;
		if (!IsSearching)
			return;
		try
		{
			await Task.Delay(450, cts.Token);
			var found = await Task.Run(() => catalog.SearchArtistsAsync(query, 12, cts.Token), cts.Token);
			if (cts.IsCancellationRequested)
				return;
			foreach (var artist in found)
				Results.Add(new ArtistChoice(artist) { IsSelected = chosen.Any(a => a.Id == artist.Id) });
			SearchMessage = found.Count == 0 ? "Nessun artista trovato" : "";
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			SearchMessage = "Impossibile cercare. Controlla la connessione.";
		}
		IsSearching = false;
	}

	[RelayCommand]
	void ToggleGenre(GenreChoice genre)
	{
		if (!genre.IsSelected && SelectedGenres.Count >= MaxChoices)
			return;
		genre.IsSelected = !genre.IsSelected;
		UpdateGenres();
	}

	[RelayCommand]
	void ToggleArtist(ArtistChoice choice)
	{
		// Un risultato della ricerca va in cima alla griglia del genere e la ricerca si chiude. Entra anche nella lista in
		// cache del genere, così c'è ancora tornando su questo passo.
		if (!Artists.Contains(choice))
		{
			if (Artists.FirstOrDefault(a => a.Artist.Id == choice.Artist.Id) is { } shown)
				choice = shown;
			else
			{
				Artists.Insert(0, choice = new ArtistChoice(choice.Artist) { IsSelected = choice.IsSelected });
				if (Genre is { } genre && loads.TryGetValue(genre.Query, out var load) && load.IsCompletedSuccessfully)
					load.Result.Insert(0, choice.Artist);
			}
			ArtistQuery = "";
		}
		choice.IsSelected = !choice.IsSelected;
		chosen.RemoveAll(a => a.Id == choice.Artist.Id);
		genreOf.Remove(choice.Artist.Id);
		if (choice.IsSelected && Genre is { } current)
		{
			chosen.Add(choice.Artist);
			genreOf[choice.Artist.Id] = current.Name;
		}
	}

	[RelayCommand]
	void Continue()
	{
		if (!CanContinue)
			return;
		if (IsGenreStep)
			foreach (var genre in SelectedGenres)
				_ = Load(genre.Query);
		if (IsLastStep)
			Finish();
		else
			GoTo(Step + 1);
	}

	// Freccia e Back di Android: il passo prima; dal primo si salta la presentazione.
	[RelayCommand]
	public void Back()
	{
		if (Step == NameStep)
			Finish();
		else
			GoTo(Step - 1);
	}

	[RelayCommand]
	void Skip() => Finish();

	[RelayCommand]
	void Retry() => GoTo(Step);

	void GoTo(int step)
	{
		var version = ++shown;
		Step = step;
		GenreQuery = "";
		ArtistQuery = "";
		Artists.Clear();
		LoadFailed = false;
		IsLoading = false;
		if (Genre is { } genre)
			_ = ShowArtistsAsync(genre.Query, version);
	}

	async Task ShowArtistsAsync(string query, int version)
	{
		IsLoading = true;
		try
		{
			var artists = await Load(query);
			if (version != shown)
				return;
			foreach (var artist in artists)
				Artists.Add(new ArtistChoice(artist) { IsSelected = chosen.Any(a => a.Id == artist.Id) });
			if (artists.Count == 0)
				Fail();
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			if (version == shown)
				Fail();
		}
		finally
		{
			if (version == shown)
				IsLoading = false;
		}

		// Riprova rifà la richiesta: quella fallita (o vuota) esce dalla cache.
		void Fail()
		{
			loads.Remove(query);
			LoadFailed = true;
		}
	}

	Task<List<CatalogArtist>> Load(string query)
	{
		if (!loads.TryGetValue(query, out var load))
			loads[query] = load = Task.Run(() => catalog.GenreArtistsAsync(query, CancellationToken.None));
		return load;
	}

	// Raggiunto il massimo, gli altri generi si spengono.
	void UpdateGenres()
	{
		var full = SelectedGenres.Count >= MaxChoices;
		foreach (var genre in Genres)
			genre.IsDimmed = full && !genre.IsSelected;
		OnPropertyChanged(nameof(CanContinue));
	}

	void Finish()
	{
		if (finished)
			return;
		finished = true;
		++shown;
		typing?.Cancel();
		Profile.Save(Name, [.. SelectedGenres.Select(g => g.Name)], chosen, genreOf);
		Finished?.Invoke();
	}
}

// Una pillola dei generi: scelta o no, spenta quando se ne sono già scelti 5.
public sealed partial class GenreChoice(string name, string query) : ObservableObject
{
	public string Name => name;
	public string Query => query;

	[ObservableProperty]
	public partial bool IsSelected { get; set; }

	[ObservableProperty]
	public partial bool IsDimmed { get; set; }

	// Visibile con la ricerca in corso.
	[ObservableProperty]
	public partial bool IsMatch { get; set; } = true;
}

// Un artista della griglia, scelto o no.
public sealed partial class ArtistChoice(CatalogArtist artist) : ObservableObject
{
	public CatalogArtist Artist => artist;

	[ObservableProperty]
	public partial bool IsSelected { get; set; }
}

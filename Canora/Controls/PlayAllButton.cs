using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Canora.Models;
using Canora.Services;
using Canora.ViewModels;

namespace Canora.Controls;

// Play verde 60 delle schede (artista, album, playlist, podcast). Come Spotify: mentre suona una voce della scheda
// diventa pausa, e il tocco mette in pausa o riprende quella; altrimenti Command fa partire la scheda.
public sealed class PlayAllButton : Grid
{
	public static readonly BindableProperty ItemsProperty = BindableProperty.Create(nameof(Items), typeof(IEnumerable), typeof(PlayAllButton),
		propertyChanged: (view, old, now) => ((PlayAllButton)view).OnItemsChanged(old, now));

	public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(PlayAllButton));

	// Le voci della scheda: Track o Episode.
	public IEnumerable? Items
	{
		get => (IEnumerable?)GetValue(ItemsProperty);
		set => SetValue(ItemsProperty, value);
	}

	// Fa partire la scheda; senza parametro, come il tocco sul vecchio play.
	public ICommand? Command
	{
		get => (ICommand?)GetValue(CommandProperty);
		set => SetValue(CommandProperty, value);
	}

	readonly Image icon = new() { Source = "ic_play_circle.png", WidthRequest = 60, HeightRequest = 60 };
	bool hasCurrent;
	bool showsPause;

	// Singleton, come il catalogo per TrackRow.
	static PlayerViewModel? Player => IPlatformApplication.Current?.Services.GetService<PlayerViewModel>();

	public PlayAllButton()
	{
		icon.SetDynamicResource(Tint.ColorProperty, "Accent");
		Add(icon);
		SemanticProperties.SetDescription(this, "Riproduci");
		var tap = new TapGestureRecognizer();
		tap.Tapped += OnTapped;
		GestureRecognizers.Add(tap);
		// Iscritti solo mentre la scheda è a schermo, come le righe con NowPlayingBadge.
		Loaded += (_, _) =>
		{
			NowPlaying.Changed += Refresh;
			Player?.PropertyChanged += OnPlayerChanged;
			Refresh();
		};
		Unloaded += (_, _) =>
		{
			NowPlaying.Changed -= Refresh;
			Player?.PropertyChanged -= OnPlayerChanged;
		};
	}

	void OnTapped(object? sender, TappedEventArgs e)
	{
		if (hasCurrent && Player is { } player)
			player.TogglePlayCommand.Execute(null);
		else if (Command?.CanExecute(null) == true)
			Command.Execute(null);
	}

	void OnItemsChanged(object? old, object? now)
	{
		if (old is INotifyCollectionChanged before)
			before.CollectionChanged -= OnCollectionChanged;
		if (now is INotifyCollectionChanged after)
			after.CollectionChanged += OnCollectionChanged;
		Refresh();
	}

	void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

	void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
			Render();
	}

	// Il brano in corso è della scheda? Solo al cambio di brano o di lista: NowPlaying.Is di una voce del catalogo può
	// leggere l'abbinamento da SQLite, e una playlist ha molte righe. Play/pausa ridisegna e basta.
	void Refresh()
	{
		hasCurrent = Items?.Cast<object>().Any(item => NowPlaying.Is(item switch
		{
			Track track => track.Id,
			Episode episode => episode.Key,
			_ => null,
		})) == true;
		Render();
	}

	void Render()
	{
		var pause = hasCurrent && Player?.IsPlaying == true;
		if (pause == showsPause)
			return;
		showsPause = pause;
		icon.Source = pause ? "ic_pause_circle.png" : "ic_play_circle.png";
		SemanticProperties.SetDescription(this, pause ? "Pausa" : "Riproduci");
	}
}

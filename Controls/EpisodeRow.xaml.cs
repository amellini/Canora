using System.Windows.Input;

namespace Canora.Controls;

public partial class EpisodeRow : ContentView
{
	// Pulsante "Da ascoltare" / "Ascoltato", riceve l'Episode della riga.
	public static readonly BindableProperty ListenedCommandProperty = BindableProperty.Create(nameof(ListenedCommand), typeof(ICommand), typeof(EpisodeRow));

	public ICommand? ListenedCommand
	{
		get => (ICommand?)GetValue(ListenedCommandProperty);
		set => SetValue(ListenedCommandProperty, value);
	}

	public EpisodeRow()
	{
		InitializeComponent();
		NowPlayingBadge.Track(this, () => (BindingContext as Models.Episode)?.Key, Playing, TitleLabel);
	}
}

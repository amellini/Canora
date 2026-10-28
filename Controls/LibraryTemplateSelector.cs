using Canora.Models;

namespace Canora.Controls;

// Libreria: playlist e podcast seguiti nella stessa lista, ciascuno con la sua riga.
public sealed class LibraryTemplateSelector : DataTemplateSelector
{
	public DataTemplate? PlaylistTemplate { get; set; }
	public DataTemplate? PodcastTemplate { get; set; }

	protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
		(item is Podcast ? PodcastTemplate : PlaylistTemplate)!;
}

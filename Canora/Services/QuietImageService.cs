using Shiny.Maui.Controls.Images;

namespace Canora.Services;

// Il servizio di ShinyImage senza progress. ShinyImage passa un Progress<T>, che consegna i suoi rapporti più tardi
// sul thread UI: un "Queued" arrivato dopo il caricamento riporta l'immagine da Loaded a Queued e la nasconde (su
// Windows succedeva a metà delle card della Home). L'anello è nascosto (Styles.xaml), quindi il progress non serve.
// ponytail: wrapper invece di una patch a Shiny.Maui.Controls 2.1.0; da togliere quando la libreria lo corregge.
sealed class QuietImageService(IImageService inner) : IImageService
{
	public Task<ImageResult> GetAsync(ImageRequest request, IProgress<ImageLoadProgress>? progress = null, CancellationToken cancellationToken = default) =>
		inner.GetAsync(request, null, cancellationToken);

	public Task ClearCacheAsync(CancellationToken cancellationToken = default) => inner.ClearCacheAsync(cancellationToken);

	public Task ClearCacheAsync(string uri, CancellationToken cancellationToken = default) => inner.ClearCacheAsync(uri, cancellationToken);

	public Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default) => inner.GetCacheSizeAsync(cancellationToken);

	public Task PrefetchAsync(IEnumerable<string> uris, CancellationToken cancellationToken = default) => inner.PrefetchAsync(uris, cancellationToken);
}

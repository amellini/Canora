using System.Net;
using System.Web;

namespace Canora.Services;

// GetManifestAsync di YoutubeExplode controlla ogni stream del video scaricandone gli ultimi 2 byte (un 404 lo scarta):
// una trentina di richieste in fila, quasi tutte per stream che qui non si suonano mai. Erano 1–4 s fra il tocco su un
// brano e l'audio (manifest misurato: 2,4 s di media, 0,3 s così). Si chiudono subito, con un 200 vuoto, le sonde dei
// video e degli stream audio diversi da quelli che l'app sceglie: Opus 251 (Android) e AAC 140 (Windows), che vanno
// in rete come prima. Se uno dei due manca, la scelta ricade su un altro audio: non verificato, ma è il caso raro.
// Risposte compresse: YoutubeExplode non le chiede, e una ricerca pesava ~710 KB invece di ~26 KB (0,9 s → 0,55 s).
// Gli stream audio non cambiano, YouTube non li comprime.
sealed class SkipStreamProbes() : DelegatingHandler(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.RequestUri is { AbsolutePath: "/videoplayback" } uri)
		{
			var query = HttpUtility.ParseQueryString(uri.Query);
			if (query["itag"] is not ("251" or "140")
				&& long.TryParse(query["clen"], out var length)
				&& query["range"] == $"{length - 2}-{length - 1}")
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
		}
		return base.SendAsync(request, cancellationToken);
	}
}

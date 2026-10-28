<p align="center">
	<img src="Canora/Resources/Images/logo.svg" width="96" alt="Can'ora logo">
</p>

<h1 align="center">Can'ora</h1>

<p align="center">
	A Spotify-style music and podcast player for Android and Windows, built with .NET MAUI.<br>
	Un player di musica e podcast in stile Spotify per Android e Windows, scritto in .NET MAUI.
</p>

<p align="center">
	<a href="#english">English</a> · <a href="#italiano">Italiano</a>
</p>

## Screenshots

### Android

| Home | Artist · Artista | Album | Now playing · In riproduzione |
| :---: | :---: | :---: | :---: |
| <img src="docs/screenshots/android-home.png" width="200" alt="Home"> | <img src="docs/screenshots/android-artist.png" width="200" alt="Artist page"> | <img src="docs/screenshots/android-album.png" width="200" alt="Album page"> | <img src="docs/screenshots/android-now-playing.png" width="200" alt="Now playing"> |

### Windows

| Home | Search · Cerca |
| :---: | :---: |
| <img src="docs/screenshots/windows-home.png" width="420" alt="Home"> | <img src="docs/screenshots/windows-search.png" width="420" alt="Search"> |
| **Podcasts · Podcast** | **Settings and themes · Impostazioni e temi** |
| <img src="docs/screenshots/windows-podcasts.png" width="420" alt="Podcasts"> | <img src="docs/screenshots/windows-settings.png" width="420" alt="Settings and themes"> |

---

## English

Can'ora is a personal project: a music player that looks and feels like Spotify, with no account to create. You search
the catalog, open artist and album pages, build your playlists, sing along with synced lyrics and follow podcasts.

The catalog (artists, albums, covers, charts) comes from Deezer's public API. The audio is streamed from YouTube: for
each track the app picks the video with the same length.

The interface is in Italian. The name comes from *canora*, Italian for "melodious" (as in *voce canora*, a tuneful
voice).

### Download

Get the APK for Android and the zip for Windows from [Releases](https://github.com/amellini/Canora/releases).

- **Android 8.0 or later**: open the APK and allow installs from that source when Android asks.
- **Windows 10 1809 or later**: unzip and run `Canora.exe`. The exe is not signed, so SmartScreen may ask you to
  confirm (*More info → Run anyway*).

### Features

- **Search** for artists, albums, tracks, playlists and podcasts, with recent searches and genre tiles to browse.
- **Artist pages** with top tracks, albums and singles. **Album and playlist pages** with play all and shuffle.
- **Home** that greets you and suggests music: a *weekly pick* of new tracks from the radios of the artists you listen
  to most (plus ListenBrainz suggestions if you add your token), *genre mixes* and *recently played*.
- **Library** with your playlists and the podcasts you follow, stored on the device (SQLite).
- **Now playing** colored after the cover art, with repeat (all or one) and **karaoke**: synced lyrics from LRCLIB.
  Tap a line to jump there; the padlock keeps the screen on while you sing.
- **Podcasts**: search by title, follow a show and its new episodes appear in the Library. Playback resumes where you
  left off, with 15-second skips back and forward.
- **First-run setup**: your name, favorite genres and artists, so the suggestions work from day one.
- **Five themes**: Classic, Cobalt Sky, Laguna, Warm Autumn Glow and Rustic Charm.
- **Android**: background playback with the media notification. Voice commands from the phone assistant through
  Android 16 AppFunctions (experimental), such as *"Riproduci &lt;song&gt; da Can'ora"*.
- **Windows**: a plain unpackaged `.exe`, sidebar navigation, previous / play-pause / next buttons in the taskbar
  thumbnail, horizontal lists you can drag with the mouse.

### Under the hood

- .NET 11 MAUI: one project for Android and Windows, CoreCLR, XAML source generation and compiled bindings.
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [CommunityToolkit.Maui](https://github.com/CommunityToolkit/Maui)
  (MediaElement for playback).
- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) for YouTube search and audio streams.
- [sqlite-net-pcl](https://github.com/praeclarum/sqlite-net) for playlists, recently played and listening history.
- [Shiny.AppFunctions](https://github.com/shinyorg/shiny) for the assistant commands on Android.
- Free web services with no API key: [Deezer API](https://developers.deezer.com/api) (catalog),
  [LRCLIB](https://lrclib.net) (lyrics), [iTunes Search API](https://performance-partners.apple.com/search-api)
  (podcasts), [ListenBrainz](https://listenbrainz.org) and [Cover Art Archive](https://coverartarchive.org) (optional
  suggestions).

### Build

You need the .NET 11 SDK (currently a release candidate) and the MAUI workloads:

```bash
dotnet workload install maui-android maui-windows
```

Android 8.0 (API 26) or later, on a connected device or an emulator:

```bash
dotnet build Canora/Canora.csproj -f net11.0-android -t:Run
```

Windows 10 version 1809 or later (the Windows target is built only on Windows):

```bash
dotnet build Canora/Canora.csproj -f net11.0-windows10.0.19041.0
```

The app is `Canora/bin/Debug/net11.0-windows10.0.19041.0/win-x64/Canora.exe`. You can also open `Canora/Canora.slnx`
in Visual Studio.

### Project layout

```
Canora/                 the .NET MAUI app
	Views/              pages
	ViewModels/
	Services/           catalog, YouTube matching, lyrics, podcasts, local database
	Controls/           reusable views (cards, rows, popups)
	Platforms/          Android and Windows specific code
	Resources/          themes, fonts, icons
docs/screenshots/       the images of this README
```

### License

Can'ora is released under the [PolyForm Noncommercial License 1.0.0](LICENSE.md). You can use, study, modify and share
it for free for any non-commercial purpose (personal use, study, research, schools, non-profit organizations), as long
as you keep the license and the `Required Notice` line that credits the original project.

**Commercial use** requires the author's written permission: [open an issue](https://github.com/amellini/Canora/issues)
to ask for it.

### Disclaimer

- **Educational purpose.** Can'ora is a personal project made for study and teaching: a hands-on example of .NET MAUI
  development on Android and Windows and of how public web services work. It is not a commercial product and is not
  meant to replace any streaming service.
- **Third-party services, including Google's.** The audio is streamed from **YouTube**, a Google service, through the
  YoutubeExplode library, which does not use the official YouTube API. The catalog comes from Deezer, lyrics from
  LRCLIB, podcasts from the iTunes Search API and, optionally, suggestions from ListenBrainz. Can'ora is not affiliated
  with, endorsed or sponsored by Google, YouTube, Spotify, Deezer, Apple, LRCLIB or ListenBrainz; their names and
  trademarks belong to their owners.
- **No improper use.** The project does not encourage or support any use that breaks the law, the rights of content
  owners or the terms of the services above, in particular the YouTube Terms of Service. It does not save or
  redistribute content: audio is only played as a stream. Whoever builds, installs or uses the app is responsible for
  using it lawfully.
- **No warranty.** The software is provided "as is", without any warranty: see the [license](LICENSE.md).
- **Images.** Cover art and artist photos in the screenshots belong to their owners. If you hold the rights to some
  content and want it removed, [open an issue](https://github.com/amellini/Canora/issues).

---

## Italiano

Can'ora è un progetto personale: un player musicale che ha l'aspetto e l'uso di Spotify, senza account da creare. Cerchi
nel catalogo, apri le pagine di artisti e album, ti fai le tue playlist, canti con i testi sincronizzati e segui i
podcast.

Il catalogo (artisti, album, copertine, classifiche) arriva dall'API pubblica di Deezer. L'audio arriva da YouTube: per
ogni brano l'app sceglie il video con la stessa durata.

L'interfaccia è in italiano. Il nome viene da *canora*, come in *voce canora*.

### Download

L'APK per Android e lo zip per Windows sono nelle [Release](https://github.com/amellini/Canora/releases).

- **Android 8.0 o successivo**: apri l'APK e consenti l'installazione da quella fonte quando Android lo chiede.
- **Windows 10 1809 o successivo**: estrai lo zip e avvia `Canora.exe`. L'exe non è firmato, quindi SmartScreen può
  chiedere conferma (*Ulteriori informazioni → Esegui comunque*).

### Funzionalità

- **Ricerca** di artisti, album, brani, playlist e podcast, con le ricerche recenti e le tile dei generi da sfogliare.
- **Pagine artista** con i brani più ascoltati, gli album e i singoli. **Pagine di album e playlist** con riproduci tutto
  e casuale.
- **Home** che ti saluta e ti propone musica: la *scelta settimanale* di brani nuovi dalle radio degli artisti che ascolti
  di più (più le proposte di ListenBrainz, se aggiungi il tuo token), i *mix per genere* e gli *ascoltati di recente*.
- **Libreria** con le tue playlist e i podcast che segui, salvati sul dispositivo (SQLite).
- **Player** colorato come la copertina, con ripeti (tutto o un brano) e **karaoke**: testi sincronizzati da LRCLIB.
  Tocchi una riga e il brano riparte da lì; con il lucchetto lo schermo resta acceso mentre canti.
- **Podcast**: cerchi per titolo, segui un programma e i nuovi episodi compaiono in Libreria. L'ascolto riprende da dove
  eri rimasto, con salti di 15 secondi avanti e indietro.
- **Presentazione al primo avvio**: il tuo nome, i generi e gli artisti preferiti, così le proposte funzionano da subito.
- **Cinque temi**: Classico, Cobalt Sky, Laguna, Warm Autumn Glow e Rustic Charm.
- **Android**: riproduzione in background con la notifica multimediale. Comandi vocali dall'assistente del telefono con
  le AppFunctions di Android 16 (sperimentali), per esempio *"Riproduci &lt;brano&gt; da Can'ora"*.
- **Windows**: un semplice `.exe` non pacchettizzato, navigazione con la barra laterale, pulsanti precedente /
  play-pausa / successivo nell'anteprima della taskbar, liste orizzontali da trascinare con il mouse.

### Com'è fatta

- .NET 11 MAUI: un solo progetto per Android e Windows, CoreCLR, XAML generato a build time e binding compilati.
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [CommunityToolkit.Maui](https://github.com/CommunityToolkit/Maui)
  (MediaElement per la riproduzione).
- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) per la ricerca su YouTube e gli stream audio.
- [sqlite-net-pcl](https://github.com/praeclarum/sqlite-net) per playlist, ascoltati di recente e cronologia degli ascolti.
- [Shiny.AppFunctions](https://github.com/shinyorg/shiny) per i comandi dell'assistente su Android.
- Servizi web gratuiti e senza chiave: [Deezer API](https://developers.deezer.com/api) (catalogo),
  [LRCLIB](https://lrclib.net) (testi), [iTunes Search API](https://performance-partners.apple.com/search-api)
  (podcast), [ListenBrainz](https://listenbrainz.org) e [Cover Art Archive](https://coverartarchive.org) (proposte,
  facoltativi).

### Compilare

Servono l'SDK di .NET 11 (per ora una release candidate) e i workload MAUI:

```bash
dotnet workload install maui-android maui-windows
```

Android 8.0 (API 26) o successivo, su un dispositivo collegato o un emulatore:

```bash
dotnet build Canora/Canora.csproj -f net11.0-android -t:Run
```

Windows 10 versione 1809 o successiva (il target Windows si compila solo su Windows):

```bash
dotnet build Canora/Canora.csproj -f net11.0-windows10.0.19041.0
```

L'app è `Canora/bin/Debug/net11.0-windows10.0.19041.0/win-x64/Canora.exe`. In alternativa apri `Canora/Canora.slnx` in
Visual Studio.

### Struttura

```
Canora/                 l'app .NET MAUI
	Views/              pagine
	ViewModels/
	Services/           catalogo, abbinamento con YouTube, testi, podcast, database locale
	Controls/           viste riusabili (card, righe, popup)
	Platforms/          codice specifico di Android e Windows
	Resources/          temi, font, icone
docs/screenshots/       le immagini di questo README
```

### Licenza

Can'ora è distribuita con la [PolyForm Noncommercial License 1.0.0](LICENSE.md). Puoi usarla, studiarla, modificarla e
condividerla gratuitamente per qualsiasi scopo non commerciale (uso personale, studio, ricerca, scuole, organizzazioni
non profit), a patto di mantenere la licenza e la riga `Required Notice` che cita il progetto di origine.

**L'uso commerciale** richiede il permesso scritto dell'autore: [apri una issue](https://github.com/amellini/Canora/issues)
per chiederlo.

### Avvertenze

- **Scopo didattico.** Can'ora è un progetto personale nato per studio e didattica: un esempio pratico di sviluppo con
  .NET MAUI su Android e Windows e di come funzionano i servizi web pubblici. Non è un prodotto commerciale e non vuole
  sostituire alcun servizio di streaming.
- **Servizi di terze parti, compresi quelli di Google.** L'audio arriva in streaming da **YouTube**, un servizio di
  Google, tramite la libreria YoutubeExplode, che non usa l'API ufficiale di YouTube. Il catalogo arriva da Deezer, i
  testi da LRCLIB, i podcast dall'iTunes Search API e, se attivate, le proposte da ListenBrainz. Can'ora non è affiliata,
  approvata o sponsorizzata da Google, YouTube, Spotify, Deezer, Apple, LRCLIB o ListenBrainz; nomi e marchi appartengono
  ai rispettivi proprietari.
- **Nessun uso improprio.** Il progetto non incoraggia né sostiene alcun uso che violi la legge, i diritti dei titolari
  dei contenuti o i termini dei servizi citati, in particolare i Termini di servizio di YouTube. Non salva né
  ridistribuisce contenuti: l'audio viene solo riprodotto in streaming. Chi compila, installa o usa l'app è responsabile
  di farlo nel rispetto della legge.
- **Nessuna garanzia.** Il software è fornito "così com'è", senza alcuna garanzia: vedi la [licenza](LICENSE.md).
- **Immagini.** Copertine e foto degli artisti negli screenshot appartengono ai rispettivi proprietari. Se detieni i
  diritti su qualche contenuto e vuoi che venga rimosso, [apri una issue](https://github.com/amellini/Canora/issues).

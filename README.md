# Fiddler Web App

Visualizzatore MUL SeaHats: Blazor Web App (.NET 10), Fluent UI Blazor 5 e rendering SkiaSharp.
Una sola applicazione da eseguire, con lettori e grafica riutilizzabili in progetti separati.

## Architettura

```text
FiddlerWebApp ──> Fiddler.Graphics ──> Fiddler.Formats
  Blazor/UI       SkiaSharp           IDX/MUL e pixel ARGB32
```

- `Fiddler.Formats`: apertura read-only delle coppie IDX/MUL, cataloghi, decoder land, static art e gump. Non dipende da SkiaSharp o da framework UI.
- `Fiddler.Graphics`: bitmap, disegno su `SKCanvas`, PNG e miniature. Non dipende da Blazor, HTTP, Windows Forms o System.Drawing.
- `FiddlerWebApp`: sessioni indipendenti, selezione collezione, ricerca ID decimale/esadecimale, catalogo paginato, anteprima e zoom.
- `Fiddler.Tests`: fixture sintetiche e verifica di decoder, indici, isolamento delle sessioni e rendering Skia.

## Avvio

Serve .NET SDK 10. Dalla cartella del repository:

```powershell
dotnet run --project src/FiddlerWebApp -- --Mul:Directory="D:\Dati\Client"
```

Aprire l'indirizzo localhost mostrato dalla console e premere **Apri archivio**.
La cartella è sul computer che esegue l'applicazione, non sul computer del browser.
In alternativa impostare `Mul:Directory` in `appsettings.json` oppure la variabile `Mul__Directory`.
I file di gioco restano esterni al repository e a `wwwroot`.

Coppie supportate (nomi riconosciuti senza distinzione maiuscole/minuscole):

| File | Collezioni |
| --- | --- |
| `artidx.mul` + `art.mul` | Terreni (ID 0–0x3FFF), oggetti (slot IDX meno 0x4000) |
| `gumpidx.mul` + `gumpart.mul` | Gump, dimensioni nel campo extra dell'indice |

Questa prima versione legge i MUL classici: UOP, verdata, hues, mappe, animazioni, importazione e salvataggio non sono ancora implementati.
Gli slot vuoti non vengono mostrati; record presenti ma corrotti vengono segnalati singolarmente.
L'apertura non carica tutto il MUL: legge l'indice, poi solo i record visualizzati (24 per pagina).
Limiti: indice 12 MB, record 16 MiB, immagine 4096 pixel per lato e 4 milioni di pixel totali.

## Riutilizzo del motore grafico

Aggiungere un riferimento a `src/Fiddler.Graphics/Fiddler.Graphics.csproj` nel sito o nello strumento desiderato:

```csharp
using Fiddler.Formats;
using Fiddler.Graphics;

using var archive = MulArchive.Open("artidx.mul", "art.mul");
var image = archive.Read(AssetKind.Item, 0x1234);
var renderer = new SkiaAssetRenderer();
byte[] png = renderer.RenderPng(image);
// Oppure renderer.Draw(canvas, image, destination) su un proprio SKCanvas.
// CreateBitmap trasferisce al chiamante la responsabilità di Dispose della bitmap.
```

La decodifica produce pixel ARGB32 con alpha non premoltiplicato. Il rendering preserva la trasparenza e usa il campionamento nearest-neighbor.
La webapp genera PNG sul server; il browser mostra immagini e applica lo zoom senza interpolazione.
Le dipendenze native Skia per Windows/macOS sono incluse da SkiaSharp; per Linux è incluso `SkiaSharp.NativeAssets.Linux.NoDependencies`.
Il percorso Linux richiede comunque una verifica di esecuzione sul server di destinazione.

## Verifica

```powershell
dotnet build FiddlerWebApp.sln -c Release
dotnet test tests/Fiddler.Tests -c Release
```

Le fixture sono generate nei test e non contengono asset di gioco. Gli archivi sono aperti in sola lettura e chiusi al termine della sessione.
Non esiste uno stato grafico statico condiviso tra utenti.

## Passi successivi

La priorità attuale è il motore MUL e la visualizzazione. Google login, autorizzazioni, pubblicazione e download verranno aggiunti successivamente.
Questa base di sviluppo usa localhost e non deve essere esposta su Internet prima di integrare il controllo accessi.

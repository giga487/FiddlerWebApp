# Fiddler Web App

Gestore degli asset SeaHats: Blazor Web App (.NET 10), Fluent UI Blazor 5 e rendering SkiaSharp.
Una sola applicazione da eseguire, con lettori e grafica riutilizzabili in progetti separati.

## Architettura

```text
FiddlerWebApp ──> Fiddler.Graphics ──> Fiddler.Formats
  Blazor/UI       SkiaSharp           IDX/MUL e pixel ARGB32
```

- `Fiddler.Formats`: contenitori MUL/UOP, decoder, documenti Gump con bozze, confronto ed esportazione. Non dipende da SkiaSharp o da framework UI.
- `Fiddler.Graphics`: bitmap, disegno su `SKCanvas`, PNG e miniature. Non dipende da Blazor, HTTP, Windows Forms o System.Drawing.
- `FiddlerWebApp`: sessioni indipendenti, selezione collezione, ricerca ID decimale/esadecimale, catalogo paginato, anteprima e zoom.
- `Fiddler.Tests`: fixture sintetiche e verifica di decoder, indici, isolamento delle sessioni e rendering Skia.

La divisione per risorsa è descritta in [docs/architecture.md](docs/architecture.md).
`Art`, `Gumps` e `Animations` contengono lettori, decoder e punti di accesso al rendering dedicati.
`Containers/Mul` gestisce soltanto IDX e byte; `Imaging` raccoglie pixel e disegno condivisi.
Mappe e suoni hanno per ora soltanto il riconoscimento dei file nel catalogo.

## Avvio

Serve .NET SDK 10. Dalla cartella del repository:

```powershell
dotnet run --project src/FiddlerWebApp -- --Mul:Directory="D:\Dati\Client"
```

Aprire l'indirizzo localhost mostrato dalla console e premere **Apri archivio**.
È anche possibile aprire **Configurazione**, inserire un percorso assoluto sul server,
premere **Analizza cartella** e poi **Salva cartella**. La selezione salvata prevale
sul valore iniziale di `Mul:Directory` e viene conservata in
`App_Data/client-directory.json` (fuori da `wwwroot`, escluso da Git).
La cartella è comune all'applicazione: gli archivi già aperti in altre sessioni
rimangono attivi finché quelle sessioni non li riaprono.
La cartella è sul computer che esegue l'applicazione, non sul computer del browser.
In alternativa impostare `Mul:Directory` in `appsettings.json` oppure la variabile `Mul__Directory`.
I file di gioco restano esterni al repository e a `wwwroot`.

## Analisi dei file

Il catalogo `Fiddler.Formats/Catalog/UOFiddlerFiles.txt` riprende i 131 nomi di
`Ultima.Files._uoFiles` del fork UOFiddler presente nel workspace. Il catalogo è
incluso nella libreria: la webapp non dipende dall'installazione di UOFiddler.
Aggiornare questo snapshot quando cambia la lista del fork.

Lo scanner esamina solo la cartella indicata e distingue file presenti, assenti,
alternative MUL/UOP, coppie/triple incomplete, file vuoti, indici non allineati e
file non leggibili. Gli statici vuoti e le definizioni opzionali vuote sono ammessi.
I file sconosciuti sono elencati separatamente, senza essere trattati come errori.
I nomi sono confrontati senza distinzione maiuscole/minuscole; eventuali collisioni
su filesystem sensibili al caso vengono segnalate.

I file richiesti dipendono dalla versione del client e dalle funzioni utilizzate:
un file opzionale assente non significa che il client sia danneggiato. Un UOP
completo soddisfa l'inventario della relativa risorsa, ma questa prima versione
del visualizzatore apre le coppie MUL di art, gump e animazioni e i Gump UOP.
L'analisi verifica presenza, leggibilità e struttura di base degli indici;
non è una verifica completa di integrità o compatibilità dei dati.

Coppie supportate (nomi riconosciuti senza distinzione maiuscole/minuscole):

| File | Collezioni |
| --- | --- |
| `artidx.mul` + `art.mul` | Terreni (ID 0–0x3FFF), oggetti (slot IDX meno 0x4000) |
| `gumpidx.mul` + `gumpart.mul` | Gump, dimensioni nel campo extra dell'indice |
| `gumpartLegacyMUL.uop` | Gump UOP v4/v5, stored, zlib e Mythic |
| `anim.idx` + `anim.mul` e `anim2…anim6` | Corpi, azioni e otto direzioni |

Questa versione legge i MUL classici e i Gump UOP. Art/animazioni UOP, verdata, hues e mappe non sono ancora implementati.
Gli slot vuoti non vengono mostrati; record presenti ma corrotti vengono segnalati singolarmente.
L'apertura non carica tutto il MUL: legge l'indice, poi solo i record visualizzati (24 per pagina).
Limiti: indice 12 MB, record 16 MiB, immagine 4096 pixel per lato e 4 milioni di pixel totali.

## Gump e animazioni

Le pagine principali sono `/gump` e `/animazioni`. Nella seconda, premere
**Apri animazioni** e scegliere archivio, corpo, azione e direzione. Sono disponibili
il filtro Animali, la ricerca ID decimale/esadecimale, play/pausa, loop, avanzamento
per frame, cursore temporale, ritardo tra i frame e zoom. Il ritardo iniziale è
150 ms come nel controllo AnimatedPictureBox del fork UOFiddler.

Il motore conserva i pivot originali e specchia le direzioni 5–7. La superficie
comune ai frame evita che l'animale si sposti soltanto perché cambia dimensione
il rettangolo del frame. SkiaSharp genera un atlas PNG sul server; il browser
riproduce i frame in canvas con requestAnimationFrame, senza richieste di rete
per ogni frame e sospendendo il clock quando la pagina non è visibile.

La navigazione usa gli ID fisici del file scelto, come la modalità animX esplicita
di UOFiddler: non applica ancora body.def/bodyconv.def, mobtypes.txt o hues.
Le animazioni UOP non sono ancora supportate. I nomi delle azioni animali e
mostri seguono quelli di Fiddler; per gli altri corpi sono mostrati gli ID azione.
Limiti aggiuntivi: 512 frame, 16 milioni di pixel decodificati per sequenza e
16 milioni di pixel per atlas. Vedere [docs/animation-rendering.md](docs/animation-rendering.md).

## Riutilizzo del motore grafico

Aggiungere un riferimento a `src/Fiddler.Graphics/Fiddler.Graphics.csproj` nel sito o nello strumento desiderato:

```csharp
using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Graphics;

using var archive = ArtMulReader.Open("artidx.mul", "art.mul");
var image = archive.ReadItem(0x1234);
var renderer = new SkiaAssetRenderer();
byte[] png = renderer.RenderPng(image);
// Oppure renderer.Draw(canvas, image, destination) su un proprio SKCanvas.
// CreateBitmap trasferisce al chiamante la responsabilità di Dispose della bitmap.
```

Per i gump usare `Fiddler.Formats.Gumps.GumpMulReader.Open("gumpidx.mul", "gumpart.mul")`
e `Read(id)`. Per disegnare direttamente da un record già letto sono disponibili
`Fiddler.Graphics.Art.ArtRenderer` e `Fiddler.Graphics.Gumps.GumpRenderer`.

La decodifica produce pixel ARGB32 con alpha non premoltiplicato. Il rendering preserva la trasparenza e usa il campionamento nearest-neighbor.
La webapp genera PNG sul server; il browser mostra immagini e applica lo zoom senza interpolazione.
Le dipendenze native Skia per Windows/macOS sono incluse da SkiaSharp; per Linux è incluso `SkiaSharp.NativeAssets.Linux.NoDependencies`.
Il percorso Linux richiede comunque una verifica di esecuzione sul server di destinazione.

## Importazione e confronto Gump

Da **Gump** aprire **Importa e confronta Gump**, oppure usare `/gump/gestione`.
Si possono aggiungere o sostituire Gump da PNG, confrontare un secondo MUL/IDX o
UOP e copiare un ID dal riferimento all'ID scelto nel documento attuale.
Le anteprime sono generate con SkiaSharp e mostrano il risultato effettivo della
conversione a 15 bit RGB e alpha a un bit. Le modifiche sono visibili nel workspace
e possono essere annullate. Il salvataggio genera una nuova coppia MUL/IDX completa
in una sottocartella univoca, senza modificare i file originali.

Il PNG viene caricato dal PC del browser; gli archivi di confronto e la cartella
di esportazione sono percorsi del server. Il confronto lavora sui pixel decodificati,
non sui byte compressi. Distingue uguali, diversi, solo attuale, solo riferimento
e record non leggibili. Dettagli e limiti in [docs/gump-management.md](docs/gump-management.md).

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

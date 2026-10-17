# Moduli del motore Fiddler

Il sistema distingue tre livelli: contenitori binari, risorse del gioco e rendering.
La webapp ospita l'interfaccia e compone i moduli; le librerie rimangono riutilizzabili
da un altro sito, da strumenti desktop o da processi senza interfaccia.

```text
src/
  Fiddler.Formats/
    Containers/
      Mul/
        IndexedMulFile.cs    # record IDX, offset, lunghezze, lettura dei byte
        MulIndexEntry.cs     # metadati grezzi dello slot
    Art/
      ArtMulReader.cs        # cataloghi land/item e conversione ID -> slot
      LandDecoder.cs        # diamante 44 x 44
      StaticArtDecoder.cs   # righe RLE degli oggetti
    Gumps/
      GumpMulReader.cs       # catalogo gump e dimensioni packed nell'indice
      GumpDecoder.cs        # RLE dei gump
      GumpUopReader.cs      # hash dei nomi e prefisso dimensioni UOP
      IGumpArchive.cs       # contratto comune MUL/UOP
      GumpDocument.cs       # bozze, undo ed esportazione
      GumpEncoder.cs        # conversione RGB/alpha e RLE
      GumpComparer.cs       # confronto per ID e pixel
    Animations/
      AnimationMulReader.cs # corpi e azioni nei file anim1–anim6
      AnimationIndexLayout.cs # layout fisico e direzioni
      AnimationDecoder.cs   # palette e RLE dei frame
      AnimationClip.cs      # sequenza e pivot per frame
    Imaging/
      DecodedImage.cs       # pixel ARGB32, senza dipendenze grafiche
      PixelDecoding.cs      # limiti, controlli e conversione colori condivisi
    Catalog/                # inventario trasversale dei file cercati da Fiddler
    Assets.cs               # metadati comuni delle risorse visualizzabili
  Fiddler.Graphics/
    Art/
      ArtRenderer.cs        # PNG e disegno Skia da record land/item
    Gumps/
      GumpRenderer.cs       # PNG e disegno Skia da record gump
      GumpImageImporter.cs  # caricamento PNG e conversione nel formato Gump
    Animations/
      AnimationRenderer.cs # ancoraggio, disegno frame e atlas Skia
      AnimationPlayer.cs   # clock play/pausa, seek e loop riutilizzabile
    Imaging/
      SkiaAssetRenderer.cs  # bitmap, canvas, miniature e PNG da pixel decodificati
  FiddlerWebApp/             # configurazione, navigazione, workspace per sessione
```

## Dipendenze

- `Fiddler.Formats` non dipende da SkiaSharp, ASP.NET o librerie Windows.
- I decoder Art e Gumps condividono solo il modello dei pixel e le primitive di lettura.
- `IndexedMulFile` non interpreta ID Art, dimensioni Gump, animazioni o immagini.
- I lettori delle risorse possiedono e chiudono il proprio contenitore MUL.
- `Fiddler.Graphics` dipende da Formats e SkiaSharp; non dipende dalla webapp.
- Il disegno condiviso rimane in Imaging; le API specifiche di Art e Gumps sono nei rispettivi moduli.
- La UI sceglie il lettore tramite `MulWorkspace` e `AnimationWorkspace` e mostra i risultati senza decodificare formati binari.

Le precedenti classi miste `MulArchive` e `MulImageDecoder` sono sostituite dai
lettori e decoder dedicati. L'API condivisa `SkiaAssetRenderer` rimane disponibile
per i consumatori che possiedono già un'immagine decodificata.

## Aggiunta di altre risorse

| Modulo | Stato | Responsabilità prevista |
| --- | --- | --- |
| Art | Implementato per MUL | Terreni e oggetti statici |
| Gumps | MUL/UOP e bozze | Immagini, importazione, confronto e nuova versione MUL |
| Animations | Implementato per MUL | Frame, direzioni, palette, pivot e temporizzazione |
| Maps | Inventario file | Blocchi terreno, statici e composizione della vista |
| Sounds | Inventario file | Campioni e dati audio; l'output è audio, senza renderer Skia |
| Containers/Uop | Implementato v4/v5 | Indice, hashing, zlib e Mythic; usato dai Gump |

Quando si implementa una risorsa, aggiungere la sua cartella in Formats e,
se produce grafica, la corrispondente cartella in Graphics. I formati futuri
devono riutilizzare i contenitori condivisi senza introdurre logica di risorsa
in questi ultimi. Non sono presenti classi vuote o lettori che fingono di
supportare mappe, suoni o animazioni/Art UOP.

I test verificano le API dedicate di Art e Gumps, il rendering Skia e gli stessi
casi di regressione su trasparenza, offset, troncamenti e isolamento delle sessioni.

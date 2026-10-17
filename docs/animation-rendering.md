# Visualizzazione di Gump e animazioni

La prima implementazione è dedicata a Gump e animazioni MUL. Formats,
Graphics e webapp rimangono progetti separati.

## Comportamento verificato nel fork

File di UOFiddler esaminati:

- `Ultima/Animations.cs`: GetFileIndex, GetActionCapacity, modalità esplicita animX e AnimationFrame.
- `UoFiddler.Controls/UserControls/AnimationListControl.cs`: corpo, azione, direzione e nomi delle azioni.
- `UoFiddler.Controls/UserControls/AnimatedPictureBox.cs`: timer iniziale di 150 ms e posizionamento.
- `UoFiddler.Controls/Classes/AnimatedFrameListExtensions.cs`: superficie comune e ancoraggio.

Le sequenze MUL contengono palette di 256 colori, tabella di offset dei frame,
centri X/Y firmati e run di indici palette. Gli offset sono relativi alla posizione
del conteggio frame, dopo i 512 byte della palette. I run usano coordinate firmate
a 10 bit, lunghezza a 12 bit e terminatore 0x7FFF7FFF.

Le cinque direzioni memorizzate diventano otto: 5, 6 e 7 riutilizzano 3, 2 e 1,
specchiando pixel e centro orizzontale. Il layout IDX segue i sei archivi,
incluso il caso body 34 in anim5. La capacità fisica delle azioni impedisce
di leggere nel blocco del corpo successivo.

## Pivot e disegno SkiaSharp

Il rettangolo di ogni frame rispetto all'ancora ha left = -CenterX e
top = -CenterY - Height. L'unione dei rettangoli determina la superficie comune.
Ogni frame mantiene la stessa ancora, anche se cambiano larghezza e altezza.
La specchiatura usa CenterX = Width - CenterX, come Fiddler.

`AnimationRenderer.DrawFrame` disegna su qualsiasi SKCanvas. `AnimationPlayer`
mantiene frame corrente, play/pausa, seek e loop. L'host chiama `Advance` con
il tempo trascorso e `Draw` con il canvas: nessuna dipendenza da timer Windows,
Blazor o HTTP. Il ritardo iniziale è 150 ms, modificabile.

## Riproduzione web

SkiaSharp genera sul server un atlas PNG con celle allineate. Il componente
invia l'atlas una volta per sequenza. Il canvas del browser riproduce le celle
con requestAnimationFrame: SkiaSharp non gira in WebAssembly e non vengono
trasferite immagini a ogni tick. I controlli di riproduzione operano localmente.
Il clock si sospende nelle schede nascoste; RAF e listener vengono rimossi
quando si cambia pagina o sequenza. Lo zoom usa nearest-neighbor.

## Limiti e verifica

La scelta dell'archivio espone gli ID fisici, come la modalità esplicita animX
di Fiddler. Non sono ancora applicati body.def, bodyconv.def, mobtypes.txt, hues,
verdata, animazioni UOP o composizione corpo/equipaggiamento. I Gump sono letti
da MUL/IDX e UOP, con importazione e confronto descritti in gump-management.md.

I test usano record sintetici e verificano palette, alpha, coordinate firmate,
pivot positivi e negativi, specchiatura, layout dei sei file, indici sparsi,
record troncati, atlas e clock. La verifica nel browser usa piccoli animali
sintetici e copre anche assenza di richieste HTTP per frame, pausa, loop, seek,
cambio archivio, velocità, direzione specchiata, navigazione Gump e layout mobile.
Rimane da verificare il comportamento con i dati reali del client e sul server Linux.

# Importazione e confronto Gump

La pagina `/gump/gestione` usa il documento corrente del workspace. Le modifiche
restano separate dall'archivio originale fino al salvataggio di una nuova versione.

## Flusso

1. Aprire i Gump dalla cartella configurata, oppure arrivare dal visualizzatore scegliendo un ID.
2. Specificare l'ID di destinazione, in decimale o con prefisso 0x.
3. Caricare un PNG dal computer del browser e controllare l'anteprima convertita.
4. Premere Aggiungi o Sostituisci nella bozza. Le modifiche appaiono anche nel visualizzatore.
5. Facoltativamente, aprire un archivio di confronto con percorso assoluto sul server.
6. Selezionare un risultato per le anteprime affiancate. È possibile copiare il riferimento nello stesso ID o in un ID diverso.
7. Salvare una nuova coppia MUL/IDX completa e, se desiderato, aprirla nel workspace.

Le bozze sono legate alla sessione Blazor: sopravvivono alla navigazione interna,
ma occorre salvare una versione prima di chiudere o ricaricare completamente la pagina.
La riapertura del workspace e il cambio di configurazione sono bloccati se ci sono
bozze. Sono disponibili annullamento per ID e scarto di tutte le bozze.

## Contenitori

- MUL: gumpart.mul più gumpidx.mul; per un MUL di confronto con indice dal nome diverso si specifica il percorso dell'indice.
- UOP: v4 e v5; i Gump sono riconosciuti tramite gli hash canonici build/gumpartlegacymul/00000000.tga, indipendentemente dal nome esterno del file.
- Compressioni: 0 (stored), 1 (zlib), 3 (zlib più Mythic). Le dimensioni sono i primi due int32 del payload decompresso.
- Se la cartella corrente contiene entrambi i formati, viene scelto l'UOP, come in UOFiddler.
- I Gump UOP oltre il vecchio limite 65535 sono riconosciuti fino a 0x11FFF.

Hash e decoder Mythic sono adattati dalle implementazioni del fork UOFiddler
Ultima/Helpers/UopUtils.cs e Ultima/Helpers/MythicDecompress.cs. Il codice è
incluso nel progetto Formats e non dipende dall'applicazione desktop.

## Conversione PNG

Il PNG è verificato prima della decodifica. Sono ammessi fino a 8 MiB, 4096 pixel
per lato e 4 milioni di pixel. Il formato Gump usa RGB a 15 bit e alpha a un bit:
alpha minore di 128 diventa trasparente, gli altri pixel diventano opachi.
Il nero opaco quantizzato viene mappato a RGB 8,8,8 per evitare il valore zero,
riservato alla trasparenza. L'anteprima mostra già queste conversioni.

## Confronto

Il confronto scorre l'unione degli ID e decodifica una risorsa alla volta.
La compressione o una diversa organizzazione dei run RLE non rendono diversi
due Gump con gli stessi pixel e dimensioni. I colori dei pixel completamente
trasparenti non influenzano il risultato. Gli errori di lettura sono riportati
per ID senza interrompere tutto il confronto. Sono disponibili filtro delle
differenze, paginazione, avanzamento e annullamento dell'operazione.

Importare, copiare o annullare una modifica invalida i risultati precedenti;
occorre ricalcolare il confronto per analizzare lo stato aggiornato.

## Salvataggio

Il salvataggio scrive l'intero documento in una nuova sottocartella della destinazione
scelta sul server. Conserva i record non modificati e include le modifiche in bozza.
L'indice contiene almeno 65536 righe e viene esteso per gli ID superiori.
I due file sono costruiti in una cartella temporanea privata e pubblicati insieme
spostando la cartella completata. Annullamenti ed errori non pubblicano coppie parziali.
Non è prevista in questa versione la sovrascrittura degli originali o l'uscita UOP.

Limiti: 512 modifiche, 16 milioni di pixel e 64 MiB di record modificati per sessione;
record MUL fino a 16 MiB, payload UOP fino a 32 MiB, 200000 record UOP complessivi.
Le bozze e il confronto appartengono alla sessione e non vengono condivisi tra utenti.

## Verifica

I test coprono importazione PNG, quantizzazione e alpha, inserimento/sostituzione,
undo, confronto delle cinque categorie, UOP v4/v5 con tutte le compressioni,
file rinominati, ID elevati, input corrotti, annullamento e riapertura del risultato.
La prova nel browser usa archivi sintetici e verifica anche copia verso un altro ID,
navigazione con bozze, protezione dalla riapertura involontaria, originali invariati,
salvataggio e anteprima della nuova versione. I dati reali richiedono ancora verifica.

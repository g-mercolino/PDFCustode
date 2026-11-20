# Configurazione e collaudo SharePoint

Il backend proietta su SharePoint le attivazioni salvate nella cartella privata PDFCUSTODE_DATA. La connessione usa Microsoft Graph v1.0 ed Entra ID con certificato oppure Managed Identity. Il dominio configurato è innowatedev.sharepoint.com. L'app MAUI continua a contattare soltanto il servizio licenze.

## Prima di collegare il tenant

L'app registration non è stata ancora creata. Occorrono Tenant ID GUID, Client ID GUID, un certificato utilizzabile dal processo server, il Site ID composto e i GUID delle tre liste. Un account amministratore deve approvare i permessi. Il dominio SharePoint non sostituisce questi identificativi. Nessun test automatico locale scrive sul tenant.

In Entra admin center aprire Identity, Applications, App registrations e New registration. Creare un'app single-tenant chiamata PDF Custode Backend, senza redirect URI. Annotare Application client ID e Directory tenant ID.

Per il collaudo locale Windows eseguire in PowerShell lo script `scripts/New-SharePointCertificate.ps1`. Esporta soltanto un certificato pubblico `.cer` nella cartella temporanea e stampa il thumbprint. Caricare il `.cer` in App registration, Certificates and secrets, Certificates, Upload certificate. La chiave privata rimane non esportabile nello store del PC. Questo certificato autentica il servizio a Entra ed è separato dalla chiave che firma le licenze PDF. Pianificare il rinnovo prima della scadenza di un anno. Per un Windows Service usare lo store/account corretti e assegnare al processo il diritto di usare la chiave.

In API permissions aggiungere Microsoft Graph, Application permissions, `Lists.SelectedOperations.Selected`, quindi ottenere Grant admin consent. La sola approvazione non basta: serve anche un grant `write` su ciascuna delle tre liste. Il backend non necessita di Sites.ReadWrite.All sull'intero tenant.

Un amministratore con i permessi delegati necessari può usare Graph Explorer per individuare il sito:

```http
GET https://graph.microsoft.com/v1.0/sites/innowatedev.sharepoint.com
GET https://graph.microsoft.com/v1.0/sites/SITE-ID/lists?$select=id,displayName
```

Per il Communication site degli screenshot il percorso sembra essere la radice. Se il sito effettivo è sotto `/sites/NOME`, usare `GET /sites/innowatedev.sharepoint.com:/sites/NOME`. Copiare l'intero `id` del sito, che contiene hostname e due GUID. In alternativa il GUID di una lista si vede nell'URL di List settings, parametro List, decodificando le parentesi.

Il grant va eseguito dall'amministratore separatamente per PDF_Distribuzioni, PDF_Licenze e PDF_Log:

```http
POST https://graph.microsoft.com/v1.0/sites/SITE-ID/lists/LIST-ID/permissions
Content-Type: application/json

{
  "roles": ["write"],
  "grantedToV2": {
    "application": { "id": "CLIENT-ID-BACKEND", "displayName": "PDF Custode Backend" }
  }
}
```

Graph Explorer opera con i privilegi dell'amministratore, diversi da quelli limitati assegnati al backend. Non copiare il suo token nella configurazione del servizio. Per l'identità di bootstrap il tenant deve autorizzare i permessi di gestione risorse richiesti dalla documentazione Microsoft. La raccolta PDF_Pacchetti si può popolare manualmente dal browser e non richiede accesso dal backend per questo flusso.

## Verificare le colonne

Prima del primo sync impostare valori univoci e indice sulle colonne `DistribuzioneId` in PDF_Distribuzioni, `CodiceHash` in PDF_Licenze ed `EventId` in PDF_Log. Questi vincoli consentono il recupero dopo richieste interrotte senza creare doppioni. Eliminare o correggere eventuali righe di prova duplicate prima di attivarli. Le lookup `Distribuzione` delle ultime due liste devono puntare a PDF_Distribuzioni ed essere singole.

Le colonne devono corrispondere allo schema concordato in chat. Il comando check controlla colonne e tipi e risolve il nome interno dal nome visualizzato. DataInizio, DataFine e DataEvento devono essere Data e ora. DataFine deve ricevere ExpiresAt firmata, non una formula locale. `LicenzeDisponibili` resta una colonna calcolata di SharePoint e non viene scritta. Conserveremo nome/email destinatario, note e piattaforma inseriti dagli operatori. Il lettore attuale non raccoglie l'email dell'utente, quindi il server non può inventarla. Stato richiede Disponibile, Assegnata, Attiva, Scaduta, Sostituita. TipoEvento richiede CodiceGenerato, CodiceAssegnato, Attivazione, Rinnovo, Sostituzione, ErroreTecnico. Esito richiede Successo, Rifiutato, Errore.

## Preparare tre codici dello stesso documento

Dalla radice della repository usare una cartella nuova. Si può usare il PDF demo già presente:

```powershell
dotnet run --project Packager -- prepare demo-originale.pdf Admin/collaudo-sharepoint
dotnet run --project Packager -- issue Admin/collaudo-sharepoint/server
dotnet run --project Packager -- issue Admin/collaudo-sharepoint/server
```

Usare il nuovo `Admin/collaudo-sharepoint/client/documento.pdfcustode` con una build del lettore che supporta biblioteca e scadenza. DocumentId è nel file `Admin/collaudo-sharepoint/client/document.json`. I nuovi codici sono mantenuti nel registro amministrativo per poterli mostrare nella colonna riservata CodiceAttivazione. I vecchi codici che avevano soltanto l'hash potrebbero non essere recuperabili in chiaro. Il registro resta la fonte autorizzativa: digitare un codice inventato soltanto in SharePoint non lo rende valido.

## Configurare il backend

Copiare `Server/appsettings.Local.example.json` in `Server/appsettings.Local.json`, escluso da Git, e compilare tutti i placeholder. Nell'array Distributions usare il DocumentId reale, `DIST-TEST-001`, il titolo, cliente e un totale iniziale pari a 3. Per una riga già presente su SharePoint il totale si conserva; modificarlo direttamente nella lista. Una sola distribuzione per DocumentId è gestita in questa versione. Per assegnare lo stesso contenuto a clienti con monte licenze distinto preparare pacchetti distinti con DocumentId differente.

```powershell
$env:PDFCUSTODE_DATA = (Resolve-Path Admin/collaudo-sharepoint/server).Path
dotnet run --project Server --no-launch-profile -- --sharepoint-check
dotnet run --project Server --no-launch-profile -- --sharepoint-sync
dotnet run --project Server --no-launch-profile -- --urls http://localhost:5187
```

Il comando check legge schema e autorizzazioni di lettura senza inserire righe e verifica che i DocumentId configurati corrispondano ai documenti locali. Il comando sync verifica anche le scritture e crea/aggiorna distribuzioni, licenze e log. Lo stesso sync viene ripetuto dal servizio ogni 30 secondi. Se PDFCUSTODE_DATA punta a tutta Admin, configurare una mappatura per ciascun documento contenuto; per il primo test usare solo la sottocartella server indicata sopra. Per test da tablet nella LAN usare `--urls http://0.0.0.0:5187`, consentire la porta nella sola rete privata e inserire nell'app l'IP del PC. In produzione usare HTTPS. `--no-launch-profile` evita URL divergenti della configurazione Visual Studio.

Le nuove attivazioni leggono il totale dalla distribuzione SharePoint prima di consumare una licenza. Se SharePoint è indisponibile non emettono nuove licenze e restituiscono 503; i retry di licenze già firmate possono recuperare la risposta locale. File lock locale impedisce doppio consumo e superamento del limite da richieste concorrenti. Nessun reintegro automatico alla scadenza. Il rinnovo con nuovo codice consuma un'altra posizione per il collaudo. Queste regole di conteggio vanno confermate dal responsabile. Distribuire un solo servizio su un'unica cartella dati durevole, non istanze con registri locali indipendenti.

In Azure è disponibile Authentication=ManagedIdentity. Per identità user-assigned compilare ManagedIdentityClientId, per system-assigned lasciarlo vuoto. I permessi Graph e i grant sulle tre liste devono essere assegnati anche al service principal della Managed Identity.

## Prove di accettazione

| Prova | Risultato atteso |
| --- | --- |
| Check con impostazioni corrette | Schema e accesso verificati |
| Primo sync con tre codici | Una distribuzione, tre licenze disponibili, log di rilevamento codici |
| Prima attivazione | Riga Attiva, LicenseId e dispositivo presenti, DataFine esattamente 30 giorni dopo DataInizio; consumate 1, disponibili 2 dopo sync |
| Ripetizione sullo stesso dispositivo | Stessa firma, medesima scadenza, consumate ancora 1 |
| Stesso codice su un altro dispositivo | Rifiuto 403 e log; nessun consumo aggiuntivo |
| Codice errato o documento diverso | Rifiuto e log correlati da RequestId |
| Due dispositivi con ultimo posto disponibile | Una sola nuova licenza; totale mai superato |
| SharePoint scollegato e poi ripristinato | Nuove attivazioni 503, eventi locali conservati, invio successivo senza duplicati EventId |
| Lettore già attivato offline | Consultazione fino alla scadenza firmata |
| Scadenza | Lettore bloccato; Stato su SharePoint aggiornato dal sync sulla base di DataFine |
| Modifica manuale destinatario | Nome/email e note conservati dal sync |
| Richiesta malformata, troppo grande, limite richieste | Risposte 400/413/429 e log; nessun codice o token in Dettagli |

Eseguire `dotnet run --project Tests` per i test locali con Graph simulato. Dopo `dotnet build Tests -c Release` eseguire anche `pwsh -File Tests/HttpSmoke.ps1` per il collaudo HTTP su porta temporanea con dati sintetici (richiede PowerShell 7). I test crittografici simulano il tempo senza cambiare l'orologio del PC e verificano il limite esatto dei 30 giorni. Le prove locali non dimostrano l'accesso al tenant reale o il funzionamento su ogni dispositivo.

## Log e recupero

`PDFCUSTODE_DATA/.audit` contiene la cronologia locale giornaliera, gli eventi pending e i ricevuti di consegna. Proteggere la cartella con i permessi dell'account servizio, includerla nei backup e monitorare spazio e crescita. Non viene cancellata automaticamente: concordare conservazione e archiviazione prima dell'uso continuativo. Gli eventi remoti sono deduplicati con EventId. Un errore dopo l'inserimento remoto e prima dell'ack locale viene recuperato al tentativo successivo. Non cancellare pending per sbloccare il servizio.

Si registrano avvio/arresto, richieste ricevute/completate, esiti attivazione, rifiuti, quota esaurita, codici rilevati, errori e ripristino sincronizzazione. Per le richieste HTTP si registrano percorso normalizzato, metodo, codice esito, durata e RequestId. Un codice rilevato durante il primo sync potrebbe essere stato creato in precedenza: il log indica l'ora di rilevamento, non inventa quella storica. Non vengono registrati body delle richieste, codici in chiaro, token, chiavi o contenuto PDF. Aperture offline, modifiche manuali delle righe SharePoint e click dell'utente non transitano dal backend: per le modifiche amministrative usare anche Version history della lista. La coda non riceve conferma di arresti forzati che interrompono il processo prima della scrittura.

## Fonti Microsoft verificate al 20 novembre 2026

- [Permessi Selected sulle liste](https://learn.microsoft.com/en-us/graph/permissions-selected-overview)
- [Grant applicativo su lista](https://learn.microsoft.com/en-us/graph/api/list-post-permissions?view=graph-rest-1.0)
- [Client credentials e certificati](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow)
- [Throttling e Retry-After](https://learn.microsoft.com/en-us/graph/throttling)
- [Aggiornamento condizionato con ETag](https://learn.microsoft.com/en-us/graph/api/listitem-update?view=graph-rest-1.0)
- [ACS ritirato dal 2 aprile 2026](https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/retirement-announcement-for-azure-acs)

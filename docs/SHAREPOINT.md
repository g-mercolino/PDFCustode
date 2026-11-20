# Configurazione e collaudo SharePoint

## Regole aggiornate

Il backend conserva codici e licenze firmate nell'archivio privato `PDFCUSTODE_DATA`. SharePoint contiene il totale annuale amministrato dagli operatori, le attivazioni completate e i log. Il lettore contatta solo il backend: non contiene credenziali SharePoint e non richiede al destinatario un accesso aziendale.

- `PDF_Distribuzioni`: una riga per anno, con totale, impegnate e disponibili, condivisa tra i documenti configurati.
- `PDF_Licenze`: una riga per codice effettivamente attivato. Generare o inviare un codice non impegna una licenza.
- `PDF_Log`: eventi del servizio, richieste ed esiti, con identificativi per correlazione e deduplicazione.
- Il modulo raccoglie **Nome, Cognome e CodiceUtente**; nome macchina e piattaforma sono automatici. CodiceUtente è distinto dal codice di attivazione.
- `DataFine = 31/12/2100` è una convenzione amministrativa. La licenza firmata resta valida **30 giorni** e il lettore ne verifica la scadenza anche offline, come confermato dall'utente.
- Nessuna revoca o sostituzione remota automatica. La scadenza tecnica non restituisce automaticamente un posto al monte licenze. Un nuovo codice attivato impegna un'altra posizione.

## Dove annotare Client ID e Tenant ID

**Non serve avviare l'app MAUI, l'APK o il backend per copiare gli identificativi.**

1. Aprire [Microsoft Entra admin center](https://entra.microsoft.com/).
2. Selezionare il tenant aziendale e aprire **Entra ID → App registrations / Registrazioni app → All applications → PDF Custode Backend**.
3. In **Overview / Panoramica** copiare **Application (client) ID** e **Directory (tenant) ID**. Sono due GUID. Object ID è diverso e non va usato. `innowatedev.sharepoint.com` non è il Tenant ID.
4. Aprire `Server/appsettings.Local.json` nel progetto. È predisposto sul PC ed escluso da Git. Su un clone nuovo copiarlo da `Server/appsettings.Local.example.json`.
5. Sostituire i placeholder e salvare:

```json
"TenantId": "GUID copiato da Directory (tenant) ID",
"ClientId": "GUID copiato da Application (client) ID"
```

I valori appartengono alla configurazione del **backend**, non al modulo dell'app o alle liste. Il file locale iniziale mantiene `Enabled=false`: impostare `true` dopo aver compilato anche certificato, sito, liste e documento e ottenuto i permessi. Dopo modifiche alla configurazione riavviare il backend, se era già in esecuzione.

```powershell
Set-Location 'C:\Users\GuglielmoMercolino\source\repos\Innowate\progetti\PdfCustode'
notepad .\Server\appsettings.Local.json
```

Riferimento: [registrazione app e identificativi Microsoft](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app).

## Certificato e autorizzazioni

**I due ID da soli non bastano.** L'identità tecnica è la registrazione applicativa Entra, senza password utente memorizzata. Per il collaudo Windows si usa un certificato; per un servizio Azure è supportata Managed Identity.

1. Dalla radice del progetto eseguire `scripts/New-SharePointCertificate.ps1` con lo stesso account Windows che eseguirà il backend. Lo script crea una chiave privata non esportabile in `CurrentUser\My`, esporta il solo certificato pubblico `.cer` e stampa `CertificateThumbprint`. Se il file pubblico esiste già, scegliere un nuovo `-PublicCertificatePath` oppure riutilizzare il certificato valido già registrato.
2. In **PDF Custode Backend → Certificates & secrets → Certificates → Upload certificate**, caricare il `.cer` e inserire il thumbprint nel file locale. Non caricare chiavi private o i file di firma delle licenze. Per un Windows Service configurare account/store e diritto di usare la chiave. Il certificato di collaudo dura un anno: pianificarne il rinnovo.
3. In **API permissions → Add a permission → Microsoft Graph → Application permissions** aggiungere `Lists.SelectedOperations.Selected` e ottenere **Grant admin consent**.
4. Un amministratore deve assegnare all'app il ruolo `write` su **ciascuna delle tre liste**. Il solo consenso Entra non concede accesso alle liste: vedere [permessi Selected Microsoft](https://learn.microsoft.com/en-us/graph/permissions-selected-overview).

L'amministratore può individuare sito e liste con [Graph Explorer](https://developer.microsoft.com/en-us/graph/graph-explorer), usando il proprio accesso autorizzato:

```http
GET https://graph.microsoft.com/v1.0/sites/innowatedev.sharepoint.com
GET https://graph.microsoft.com/v1.0/sites/SITE-ID/lists?$select=id,displayName
```

Gli screenshot mostrano il sito radice. Se il sito effettivo è sotto `/sites/NOME`, usare `GET /sites/innowatedev.sharepoint.com:/sites/NOME`. `SiteId` è l'intero ID composto da hostname e due GUID. Copiare i GUID delle tre liste in `DistributionListId`, `LicenseListId` e `LogListId`. Il GUID di una lista è anche nel parametro `List` dell'URL di List settings, decodificando i caratteri URL.

Per ognuna delle tre liste, l'amministratore esegue il grant:

```http
POST https://graph.microsoft.com/v1.0/sites/SITE-ID/lists/LIST-ID/permissions
Content-Type: application/json

{
  "roles": ["write"],
  "grantedToV2": {
    "application": { "id": "APPLICATION-CLIENT-ID-DEL-BACKEND", "displayName": "PDF Custode Backend" }
  }
}
```

L'identità che esegue i grant richiede privilegi amministrativi ulteriori, distinti da quelli limitati del backend; vedere [grant su lista Microsoft](https://learn.microsoft.com/en-us/graph/api/list-post-permissions?view=graph-rest-1.0). Non assegnare al backend i permessi generali dell'amministratore e non copiarne il token nella configurazione. `PDF_Pacchetti` viene popolata manualmente e non richiede un grant al backend in questo flusso.

## Correzioni alle liste esistenti

Non ricreare le liste. Usare **Settings → List settings → Columns**. Nascondere una colonna dalla vista non ne cambia l'obbligatorietà.

### PDF_Distribuzioni

| Colonna | Tipo e impostazioni |
| --- | --- |
| Title | Testo su una riga; es. Licenze 2026 |
| Anno | Numero, zero decimali, valori univoci e indice; es. 2026 |
| LicenzeTotali | Numero, zero decimali, maggiore o uguale a zero; amministrato dagli operatori |
| LicenzeConsumate | Numero, zero decimali, scritto dal servizio. Il nome visualizzato può essere rinominato LicenzeImpegnate; il backend accetta entrambi |
| LicenzeDisponibili | Calcolata `=[LicenzeTotali]-[LicenzeConsumate]`, risultato Numero, zero decimali. Adeguare la formula se si rinomina la colonna |
| Note | Più righe di testo, Plain text, facoltativa |

`DistribuzioneId`, `Cliente`, `DocumentId`, `VersioneDocumento` non sono più usati. Se ancora presenti renderli facoltativi prima di nasconderli; conservare eventuali dati storici. Non creare una riga per ciascun documento dello stesso anno. Se manca la riga corrente viene creata con `InitialLicenseTotal`; se esiste si conserva `LicenzeTotali` della lista.

### PDF_Licenze

| Colonna | Tipo e impostazioni |
| --- | --- |
| Title | Testo su una riga; riferimento creato dal backend |
| Nome, Cognome, **CodiceUtente**, NomeMacchina | Quattro colonne separate, Testo su una riga. **Aggiungere CodiceUtente se manca** |
| CodiceHash | Testo su una riga, valori univoci e indice |
| CodiceAttivazione | Testo su una riga, facoltativa; accesso riservato agli operatori |
| LicenseId, DeviceKeyHash | Testo su una riga |
| Stato | Scelta singola; mantenere i valori già creati, incluso Attiva. Le modifiche amministrative successive sono conservate |
| DataInizio | Data e ora, primo rilascio della licenza firmata |
| DataFine | Data e ora modificabile; il backend scrive 31/12/2100. Non usare una colonna calcolata DataInizio+30 |
| Piattaforma | Testo su una riga oppure Scelta singola con Windows e Android |
| ScadenzaTecnica | **Facoltativa**, Data e ora; se creata riceve la vera scadenza firmata a 30 giorni |
| Note | Più righe di testo, Plain text, facoltativa |

Se esiste ancora la lookup `Distribuzione`, renderla facoltativa e nasconderla. NomeDestinatario ed EmailDestinatario non sono più usati. Non inserire nuove righe di codici disponibili: i codici vengono emessi dal preparatore e la riga viene creata all'attivazione. Scrivere un codice inventato nella lista non lo rende valido.

Per attivazioni storiche senza profilo il servizio non inventa nome/cognome/codice utente; gli operatori possono completarli nella lista. Le vecchie righe di prova mai attivate vanno archiviate/rimosse manualmente dopo verifica: il nuovo sync non cancella righe esistenti.

La vista Attive resta utile. Disponibili sarà vuota per i nuovi codici. Scadute, se basata sul solo Stato, riflette una classificazione amministrativa. Per vedere le scadenze effettive creare ScadenzaTecnica e una vista filtrata sulla data: il servizio non imposta Stato=Scaduta dopo 30 giorni.

### PDF_Log e permessi

Mantenere lo schema esistente. `EventId`: Testo su una riga, univoco e indicizzato. `DataEvento`: Data e ora, ordinata decrescente. `Dettagli`: Più righe di testo, Plain text. TipoEvento mantiene CodiceGenerato, CodiceAssegnato, Attivazione, Rinnovo, Sostituzione, ErroreTecnico; Esito Successo, Rifiutato, Errore. La lookup Distribuzione, se presente, è singola e punta a PDF_Distribuzioni; deve essere facoltativa per gli eventi senza anno associabile.

Conservare colonne standard e cronologia versioni. Gli operatori e il backend gestiscono le liste; i destinatari non devono poter modificarle. I test automatici locali non scrivono sulle liste reali.

## Preparazione e avvio del collaudo

Da PowerShell nella radice del progetto usare una **cartella nuova**, per non sovrascrivere precedenti test:

```powershell
dotnet run --project Packager -- prepare demo-originale.pdf Admin/collaudo-annuale-01
dotnet run --project Packager -- issue Admin/collaudo-annuale-01/server
dotnet run --project Packager -- issue Admin/collaudo-annuale-01/server
```

Importare nel lettore `Admin/collaudo-annuale-01/client/documento.pdfcustode`. Il DocumentId è in `Admin/collaudo-annuale-01/client/document.json`. Completare il file locale, ad esempio:

```json
"QuotaYear": 2026,
"InitialLicenseTotal": 3,
"DocumentIds": ["DOCUMENT-ID-REALE"]
```

QuotaYear è una scelta amministrativa: nessun rinnovo automatico il primo gennaio. Le nuove attivazioni impegnano l'anno configurato; le precedenti mantengono il proprio anno. Per gli stati legacy si usa l'anno UTC di IssuedAt firmato. Per gli anni storici deve già esistere la relativa riga annuale: il backend non inventa totali passati.

Completare gli altri placeholder, ottenere i permessi e impostare `Enabled=true`. Poi:

```powershell
$env:PDFCUSTODE_DATA = (Resolve-Path Admin/collaudo-annuale-01/server).Path
dotnet run --project Server --no-launch-profile -- --sharepoint-check
```

Il check legge e verifica schema e accesso senza creare righe. Non dimostra da solo il permesso di scrittura. Correggere eventuali colonne/indici mancanti e ripetere. Dopo il check:

```powershell
dotnet run --project Server --no-launch-profile -- --sharepoint-sync
dotnet run --project Server --no-launch-profile -- --urls http://localhost:5187
```

Il primo comando sincronizza e termina; il secondo avvia il backend e va lasciato aperto durante l'attivazione. La sincronizzazione periodica avviene ogni 30 secondi. Aprire poi il **lettore aggiornato**, importare il pacchetto e inserire Nome, Cognome, CodiceUtente, indirizzo e codice di attivazione.

Per un tablet fisico nella LAN avviare con `--urls http://0.0.0.0:5187`, autorizzare la porta sulla rete privata e inserire nell'app l'IP del PC, es. `http://192.168.1.21:5187`. Localhost sul tablet indica il tablet. HTTP richiede una build di collaudo `AllowInsecureTestServer=true`; per l'uso pubblico serve HTTPS. Il backend deve raggiungere Entra/Graph per nuove attivazioni; dopo l'attivazione il lettore può stare offline fino alla scadenza tecnica.

## Prove di accettazione

| Prova | Risultato atteso |
| --- | --- |
| Primo sync con tre codici emessi | Una riga annuale, impegnate 0 e disponibili 3; nessuna nuova riga in PDF_Licenze |
| Prima attivazione con profilo valido | Una riga Attiva con profilo, macchina, piattaforma e LicenseId; impegnate 1 e disponibili 2 dopo sync |
| Date | DataInizio corretta, DataFine 31/12/2100; ScadenzaTecnica, se presente, a 30 giorni |
| Ripetizione dopo risposta persa sullo stesso dispositivo | Stessa licenza e scadenza, nessun consumo aggiuntivo |
| Stesso codice su altro dispositivo | Rifiuto, nessun consumo aggiuntivo |
| Profilo mancante/invalido su nuova attivazione | 400, nessuna emissione o consumo |
| Due attivazioni con ultimo posto, anche per PDF diversi | Una sola nuova licenza |
| SharePoint indisponibile | Nuove emissioni bloccate; risposte già firmate recuperabili e log conservati localmente |
| Ripristino e sync ripetuti | Nessun doppione LicenseId/EventId; contatori coerenti |
| Modifiche manuali Note, Stato, LicenzeTotali | Valori amministrativi conservati |
| Lettore offline già attivato | Lettura/ricerca fino alla scadenza; blocco dopo 30 giorni, senza restituire il posto annuale |
| Pacchetto e licenza copiati su altro dispositivo | Il file cifrato è trasferibile ma la licenza del primo dispositivo non permette la lettura sul secondo. Un codice nuovo autorizza una nuova attivazione e impegna un posto |

Controlli locali con dati sintetici e Graph simulato:

```powershell
dotnet build Tests -c Release
dotnet run --project Tests -c Release --no-build
pwsh -File Tests/HttpSmoke.ps1
```

I test simulano il tempo senza cambiare l'orologio del PC. Non dimostrano l'accesso al tenant reale. Per più documenti mantenere un'unica radice privata con tutti i rispettivi server e configurare tutti i DocumentId.

## Log, archivio e limiti operativi

`PDFCUSTODE_DATA/.audit` contiene cronologia giornaliera, eventi pending e conferme. Si registrano avvio/arresto, richieste ricevute/completate, esiti, rifiuti, quota esaurita, codici rilevati, attivazioni persistite, errori e ripristino sync. RequestId correla le richieste; EventId evita duplicazioni. I profili sono nella registrazione dell'attivazione; non si riversano i body nei log e non si registrano codici in chiaro, token, chiavi o PDF. CodiceAttivazione nella lista amministrativa è distinta dai log e richiede accesso riservato.

Non cancellare stato o pending per sbloccare il servizio. Proteggere e includere nei backup tutta la radice privata, compresi profili e anni di allocazione, e monitorare crescita/conservazione dei log. Usare un solo servizio con archivio durevole comune: repliche con registri indipendenti richiederebbero un database transazionale condiviso. Le aperture offline e i click non transitano dal backend; per modifiche manuali usare la cronologia SharePoint. Un arresto forzato può impedire la scrittura dell'ultimo evento.

In Azure è supportato Authentication=ManagedIdentity: valorizzare ManagedIdentityClientId per una user-assigned, lasciarlo vuoto per una system-assigned. Anche la Managed Identity richiede permesso Graph e grant sulle liste.

## Fonti Microsoft

- [Registrazione app e identificativi](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Permessi Selected sulle liste](https://learn.microsoft.com/en-us/graph/permissions-selected-overview)
- [Grant applicativo su lista](https://learn.microsoft.com/en-us/graph/api/list-post-permissions?view=graph-rest-1.0)
- [Client credentials e certificati](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow)
- [Throttling e Retry-After](https://learn.microsoft.com/en-us/graph/throttling)
- [Aggiornamento con ETag](https://learn.microsoft.com/en-us/graph/api/listitem-update?view=graph-rest-1.0)

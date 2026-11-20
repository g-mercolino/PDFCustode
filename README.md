# PDF Custode

Lettore Windows x64 e Android 15+ con biblioteca e importazione di file cifrati `.pdfcustode`. Dopo l'attivazione i documenti sono leggibili offline.

## Preparare un documento

Dalla radice del progetto, usare una cartella di output nuova o vuota.

```powershell
dotnet run --project Packager -- prepare 'C:/percorso/prova.pdf' Admin/prova
```

Inviare `Admin/prova/client/documento.pdfcustode` e il codice stampato. Il codice è salvato anche in `Admin/prova/server/activation-code.txt`. Conservare il materiale amministrativo della cartella `server` sul PC amministratore.

Per convertire un documento già preparato, conservando identità e attivazioni esistenti, eseguire il comando seguente.

```powershell
dotnet run --project Packager -- export Admin/collaudo/client
```

Per un codice aggiuntivo per un altro dispositivo usare `dotnet run --project Packager -- issue Admin/prova/server`.

## Avviare il servizio

Il servizio può leggere tutti i documenti sotto `Admin`, anche quelli preparati mentre è in esecuzione. Può anche continuare a usare una singola cartella `server`.

```powershell
$env:PDFCUSTODE_DATA = (Resolve-Path Admin).Path
dotnet run --project Server --no-launch-profile -- --urls http://0.0.0.0:5187
```

Dal PC usare `http://localhost:5187`. Dal tablet sulla stessa rete Wi-Fi usare l'indirizzo IPv4 del PC, per esempio `http://192.168.1.21:5187`. Consentire l'accesso nel firewall sulla rete privata. I pacchetti di collaudo consentono HTTP sulla rete privata. Per un servizio pubblico usare HTTPS.

## Usare la biblioteca

Installare l'APK firmato in `Destinatario-Android` o aprire `Destinatario-Windows-Home/PdfCustode.App.exe`. Trasferire il file `.pdfcustode`, premere **Apri documento**, selezionarlo e inserire indirizzo e codice di attivazione.

La biblioteca conserva una copia cifrata di ogni documento e una licenza separata. Il pulsante **Home** torna all'elenco. Reimportare lo stesso file conserva la licenza. Per nuovi PDF basta preparare e inviare un nuovo `.pdfcustode`.

La licenza dura 30 giorni esatti dalla prima attivazione del codice sul dispositivo. La scadenza è inclusa nei dati firmati dal servizio e viene verificata anche offline. Il lettore controlla la scadenza all'apertura e durante la consultazione. Le licenze precedenti, prive del campo di scadenza, usano la data di attivazione firmata più 30 giorni. Il controllo offline usa la data e l'ora del dispositivo.

Per rinnovare usare `dotnet run --project Packager -- issue Admin/prova/server` e inserire il nuovo codice nel lettore. Ogni nuovo codice attivato avvia un nuovo periodo di 30 giorni. Reinserire un codice già usato restituisce la sua licenza originale con la medesima scadenza. Per applicare questa regola installare il lettore aggiornato e riavviare il servizio aggiornato. Le precedenti versioni del lettore devono essere aggiornate.

Installare gli aggiornamenti sopra la versione precedente per conservare i dati. Disinstallare l'app elimina dati e chiave del dispositivo. Una vecchia licenza può essere recuperata importando il file esportato dello stesso documento sullo stesso dispositivo.

## Compilare

Servono .NET 10 e gli strumenti MAUI, con SDK Android e Java configurati per Android. I documenti delle vecchie versioni sono esclusi dai nuovi pacchetti.

```powershell
dotnet publish App/PdfCustode.App.csproj -c Release -f net10.0-android -p:AllowInsecureTestServer=true -p:RunAOTCompilation=false -p:AndroidPackageFormats=apk -o Destinatario-Android --source https://api.nuget.org/v3/index.json
dotnet publish App/PdfCustode.App.csproj -c Release -f net10.0-windows10.0.19041.0 -p:RuntimeIdentifierOverride=win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:AllowInsecureTestServer=true -o Destinatario-Windows-Home --source https://api.nuget.org/v3/index.json
```

Eseguire `dotnet run --project Tests` per verificare cifratura, importazione, reimportazione, isolamento delle licenze e attivazione di più documenti.

# PDF Custode

Lettore .NET MAUI per Windows e Android 15+, con ricerca nel PDF incorporato e attivazione iniziale tramite servizio ASP.NET Core. Apri `PDFCustode.sln` con gli strumenti .NET 10 e MAUI installati.

Per preparare un PDF di prova, dalla radice del repository esegui in PowerShell:

```powershell
dotnet run --project Packager -- prepare 'C:/percorso/prova.pdf' Admin/collaudo
Copy-Item Admin/collaudo/client/document.json,Admin/collaudo/client/authority.pub App/Resources/Raw/
```

`Compila.ps1` genera i pacchetti di collaudo. Adatta i percorsi SDK Android e Java se sono diversi sul tuo PC. Per avviare il servizio, in un terminale separato:

```powershell
$env:PDFCUSTODE_DATA = (Resolve-Path Admin/collaudo/server).Path
dotnet run --project Server --no-launch-profile
```

Attiva il lettore usando il codice generato dal preparatore e `http://localhost:5187` su Windows. Su un tablet di test collegato via USB, usa `adb reverse tcp:5187 tcp:5187` e lo stesso indirizzo. Per verificare l'offline, ferma il servizio e riapri il lettore già attivato.

Esegui i test con `dotnet run --project Tests`. Mantieni i documenti originali, i codici e le chiavi di firma in una cartella amministrativa riservata. Il servizio di collaudo e la firma Android di sviluppo servono ai test locali.

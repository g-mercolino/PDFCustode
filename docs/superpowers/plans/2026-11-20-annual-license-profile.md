# Annual license pool and activation profile implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans. The user has requested implementation in this session.

**Goal:** Align the backend, SharePoint projection and reader activation form with the supervisor's revised requirements.

**Architecture:** Local durable activation state remains authoritative for issued codes and signed licenses. SharePoint supplies the annual total and receives activated licenses and audit events. One filesystem lock around the annual count and issuance prevents concurrent over-allocation across documents sharing one data root.

**Tech Stack:** .NET 10, ASP.NET Core, MAUI, Microsoft Graph v1.0, Azure.Identity with certificate or managed identity.

**Spec:** Supervisor instructions in this chat, confirmed 2026-11-20: annual pool; license consumed at activation; Nome, Cognome, CodiceUtente entered by recipient; automatic machine name; one backend technical identity; administrative DataFine 31/12/2100. User explicitly retains the signed 30-day technical expiry and authorizes updating the reader form.

## Global constraints

- Keep cryptographic device binding and the 30-day offline validity checks.
- Do not introduce user login to SharePoint or embed backend credentials in the reader.
- Manual administrative revocation/replacement; no automatic return of expired seats.
- A fixed configured QuotaYear controls allocation. Changing year is an administrative operation.
- Existing signed responses remain retryable without Graph access and without consuming another seat.
- Log outcomes and correlation IDs, never request bodies, activation codes, tokens, private keys or PDF content.

## Review focus

- Concurrent activation of different documents with one remaining annual seat.
- Restart or lost HTTP response after the signed license was persisted.
- Legacy activation entries without user metadata or allocation year.
- Distinguishing administrative validity from actual offline expiry.
- Operator edits, Graph conflicts and unavailable SharePoint.

## Task 1: Durable profile and annual allocation

**Files:** Core/Crypto.cs; Server/ActivationStore.cs; ActivationCatalog.cs; ActivationCoordinator.cs; Program.cs; Tests/AnnualActivationTests.cs; Tests/HttpSmoke.ps1.

**Interfaces:** ActivationRequest keeps its three technical arguments and adds optional Nome, Cognome, CodiceUtente, NomeMacchina, Piattaforma. ActivationProfile stores those five strings. ActivationSnapshot adds Profile, QuotaYear and EffectiveQuotaYear (legacy fallback to signed IssuedAt year). Synchronizer exposes QuotaYear and GetQuotaAsync(documentId, ct).

- [x] Add failing tests for global annual concurrency, durable profile, retry, legacy state and invalid profile rejection.
- [x] Persist profile/year atomically with issuance and enforce the annual quota under a root lock.
- [x] Return a clear 400 for invalid fresh-activation data; maintain existing code/device validation and audit safeguards.
- [x] Run unit and real HTTP smoke checks, including the unchanged 30-day signed expiry.

## Task 2: Annual SharePoint projection

**Files:** Server/SharePoint/SharePointOptions.cs; SharePointSynchronizer.cs; Tests/SharePointSyncTests.cs; Server/appsettings.Local.example.json; docs/SHAREPOINT.md.

**Interfaces:** Options use QuotaYear, InitialLicenseTotal and DocumentIds. PDF_Distribuzioni is keyed by Anno; consumed count accepts the existing LicenzeConsumate or renamed LicenzeImpegnate. PDF_Licenze is keyed by CodiceHash and receives signed activations only.

- [x] Add failing tests for empty activated list before activation, annual counts, profile projection and fixed administrative date.
- [x] Resolve the new schema, preserve operator totals/status/notes, project activated records, and retain durable audit deduplication/retries.
- [x] Write DataFine=2100-12-31 and optional ScadenzaTecnica from the actual signed expiry. Preserve historical years without inventing their quota.
- [x] Run simulated Graph tests; document column and configuration migration.

## Task 3: Reader form and end-to-end verification

**Files:** App/ReaderPage.cs; App/PdfCustode.App.csproj; README.md.

- [x] Add the three required personal fields, collect machine/platform automatically and send the new API fields.
- [x] Build Windows and Android targets; run the complete backend test suite and HTTP smoke checks.
- [x] Provide Entra ID configuration instructions and an acceptance procedure. State separately whether real-tenant access was tested.

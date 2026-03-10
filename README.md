# Vendor Bill Processor QB
Installed Windows desktop app for preparing vendor bills before posting to QuickBooks Desktop.

## Current implementation (Slice 1)
- WPF desktop UI for bill intake/review.
- CSV import workflow.
- Deterministic PO/Job normalization to the first 6 digits (strips content after first dash).
- Validation engine that marks records as `ReadyToPost` or `NeedsReview`.
- Encrypted local persistence (Windows DPAPI, current user scope).
- Redacted audit logging (vendor/invoice/PO masked).
- Automated self-tests for core normalization and workflow rules.

## Slice 2 (QuickBooks integration workflow)
- Guarded posting controls in the UI:
  - session authorization checkbox
  - transport selector (File Drop Bridge or Direct Desktop SDK)
  - company file identifier input
- Idempotent request queueing with deterministic request IDs.
- QuickBooks bridge outbox/inbox workflow:
  - queue `BillAdd` qbXML files to local outbox
  - ingest bridge response files from local inbox
- Verification result handling:
  - success -> `Posted`
  - recoverable failure -> back to `ReadyToPost` for retry
  - non-recoverable failure -> `PostingFailed`
- Retry-aware posting metadata tracked per bill:
  - request ID, attempts, last error, QuickBooks Txn ID, posted timestamp

## Slice 2.5 (direct transport mode)
- Added `Direct Desktop SDK (QBXMLRP2)` mode that submits qbXML directly via QuickBooks Desktop RequestProcessor COM automation.
- Transport mode is selectable at runtime in the app header.
- Direct mode keeps the same queue/verify workflow:
  - `Queue QB Post` submits through direct transport and buffers results
  - `Verify QB Results` applies buffered post outcomes to bill state
- If QB SDK is unavailable, direct mode returns explicit transport errors on verification with safe failure details.

## Slice 3 (packaging + signing workflow)
- Added release scripts under `scripts/release`:
  - `Invoke-ReleasePackaging.ps1` (publish, checksum, package zip)
  - `Sign-ReleaseArtifacts.ps1` (Authenticode signing with `signtool`)
  - `Verify-Signatures.ps1` (signature status validation)
- Added CI workflow `.github/workflows/release.yml`:
  - builds/tests on `workflow_dispatch` and `v*` tags
  - packages artifacts
  - optionally signs artifacts when cert secrets are configured

## Slice 4 (bill pay management dashboard)
- Added `Sync QB Bills + Balance` workflow to pull unpaid vendor bills and operating account balance from QuickBooks Desktop.
- Added due-date classification with a fixed **3-day due-soon window**:
  - `Overdue` (past due)
  - `DueSoon` (due in 0-3 days)
  - `Upcoming` (due in 4+ days)
- Added dashboard filters for All / Overdue / Due Soon / Upcoming.
- Added per-row and header-level approval checkboxes for check-run selection.
- Added running totals panel:
  - operating balance before run
  - approved check total
  - projected operating balance after run
- Added print-ready approved check run summary window with native print dialog support.

## Solution layout
- `src/BillProcessor.App`: WPF desktop shell.
- `src/BillProcessor.Core`: domain models and processing workflow.
- `src/BillProcessor.Infrastructure`: secure storage, importers, and safe logging.
- `src/BillProcessor.Infrastructure/QuickBooks`: file-drop QuickBooks bridge gateway.
- `src/BillProcessor.Infrastructure/QuickBooks/QbXmlRp2DesktopTransport.cs`: direct QuickBooks Desktop transport.
- `tests/BillProcessor.Core.SelfTests`: no-dependency automated self-tests.

## CSV import format
Required columns:
- `VendorName`
- `InvoiceNumber`
- `Amount`
- `PoJob`

Optional columns:
- `InvoiceDate`
- `DueDate`
- `ExpenseAccount`

Example:
```csv
VendorName,InvoiceNumber,InvoiceDate,DueDate,Amount,PoJob,ExpenseAccount
Acme Supplies,INV-1001,2026-03-01,2026-03-15,1532.55,123456-88,Materials
```

## Build and run
```powershell
dotnet build .\BillProcessorQb.slnx
dotnet run --project .\src\BillProcessor.App\BillProcessor.App.csproj
```

## Run automated self-tests
```powershell
dotnet run --project .\tests\BillProcessor.Core.SelfTests\BillProcessor.Core.SelfTests.csproj
```

## Build release package (unsigned)
```powershell
.\scripts\release\Invoke-ReleasePackaging.ps1 -Version 1.0.0
```
Default behavior is a portable (framework-dependent) package. To force a runtime-specific package:
```powershell
.\scripts\release\Invoke-ReleasePackaging.ps1 -Version 1.0.0 -RuntimeIdentifier win-x64
```
Note: RID-specific packaging requires runtime packs from `nuget.org` (or an equivalent internal mirror feed).  
If your machine only has offline sources enabled, `dotnet publish -r <rid>` will fail with `NU1101` package restore errors.

## Build release package (signed)
Prerequisites:
- Install Windows SDK `signtool`.
- Set `RELEASE_CERT_PATH` to your `.pfx` file path.
- Set `RELEASE_CERT_PASSWORD` to your cert password.

```powershell
$env:RELEASE_CERT_PATH = "C:\secure\certs\release-cert.pfx"
$env:RELEASE_CERT_PASSWORD = "{{RELEASE_CERT_PASSWORD}}"
.\scripts\release\Invoke-ReleasePackaging.ps1 -Version 1.0.0 -RuntimeIdentifier win-x64 -Sign
```

Outputs:
- `artifacts/release/VendorBillProcessorQB-<version>-<rid>/` (staging payload + metadata + checksums)
- `artifacts/release/VendorBillProcessorQB-<version>-<rid>.zip` (distributable package)

## GitHub Actions release workflow
Workflow file: `.github/workflows/release.yml`

### Manual dispatch
Provide:
- `version` (required)
- optional `runtime`
- optional `selfContained`
- optional `sign`

### Required secrets for signing
- `RELEASE_CERT_BASE64` (base64 encoded PFX)
- `RELEASE_CERT_PASSWORD`

## Security notes
- Local bill data is encrypted at rest using DPAPI (`DataProtectionScope.CurrentUser`).
- Log output is redacted to avoid exposing full vendor/invoice identifiers.
- Input is validated before records are marked ready for downstream posting.
- QuickBooks posting is explicitly guarded per session; queueing is blocked without authorization and a company identifier.

## QuickBooks bridge file contract
Outbox and inbox paths are shown in the app header and default to:
- `%LOCALAPPDATA%\VendorBillProcessorQB\qbbridge\outbox`
- `%LOCALAPPDATA%\VendorBillProcessorQB\qbbridge\inbox`

Queued request files:
- `*.qbxml` (BillAdd request payload)
- `*.meta.json` (request metadata)

Accepted inbox result formats:
1. JSON result file:
```json
{
  "requestId": "BP-abc123...",
  "success": true,
  "recoverable": false,
  "errorCode": "",
  "errorMessage": "",
  "quickBooksTxnId": "TXN-123456",
  "processedAtUtc": "2026-03-09T15:30:00Z"
}
```
2. QuickBooks XML response containing `BillAddRs` (requestID + statusCode + statusMessage + optional `TxnID`).
3. Bill pay snapshot JSON file named `billpay-snapshot*.json` (used by File Drop Bridge mode for Slice 4 sync):
```json
{
  "syncedAtUtc": "2026-03-09T15:30:00Z",
  "operatingAccountName": "Operating Account",
  "operatingAccountBalance": 12050.42,
  "bills": [
    {
      "vendorName": "Acme Supplies",
      "invoiceNumber": "INV-1001",
      "invoiceDate": "2026-03-01T00:00:00",
      "dueDate": "2026-03-15T00:00:00",
      "amount": 1532.55,
      "poJob": "123456-88",
      "expenseAccountName": "Materials",
      "quickBooksTxnId": "7D2A-112233"
    }
  ]
}
```

## Direct Desktop SDK notes
- Direct mode requires QuickBooks Desktop SDK components exposing `QBXMLRP2.RequestProcessor`.
- QuickBooks Desktop must be installed and accessible under the current user context.
- Slice 4 sync in direct mode queries unpaid bills (`BillQueryRq` with `PaidStatus=NotPaidOnly`) and operating account balance (`AccountQueryRq`).
- For direct mode company-file targeting:
  - pass a `.QBW` path in the Company File box to target a specific company file
  - non-path identifiers are treated as current/active company context

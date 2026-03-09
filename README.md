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

## Direct Desktop SDK notes
- Direct mode requires QuickBooks Desktop SDK components exposing `QBXMLRP2.RequestProcessor`.
- QuickBooks Desktop must be installed and accessible under the current user context.
- For direct mode company-file targeting:
  - pass a `.QBW` path in the Company File box to target a specific company file
  - non-path identifiers are treated as current/active company context

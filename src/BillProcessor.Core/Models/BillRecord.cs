namespace BillProcessor.Core.Models;

public sealed class BillRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VendorName { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime? InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public decimal Amount { get; set; }
    public string ExpenseAccountName { get; set; } = "Uncategorized Expense";
    public string PurchaseOrderOrJobRaw { get; set; } = string.Empty;
    public string PurchaseOrderOrJobNormalized { get; set; } = string.Empty;
    public BillProcessingStatus Status { get; set; } = BillProcessingStatus.Imported;
    public List<string> ValidationErrors { get; set; } = [];
    public string ValidationErrorText { get; set; } = string.Empty;
    public string QuickBooksRequestId { get; set; } = string.Empty;
    public string QuickBooksTxnId { get; set; } = string.Empty;
    public int QuickBooksPostAttemptCount { get; set; }
    public DateTimeOffset? QuickBooksLastAttemptUtc { get; set; }
    public string QuickBooksLastErrorCode { get; set; } = string.Empty;
    public string QuickBooksLastErrorMessage { get; set; } = string.Empty;
    public bool QuickBooksLastErrorRecoverable { get; set; }
    public DateTimeOffset? QuickBooksPostedAtUtc { get; set; }
    public List<BillAuditEntry> AuditTrail { get; set; } = [];
    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public void AddAudit(string action, string detail)
    {
        AuditTrail.Add(new BillAuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Action = action,
            Detail = detail
        });

        LastUpdatedUtc = DateTimeOffset.UtcNow;
    }
}

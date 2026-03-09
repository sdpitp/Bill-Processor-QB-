using BillProcessor.Core.Models;

namespace BillProcessor.Core.Services;

public sealed class BillWorkflowEngine
{
    private readonly BillValidator _validator = new();

    public BillRecord Process(BillRecord bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        bill.Status = BillProcessingStatus.Normalized;
        bill.PurchaseOrderOrJobNormalized = PoJobNormalizer.Normalize(bill.PurchaseOrderOrJobRaw);
        bill.ValidationErrors.Clear();
        bill.ValidationErrorText = string.Empty;

        bill.AddAudit(
            "normalized",
            $"Raw PO/Job '{bill.PurchaseOrderOrJobRaw}' normalized to '{bill.PurchaseOrderOrJobNormalized}'.");

        var validation = _validator.Validate(bill);
        bill.ValidationErrors.AddRange(validation.Errors);
        bill.ValidationErrorText = string.Join(" | ", validation.Errors);
        bill.Status = validation.IsValid
            ? BillProcessingStatus.ReadyToPost
            : BillProcessingStatus.NeedsReview;

        bill.AddAudit(
            "validated",
            validation.IsValid
                ? "Record passed validation and is ready to post."
                : $"Record requires review: {bill.ValidationErrorText}");

        return bill;
    }

    public IReadOnlyList<BillRecord> ProcessAll(IEnumerable<BillRecord> bills)
    {
        ArgumentNullException.ThrowIfNull(bills);
        return bills.Select(Process).ToList();
    }
}

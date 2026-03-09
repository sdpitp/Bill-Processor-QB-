using BillProcessor.Core.Models;

namespace BillProcessor.Core.Services;

public sealed class BillValidator
{
    public BillValidationResult Validate(BillRecord bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var result = new BillValidationResult();

        if (string.IsNullOrWhiteSpace(bill.VendorName))
        {
            result.Errors.Add("Vendor name is required.");
        }

        if (string.IsNullOrWhiteSpace(bill.InvoiceNumber))
        {
            result.Errors.Add("Invoice number is required.");
        }

        if (bill.Amount <= 0)
        {
            result.Errors.Add("Amount must be greater than zero.");
        }
        if (string.IsNullOrWhiteSpace(bill.ExpenseAccountName))
        {
            result.Errors.Add("Expense account is required for QuickBooks posting.");
        }

        if (bill.InvoiceDate.HasValue && bill.DueDate.HasValue && bill.DueDate.Value.Date < bill.InvoiceDate.Value.Date)
        {
            result.Errors.Add("Due date cannot be before invoice date.");
        }

        if (string.IsNullOrWhiteSpace(bill.PurchaseOrderOrJobNormalized))
        {
            result.Errors.Add("PO/Job number is required.");
            return result;
        }

        if (bill.PurchaseOrderOrJobNormalized.Length != PoJobNormalizer.RequiredDigits)
        {
            result.Errors.Add("PO/Job number must contain exactly 6 digits after normalization.");
        }
        else if (!bill.PurchaseOrderOrJobNormalized.All(char.IsDigit))
        {
            result.Errors.Add("PO/Job number must be numeric.");
        }

        return result;
    }
}

using System.Security.Cryptography;
using System.Text;
using BillProcessor.Core.Models;

namespace BillProcessor.Core.Services;

public static class QuickBooksRequestIdGenerator
{
    public static string Generate(BillRecord bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        var canonical = string.Join("|",
            bill.Id.ToString("N"),
            NormalizeToken(bill.VendorName),
            NormalizeToken(bill.InvoiceNumber),
            bill.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            NormalizeToken(bill.PurchaseOrderOrJobNormalized),
            NormalizeToken(bill.ExpenseAccountName));

        var hashHex = HashPayload(canonical);
        return $"BP-{hashHex[..24]}";
    }

    public static string HashPayload(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeToken(string value)
    {
        return value.Trim().ToUpperInvariant();
    }
}

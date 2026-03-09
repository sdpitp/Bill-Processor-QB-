using System.Globalization;
using System.Security;
using BillProcessor.Core.Models;

namespace BillProcessor.Core.Services;

public static class QuickBooksBillXmlBuilder
{
    public static string BuildBillAddRequest(BillRecord bill, string requestId, string companyFileIdentifier)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var vendorName = Escape(bill.VendorName);
        var invoiceNumber = Escape(bill.InvoiceNumber);
        var expenseAccount = Escape(bill.ExpenseAccountName);
        var poJob = Escape(bill.PurchaseOrderOrJobNormalized);
        var companyHint = Escape(companyFileIdentifier);
        var txnDate = (bill.InvoiceDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dueDate = (bill.DueDate ?? (bill.InvoiceDate ?? DateTime.Today).AddDays(30))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var amount = bill.Amount.ToString("F2", CultureInfo.InvariantCulture);

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
               "<?qbxml version=\"16.0\"?>\n" +
               "<QBXML>\n" +
               "  <QBXMLMsgsRq onError=\"stopOnError\">\n" +
               $"    <BillAddRq requestID=\"{requestId}\">\n" +
               "      <BillAdd>\n" +
               "        <VendorRef>\n" +
               $"          <FullName>{vendorName}</FullName>\n" +
               "        </VendorRef>\n" +
               $"        <TxnDate>{txnDate}</TxnDate>\n" +
               $"        <RefNumber>{invoiceNumber}</RefNumber>\n" +
               $"        <DueDate>{dueDate}</DueDate>\n" +
               $"        <Memo>Vendor Bill Processor - {companyHint}</Memo>\n" +
               "        <ExpenseLineAdd>\n" +
               "          <AccountRef>\n" +
               $"            <FullName>{expenseAccount}</FullName>\n" +
               "          </AccountRef>\n" +
               $"          <Amount>{amount}</Amount>\n" +
               $"          <Memo>PO/Job {poJob}</Memo>\n" +
               "        </ExpenseLineAdd>\n" +
               "      </BillAdd>\n" +
               "    </BillAddRq>\n" +
               "  </QBXMLMsgsRq>\n" +
               "</QBXML>\n";
    }

    private static string Escape(string? value)
    {
        return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
    }
}

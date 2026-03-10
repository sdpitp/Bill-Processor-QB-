namespace BillProcessor.Core.Models;

public sealed class QuickBooksBillPaySyncRequest
{
    public string CompanyFileIdentifier { get; set; } = string.Empty;
    public string OperatingAccountName { get; set; } = "Operating Account";
    public int DueSoonDays { get; set; } = 3;
    public int MaxBillsToReturn { get; set; } = 500;
    public DateTime AsOfDate { get; set; } = DateTime.Today;
}

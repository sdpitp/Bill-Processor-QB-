namespace BillProcessor.Core.Models;

public sealed class QuickBooksBillPaySnapshot
{
    public DateTimeOffset SyncedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string OperatingAccountName { get; set; } = "Operating Account";
    public decimal OperatingAccountBalance { get; set; }
    public string SourceDescription { get; set; } = string.Empty;
    public string WarningMessage { get; set; } = string.Empty;
    public List<BillRecord> Bills { get; set; } = [];
}

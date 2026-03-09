namespace BillProcessor.Core.Models;

public sealed class BillAuditEntry
{
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

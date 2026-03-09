namespace BillProcessor.Core.Models;

public sealed class QuickBooksPostResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool Recoverable { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string QuickBooksTxnId { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

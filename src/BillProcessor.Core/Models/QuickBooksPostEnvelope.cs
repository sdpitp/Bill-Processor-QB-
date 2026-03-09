namespace BillProcessor.Core.Models;

public sealed class QuickBooksPostEnvelope
{
    public string RequestId { get; set; } = string.Empty;
    public Guid BillId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string PayloadHash { get; set; } = string.Empty;
    public string QbXmlPayload { get; set; } = string.Empty;
}

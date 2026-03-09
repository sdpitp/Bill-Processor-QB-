namespace BillProcessor.Core.Models;

public sealed class QuickBooksQueueResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool QueuedNewRequest { get; set; }
    public string Detail { get; set; } = string.Empty;
}

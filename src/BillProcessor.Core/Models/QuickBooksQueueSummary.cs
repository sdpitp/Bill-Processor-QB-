namespace BillProcessor.Core.Models;

public sealed class QuickBooksQueueSummary
{
    public int EligibleCount { get; set; }
    public int QueuedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int SkippedCount { get; set; }
}

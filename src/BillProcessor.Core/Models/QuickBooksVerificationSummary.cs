namespace BillProcessor.Core.Models;

public sealed class QuickBooksVerificationSummary
{
    public int TotalResultsRead { get; set; }
    public int PostedCount { get; set; }
    public int FailedCount { get; set; }
    public int RecoverableFailuresScheduledForRetry { get; set; }
    public int UnmatchedResults { get; set; }
}

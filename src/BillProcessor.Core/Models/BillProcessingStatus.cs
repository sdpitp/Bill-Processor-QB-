namespace BillProcessor.Core.Models;

public enum BillProcessingStatus
{
    Imported = 0,
    Normalized = 1,
    NeedsReview = 2,
    ReadyToPost = 3,
    PostingQueued = 4,
    PostingFailed = 5,
    Posted = 6
}

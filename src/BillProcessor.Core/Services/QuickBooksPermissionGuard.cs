using BillProcessor.Core.Models;

namespace BillProcessor.Core.Services;

public static class QuickBooksPermissionGuard
{
    public static void EnsureCanPost(QuickBooksSessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);

        if (sessionContext.AccessIntent != QuickBooksAccessIntent.PostBills)
        {
            throw new InvalidOperationException("QuickBooks access intent must be PostBills for posting operations.");
        }

        if (!sessionContext.IsPostingAuthorizedForSession)
        {
            throw new InvalidOperationException(
                "Posting is not authorized for this session. Explicitly authorize posting before queueing bills.");
        }

        if (string.IsNullOrWhiteSpace(sessionContext.CompanyFileIdentifier))
        {
            throw new InvalidOperationException("A company file identifier is required for guarded QuickBooks posting.");
        }
    }
}

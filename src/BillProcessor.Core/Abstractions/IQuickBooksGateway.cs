using BillProcessor.Core.Models;

namespace BillProcessor.Core.Abstractions;

public interface IQuickBooksGateway
{
    Task<IReadOnlyList<QuickBooksQueueResult>> QueueAsync(
        IReadOnlyList<QuickBooksPostEnvelope> envelopes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuickBooksPostResult>> FetchResultsAsync(CancellationToken cancellationToken = default);

    string GetOutboxPath();
    string GetInboxPath();
}

using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;

namespace BillProcessor.Infrastructure.QuickBooks;

public sealed class DirectQuickBooksGateway : IQuickBooksGateway
{
    private readonly IQuickBooksDesktopTransport _transport;
    private readonly Dictionary<string, QuickBooksPostResult> _pendingResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenRequestIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    public DirectQuickBooksGateway(IQuickBooksDesktopTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string GetOutboxPath() => $"direct://{_transport.GetTransportName()}/submit";
    public string GetInboxPath() => $"direct://{_transport.GetTransportName()}/results";

    public async Task<IReadOnlyList<QuickBooksQueueResult>> QueueAsync(
        IReadOnlyList<QuickBooksPostEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        var queueResults = new List<QuickBooksQueueResult>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool shouldSubmit;
            lock (_syncRoot)
            {
                shouldSubmit = _seenRequestIds.Add(envelope.RequestId);
            }

            if (!shouldSubmit)
            {
                queueResults.Add(new QuickBooksQueueResult
                {
                    RequestId = envelope.RequestId,
                    QueuedNewRequest = false,
                    Detail = "Direct transport skipped duplicate request."
                });
                continue;
            }

            var postResult = await _transport.SubmitBillAsync(
                new QuickBooksDirectPostRequest
                {
                    RequestId = envelope.RequestId,
                    CompanyFileIdentifier = envelope.CompanyFileIdentifier,
                    QbXmlPayload = envelope.QbXmlPayload
                },
                cancellationToken);

            lock (_syncRoot)
            {
                _pendingResults[envelope.RequestId] = postResult;
            }

            queueResults.Add(new QuickBooksQueueResult
            {
                RequestId = envelope.RequestId,
                QueuedNewRequest = true,
                Detail = $"Submitted through {_transport.GetTransportName()}."
            });
        }

        return queueResults;
    }

    public Task<IReadOnlyList<QuickBooksPostResult>> FetchResultsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<QuickBooksPostResult> snapshot;
        lock (_syncRoot)
        {
            snapshot = _pendingResults.Values.ToList();
            _pendingResults.Clear();
        }

        return Task.FromResult(snapshot);
    }
}

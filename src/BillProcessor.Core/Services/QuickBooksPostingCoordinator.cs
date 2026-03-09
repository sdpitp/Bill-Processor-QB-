using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;

namespace BillProcessor.Core.Services;

public sealed class QuickBooksPostingCoordinator
{
    private readonly IQuickBooksGateway _gateway;
    private readonly int _maxRecoverableRetries;

    public QuickBooksPostingCoordinator(IQuickBooksGateway gateway, int maxRecoverableRetries = 3)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (maxRecoverableRetries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecoverableRetries), "Must be at least 1.");
        }

        _gateway = gateway;
        _maxRecoverableRetries = maxRecoverableRetries;
    }

    public async Task<QuickBooksQueueSummary> QueueBillsAsync(
        IEnumerable<BillRecord> bills,
        QuickBooksSessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(sessionContext);
        QuickBooksPermissionGuard.EnsureCanPost(sessionContext);

        var allBills = bills.ToList();
        var eligibleBills = allBills.Where(IsEligibleForQueue).ToList();
        var summary = new QuickBooksQueueSummary
        {
            EligibleCount = eligibleBills.Count,
            SkippedCount = allBills.Count - eligibleBills.Count
        };

        if (eligibleBills.Count == 0)
        {
            return summary;
        }

        var envelopeByRequestId = new Dictionary<string, (BillRecord Bill, QuickBooksPostEnvelope Envelope)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var bill in eligibleBills)
        {
            var requestId = QuickBooksRequestIdGenerator.Generate(bill);
            if (bill.Status == BillProcessingStatus.PostingQueued &&
                string.Equals(bill.QuickBooksRequestId, requestId, StringComparison.OrdinalIgnoreCase))
            {
                summary.DuplicateCount++;
                continue;
            }

            var payload = QuickBooksBillXmlBuilder.BuildBillAddRequest(bill, requestId, sessionContext.CompanyFileIdentifier);
            envelopeByRequestId[requestId] = (bill, new QuickBooksPostEnvelope
            {
                BillId = bill.Id,
                RequestId = requestId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PayloadHash = QuickBooksRequestIdGenerator.HashPayload(payload),
                QbXmlPayload = payload
            });
        }

        if (envelopeByRequestId.Count == 0)
        {
            return summary;
        }

        var queueResults = await _gateway.QueueAsync(envelopeByRequestId.Values.Select(entry => entry.Envelope).ToList(), cancellationToken);
        var queueResultMap = queueResults.ToDictionary(item => item.RequestId, StringComparer.OrdinalIgnoreCase);

        foreach (var (requestId, payloadInfo) in envelopeByRequestId)
        {
            var bill = payloadInfo.Bill;
            bill.QuickBooksRequestId = requestId;
            bill.QuickBooksPostAttemptCount++;
            bill.QuickBooksLastAttemptUtc = DateTimeOffset.UtcNow;

            if (!queueResultMap.TryGetValue(requestId, out var result))
            {
                result = new QuickBooksQueueResult
                {
                    RequestId = requestId,
                    QueuedNewRequest = true,
                    Detail = "Queued with default success handling."
                };
            }

            if (result.QueuedNewRequest)
            {
                summary.QueuedCount++;
                bill.Status = BillProcessingStatus.PostingQueued;
                bill.QuickBooksLastErrorCode = string.Empty;
                bill.QuickBooksLastErrorMessage = string.Empty;
                bill.QuickBooksLastErrorRecoverable = false;
                bill.AddAudit("qb_queued", $"Queued QuickBooks BillAdd request {requestId}.");
            }
            else
            {
                summary.DuplicateCount++;
                bill.Status = BillProcessingStatus.PostingQueued;
                bill.AddAudit("qb_duplicate", $"Skipped duplicate QuickBooks request {requestId}.");
            }
        }

        return summary;
    }

    public async Task<QuickBooksVerificationSummary> ApplyVerificationResultsAsync(
        IEnumerable<BillRecord> bills,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bills);

        var billByRequestId = bills
            .Where(bill => !string.IsNullOrWhiteSpace(bill.QuickBooksRequestId))
            .GroupBy(bill => bill.QuickBooksRequestId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(bill => bill.QuickBooksRequestId, StringComparer.OrdinalIgnoreCase);

        var results = await _gateway.FetchResultsAsync(cancellationToken);
        var summary = new QuickBooksVerificationSummary
        {
            TotalResultsRead = results.Count
        };

        foreach (var result in results)
        {
            if (!billByRequestId.TryGetValue(result.RequestId, out var bill))
            {
                summary.UnmatchedResults++;
                continue;
            }

            if (result.Success)
            {
                summary.PostedCount++;
                bill.Status = BillProcessingStatus.Posted;
                bill.QuickBooksTxnId = result.QuickBooksTxnId;
                bill.QuickBooksPostedAtUtc = result.ProcessedAtUtc;
                bill.QuickBooksLastErrorCode = string.Empty;
                bill.QuickBooksLastErrorMessage = string.Empty;
                bill.QuickBooksLastErrorRecoverable = false;
                bill.AddAudit("qb_posted", $"Posted to QuickBooks. TxnID={result.QuickBooksTxnId}");
                continue;
            }

            summary.FailedCount++;
            bill.QuickBooksLastErrorCode = result.ErrorCode;
            bill.QuickBooksLastErrorMessage = result.ErrorMessage;
            bill.QuickBooksLastErrorRecoverable = result.Recoverable;

            if (result.Recoverable && bill.QuickBooksPostAttemptCount < _maxRecoverableRetries)
            {
                summary.RecoverableFailuresScheduledForRetry++;
                bill.Status = BillProcessingStatus.ReadyToPost;
                bill.AddAudit(
                    "qb_retry_scheduled",
                    $"Recoverable error {result.ErrorCode}: {result.ErrorMessage}. Ready for retry.");
            }
            else
            {
                bill.Status = BillProcessingStatus.PostingFailed;
                bill.AddAudit(
                    "qb_failed",
                    $"Non-recoverable error {result.ErrorCode}: {result.ErrorMessage}.");
            }
        }

        return summary;
    }

    public string GetOutboxPath() => _gateway.GetOutboxPath();
    public string GetInboxPath() => _gateway.GetInboxPath();

    private bool IsEligibleForQueue(BillRecord bill)
    {
        if (bill.Status == BillProcessingStatus.ReadyToPost)
        {
            return true;
        }

        return bill.Status == BillProcessingStatus.PostingFailed &&
               bill.QuickBooksLastErrorRecoverable &&
               bill.QuickBooksPostAttemptCount < _maxRecoverableRetries;
    }
}

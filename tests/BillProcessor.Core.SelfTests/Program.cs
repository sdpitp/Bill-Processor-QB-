using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;
using BillProcessor.Core.Services;

var testSuite = new (string Name, Action Execute)[]
{
    ("Normalizes PO/Job by keeping first six digits before dash", ShouldNormalizePoJobWithDash),
    ("Normalizes PO/Job by stripping non-digits", ShouldNormalizePoJobWithMixedCharacters),
    ("Valid bill transitions to ReadyToPost", ShouldSetReadyToPostForValidBill),
    ("Invalid bill transitions to NeedsReview", ShouldSetNeedsReviewForInvalidBill),
    ("Validator catches due date before invoice date", ShouldCatchInvalidDateOrdering),
    ("QB queue requires explicit session authorization", ShouldRequireSessionAuthorizationForQueue),
    ("QB queue posts ready bill with idempotent request ID", ShouldQueueReadyBill),
    ("QB duplicate queue request is skipped", ShouldSkipDuplicateQueueRequest),
    ("QB recoverable failure returns bill to ReadyToPost", ShouldScheduleRecoverableRetry),
    ("QB success response marks bill as Posted", ShouldMarkBillAsPostedFromVerification),
    ("Direct gateway returns transport results through verify pipeline", ShouldProcessDirectGatewayResults),
    ("Direct gateway enforces idempotent request IDs", ShouldSkipDirectGatewayDuplicates)
};

var failures = new List<string>();

foreach (var (name, execute) in testSuite)
{
    try
    {
        execute();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"[FAIL] {name} -> {exception.Message}");
        failures.Add(name);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Self-tests failed: {failures.Count}/{testSuite.Length}");
    return 1;
}

Console.WriteLine($"All self-tests passed: {testSuite.Length}/{testSuite.Length}");
return 0;

static void ShouldNormalizePoJobWithDash()
{
    var normalized = PoJobNormalizer.Normalize("123456-789-01");
    AssertEqual("123456", normalized, "PO/Job normalization should stop at dash and keep first 6 digits.");
}

static void ShouldNormalizePoJobWithMixedCharacters()
{
    var normalized = PoJobNormalizer.Normalize("PO #998877A");
    AssertEqual("998877", normalized, "PO/Job normalization should extract digits only.");
}

static void ShouldSetReadyToPostForValidBill()
{
    var bill = new BillRecord
    {
        VendorName = "Acme Supplies",
        InvoiceNumber = "INV-1001",
        InvoiceDate = new DateTime(2026, 3, 1),
        DueDate = new DateTime(2026, 3, 15),
        Amount = 1532.55m,
        ExpenseAccountName = "Materials",
        PurchaseOrderOrJobRaw = "123456-88"
    };

    var workflow = new BillWorkflowEngine();
    workflow.Process(bill);

    AssertEqual(BillProcessingStatus.ReadyToPost, bill.Status, "Valid bill should be marked ReadyToPost.");
    AssertEqual("123456", bill.PurchaseOrderOrJobNormalized, "PO/Job should normalize to first 6 digits.");
    AssertTrue(bill.ValidationErrors.Count == 0, "Valid bill should have no validation errors.");
}

static void ShouldSetNeedsReviewForInvalidBill()
{
    var bill = new BillRecord
    {
        VendorName = string.Empty,
        InvoiceNumber = string.Empty,
        Amount = 0m,
        PurchaseOrderOrJobRaw = "123-45"
    };

    var workflow = new BillWorkflowEngine();
    workflow.Process(bill);

    AssertEqual(BillProcessingStatus.NeedsReview, bill.Status, "Invalid bill should be marked NeedsReview.");
    AssertTrue(bill.ValidationErrors.Count >= 4, "Invalid bill should include validation errors.");
}

static void ShouldCatchInvalidDateOrdering()
{
    var bill = new BillRecord
    {
        VendorName = "Data Vendor",
        InvoiceNumber = "INV-7",
        InvoiceDate = new DateTime(2026, 3, 20),
        DueDate = new DateTime(2026, 3, 15),
        Amount = 10m,
        ExpenseAccountName = "Office Supplies",
        PurchaseOrderOrJobNormalized = "123456"
    };

    var validator = new BillValidator();
    var result = validator.Validate(bill);

    AssertTrue(!result.IsValid, "Date ordering should fail validation.");
    AssertTrue(result.Errors.Any(error => error.Contains("Due date cannot be before invoice date.")),
        "Expected due date ordering validation message.");
}

static void ShouldRequireSessionAuthorizationForQueue()
{
    var gateway = new FakeQuickBooksGateway();
    var coordinator = new QuickBooksPostingCoordinator(gateway);
    var bill = CreateReadyBill();

    var unauthorizedSession = new QuickBooksSessionContext
    {
        IsPostingAuthorizedForSession = false,
        CompanyFileIdentifier = "AcmeCompany",
        AccessIntent = QuickBooksAccessIntent.PostBills
    };

    AssertThrows<InvalidOperationException>(
        () => coordinator.QueueBillsAsync([bill], unauthorizedSession).GetAwaiter().GetResult(),
        "Queue should fail when session authorization is not granted.");
}

static void ShouldQueueReadyBill()
{
    var gateway = new FakeQuickBooksGateway();
    var coordinator = new QuickBooksPostingCoordinator(gateway);
    var bill = CreateReadyBill();

    var summary = coordinator.QueueBillsAsync([bill], CreateAuthorizedSession()).GetAwaiter().GetResult();

    AssertEqual(1, summary.EligibleCount, "One bill should be eligible for queueing.");
    AssertEqual(1, summary.QueuedCount, "One bill should be queued.");
    AssertEqual(BillProcessingStatus.PostingQueued, bill.Status, "Bill should move to PostingQueued.");
    AssertTrue(!string.IsNullOrWhiteSpace(bill.QuickBooksRequestId), "Request ID should be populated.");
    AssertTrue(gateway.Outbox.Count == 1, "Gateway should contain one queued envelope.");
}

static void ShouldSkipDuplicateQueueRequest()
{
    var gateway = new FakeQuickBooksGateway();
    var coordinator = new QuickBooksPostingCoordinator(gateway);
    var bill = CreateReadyBill();
    var session = CreateAuthorizedSession();

    var firstSummary = coordinator.QueueBillsAsync([bill], session).GetAwaiter().GetResult();
    var secondSummary = coordinator.QueueBillsAsync([bill], session).GetAwaiter().GetResult();

    AssertEqual(1, firstSummary.QueuedCount, "First queue call should enqueue the bill.");
    AssertEqual(1, secondSummary.SkippedCount, "Second queue call should skip already queued bill.");
    AssertTrue(gateway.Outbox.Count == 1, "Outbox should still have one request.");
}

static void ShouldScheduleRecoverableRetry()
{
    var gateway = new FakeQuickBooksGateway();
    var coordinator = new QuickBooksPostingCoordinator(gateway);
    var bill = CreateReadyBill();
    coordinator.QueueBillsAsync([bill], CreateAuthorizedSession()).GetAwaiter().GetResult();

    gateway.PendingResults.Add(new QuickBooksPostResult
    {
        RequestId = bill.QuickBooksRequestId,
        Success = false,
        Recoverable = true,
        ErrorCode = "500",
        ErrorMessage = "Temporary bridge timeout."
    });

    var summary = coordinator.ApplyVerificationResultsAsync([bill]).GetAwaiter().GetResult();
    AssertEqual(1, summary.RecoverableFailuresScheduledForRetry, "Recoverable failure should be marked for retry.");
    AssertEqual(BillProcessingStatus.ReadyToPost, bill.Status, "Bill should return to ReadyToPost for retry.");
}

static void ShouldMarkBillAsPostedFromVerification()
{
    var gateway = new FakeQuickBooksGateway();
    var coordinator = new QuickBooksPostingCoordinator(gateway);
    var bill = CreateReadyBill();
    coordinator.QueueBillsAsync([bill], CreateAuthorizedSession()).GetAwaiter().GetResult();

    gateway.PendingResults.Add(new QuickBooksPostResult
    {
        RequestId = bill.QuickBooksRequestId,
        Success = true,
        QuickBooksTxnId = "TXN-123456"
    });

    var summary = coordinator.ApplyVerificationResultsAsync([bill]).GetAwaiter().GetResult();
    AssertEqual(1, summary.PostedCount, "Posted result should increment posted count.");
    AssertEqual(BillProcessingStatus.Posted, bill.Status, "Bill should be marked as posted.");
    AssertEqual("TXN-123456", bill.QuickBooksTxnId, "QuickBooks Txn ID should be persisted.");
}

static void ShouldProcessDirectGatewayResults()
{
    var transport = new FakeDesktopTransport();
    var gateway = new DirectStyleGateway(transport);
    var coordinator = new QuickBooksPostingCoordinator(gateway);
    var bill = CreateReadyBill();

    var queueSummary = coordinator.QueueBillsAsync([bill], CreateAuthorizedSession()).GetAwaiter().GetResult();
    AssertEqual(1, queueSummary.QueuedCount, "Direct gateway should accept queue item.");

    var verification = coordinator.ApplyVerificationResultsAsync([bill]).GetAwaiter().GetResult();
    AssertEqual(1, verification.PostedCount, "Direct gateway result should flow into verification.");
    AssertEqual(BillProcessingStatus.Posted, bill.Status, "Bill should be marked posted from direct transport result.");
    AssertTrue(transport.Calls == 1, "Direct transport should be called once.");
}

static void ShouldSkipDirectGatewayDuplicates()
{
    var transport = new FakeDesktopTransport();
    var gateway = new DirectStyleGateway(transport);
    var coordinator = new QuickBooksPostingCoordinator(gateway);
    var bill = CreateReadyBill();
    var session = CreateAuthorizedSession();

    var first = coordinator.QueueBillsAsync([bill], session).GetAwaiter().GetResult();
    var second = coordinator.QueueBillsAsync([bill], session).GetAwaiter().GetResult();

    AssertEqual(1, first.QueuedCount, "First queue should submit to direct transport.");
    AssertEqual(1, second.SkippedCount, "Second queue should skip already queued bill.");
    AssertTrue(transport.Calls == 1, "Direct transport should only be called once.");
}

static BillRecord CreateReadyBill()
{
    var bill = new BillRecord
    {
        VendorName = "Acme Supplies",
        InvoiceNumber = "INV-1001",
        InvoiceDate = new DateTime(2026, 3, 1),
        DueDate = new DateTime(2026, 3, 15),
        Amount = 1532.55m,
        ExpenseAccountName = "Materials",
        PurchaseOrderOrJobRaw = "123456-88"
    };

    var workflow = new BillWorkflowEngine();
    workflow.Process(bill);
    return bill;
}

static QuickBooksSessionContext CreateAuthorizedSession()
{
    return new QuickBooksSessionContext
    {
        IsPostingAuthorizedForSession = true,
        CompanyFileIdentifier = "AcmeCompany",
        RequestedBy = "self-tests",
        AccessIntent = QuickBooksAccessIntent.PostBills
    };
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected: {expected}, Actual: {actual}");
    }
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

sealed class FakeQuickBooksGateway : IQuickBooksGateway
{
    public Dictionary<string, QuickBooksPostEnvelope> Outbox { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<QuickBooksPostResult> PendingResults { get; } = [];

    public Task<IReadOnlyList<QuickBooksQueueResult>> QueueAsync(
        IReadOnlyList<QuickBooksPostEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        var results = new List<QuickBooksQueueResult>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Outbox.ContainsKey(envelope.RequestId))
            {
                results.Add(new QuickBooksQueueResult
                {
                    RequestId = envelope.RequestId,
                    QueuedNewRequest = false,
                    Detail = "Duplicate request."
                });
                continue;
            }

            Outbox[envelope.RequestId] = envelope;
            results.Add(new QuickBooksQueueResult
            {
                RequestId = envelope.RequestId,
                QueuedNewRequest = true,
                Detail = "Queued."
            });
        }

        return Task.FromResult<IReadOnlyList<QuickBooksQueueResult>>(results);
    }

    public Task<IReadOnlyList<QuickBooksPostResult>> FetchResultsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = PendingResults.ToList();
        PendingResults.Clear();
        return Task.FromResult<IReadOnlyList<QuickBooksPostResult>>(snapshot);
    }

    public string GetOutboxPath() => "fake/outbox";
    public string GetInboxPath() => "fake/inbox";
}

sealed class FakeDesktopTransport : IQuickBooksDesktopTransport
{
    public int Calls { get; private set; }

    public Task<QuickBooksPostResult> SubmitBillAsync(
        QuickBooksDirectPostRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(new QuickBooksPostResult
        {
            RequestId = request.RequestId,
            Success = true,
            Recoverable = false,
            QuickBooksTxnId = $"TXN-DIRECT-{Calls}",
            ProcessedAtUtc = DateTimeOffset.UtcNow
        });
    }

    public string GetTransportName() => "FakeDesktopTransport";
}

sealed class DirectStyleGateway : IQuickBooksGateway
{
    private readonly IQuickBooksDesktopTransport _transport;
    private readonly Dictionary<string, QuickBooksPostResult> _pendingResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenRequestIds = new(StringComparer.OrdinalIgnoreCase);

    public DirectStyleGateway(IQuickBooksDesktopTransport transport)
    {
        _transport = transport;
    }

    public async Task<IReadOnlyList<QuickBooksQueueResult>> QueueAsync(
        IReadOnlyList<QuickBooksPostEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        var queueResults = new List<QuickBooksQueueResult>();
        foreach (var envelope in envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_seenRequestIds.Add(envelope.RequestId))
            {
                queueResults.Add(new QuickBooksQueueResult
                {
                    RequestId = envelope.RequestId,
                    QueuedNewRequest = false,
                    Detail = "Duplicate."
                });
                continue;
            }

            var result = await _transport.SubmitBillAsync(
                new QuickBooksDirectPostRequest
                {
                    RequestId = envelope.RequestId,
                    CompanyFileIdentifier = envelope.CompanyFileIdentifier,
                    QbXmlPayload = envelope.QbXmlPayload
                },
                cancellationToken);

            _pendingResults[envelope.RequestId] = result;
            queueResults.Add(new QuickBooksQueueResult
            {
                RequestId = envelope.RequestId,
                QueuedNewRequest = true,
                Detail = "Submitted."
            });
        }

        return queueResults;
    }

    public Task<IReadOnlyList<QuickBooksPostResult>> FetchResultsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<QuickBooksPostResult> snapshot = _pendingResults.Values.ToList();
        _pendingResults.Clear();
        return Task.FromResult(snapshot);
    }

    public string GetOutboxPath() => "direct://fake/outbox";
    public string GetInboxPath() => "direct://fake/inbox";
}

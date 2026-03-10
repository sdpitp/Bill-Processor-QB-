using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;

namespace BillProcessor.Infrastructure.QuickBooks;

public sealed class FileDropQuickBooksGateway : IQuickBooksGateway
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _baseDirectory;
    private readonly string _outboxDirectory;
    private readonly string _inboxDirectory;
    private readonly string _archiveDirectory;

    public FileDropQuickBooksGateway(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VendorBillProcessorQB",
            "qbbridge");

        _outboxDirectory = Path.Combine(_baseDirectory, "outbox");
        _inboxDirectory = Path.Combine(_baseDirectory, "inbox");
        _archiveDirectory = Path.Combine(_baseDirectory, "archive");
        EnsureDirectories();
    }

    public string GetOutboxPath() => _outboxDirectory;
    public string GetInboxPath() => _inboxDirectory;

    public async Task<QuickBooksBillPaySnapshot> GetBillPaySnapshotAsync(
        QuickBooksBillPaySyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureDirectories();

        var snapshotFile = Directory.EnumerateFiles(_inboxDirectory, "billpay-snapshot*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (snapshotFile is null)
        {
            return new QuickBooksBillPaySnapshot
            {
                SyncedAtUtc = DateTimeOffset.UtcNow,
                OperatingAccountName = request.OperatingAccountName,
                OperatingAccountBalance = 0m,
                SourceDescription = "File drop snapshot",
                WarningMessage = "No billpay-snapshot JSON file found in inbox.",
                Bills = []
            };
        }

        var payload = await File.ReadAllTextAsync(snapshotFile, cancellationToken);
        var parsed = JsonSerializer.Deserialize<BillPaySnapshotDto>(payload, SerializerOptions)
                     ?? throw new InvalidDataException("Unable to parse billpay-snapshot JSON.");

        var bills = parsed.Bills?.Select(bill => new BillRecord
        {
            Id = Guid.NewGuid(),
            VendorName = bill.VendorName ?? string.Empty,
            InvoiceNumber = bill.InvoiceNumber ?? string.Empty,
            InvoiceDate = bill.InvoiceDate,
            DueDate = bill.DueDate,
            Amount = bill.Amount,
            ExpenseAccountName = bill.ExpenseAccountName ?? "Uncategorized Expense",
            PurchaseOrderOrJobRaw = bill.PoJob ?? string.Empty,
            PurchaseOrderOrJobNormalized = bill.PoJob ?? string.Empty,
            QuickBooksTxnId = bill.QuickBooksTxnId ?? string.Empty,
            SyncedFromQuickBooks = true,
            ApprovedForPrint = false,
            Status = BillProcessingStatus.ReadyToPost
        }).ToList() ?? [];

        return new QuickBooksBillPaySnapshot
        {
            SyncedAtUtc = parsed.SyncedAtUtc ?? DateTimeOffset.UtcNow,
            OperatingAccountName = parsed.OperatingAccountName ?? request.OperatingAccountName,
            OperatingAccountBalance = parsed.OperatingAccountBalance,
            SourceDescription = "File drop snapshot",
            WarningMessage = string.Empty,
            Bills = bills
        };
    }

    public async Task<IReadOnlyList<QuickBooksQueueResult>> QueueAsync(
        IReadOnlyList<QuickBooksPostEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        EnsureDirectories();

        var results = new List<QuickBooksQueueResult>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeRequestId = MakeSafeFileName(envelope.RequestId);
            var requestPath = Path.Combine(_outboxDirectory, $"{safeRequestId}.qbxml");
            var metadataPath = Path.Combine(_outboxDirectory, $"{safeRequestId}.meta.json");

            if (File.Exists(requestPath))
            {
                results.Add(new QuickBooksQueueResult
                {
                    RequestId = envelope.RequestId,
                    QueuedNewRequest = false,
                    Detail = "Request already exists in outbox."
                });
                continue;
            }

            await File.WriteAllTextAsync(requestPath, envelope.QbXmlPayload, Encoding.UTF8, cancellationToken);
            var metadata = new QueueMetadata
            {
                RequestId = envelope.RequestId,
                BillId = envelope.BillId,
                CreatedAtUtc = envelope.CreatedAtUtc,
                PayloadHash = envelope.PayloadHash
            };
            await File.WriteAllTextAsync(
                metadataPath,
                JsonSerializer.Serialize(metadata, SerializerOptions),
                Encoding.UTF8,
                cancellationToken);

            results.Add(new QuickBooksQueueResult
            {
                RequestId = envelope.RequestId,
                QueuedNewRequest = true,
                Detail = "Queued to outbox."
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<QuickBooksPostResult>> FetchResultsAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirectories();
        var files = Directory.EnumerateFiles(_inboxDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var results = new List<QuickBooksPostResult>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                QuickBooksPostResult result = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? await ParseJsonResultAsync(file, cancellationToken)
                    : await ParseXmlResultAsync(file, cancellationToken);
                results.Add(result);
            }
            catch (Exception exception)
            {
                results.Add(new QuickBooksPostResult
                {
                    RequestId = Path.GetFileNameWithoutExtension(file),
                    Success = false,
                    Recoverable = true,
                    ErrorCode = "PARSE_ERROR",
                    ErrorMessage = exception.Message,
                    ProcessedAtUtc = DateTimeOffset.UtcNow
                });
            }
            finally
            {
                ArchiveFile(file);
            }
        }

        return results;
    }

    private async Task<QuickBooksPostResult> ParseJsonResultAsync(string path, CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllTextAsync(path, cancellationToken);
        var parsed = JsonSerializer.Deserialize<JsonResultDto>(payload, SerializerOptions)
                     ?? throw new InvalidDataException("Unable to deserialize QuickBooks JSON response.");

        if (string.IsNullOrWhiteSpace(parsed.RequestId))
        {
            throw new InvalidDataException("QuickBooks JSON response is missing requestId.");
        }

        return new QuickBooksPostResult
        {
            RequestId = parsed.RequestId,
            Success = parsed.Success,
            Recoverable = parsed.Recoverable,
            ErrorCode = parsed.ErrorCode ?? string.Empty,
            ErrorMessage = parsed.ErrorMessage ?? string.Empty,
            QuickBooksTxnId = parsed.QuickBooksTxnId ?? string.Empty,
            ProcessedAtUtc = parsed.ProcessedAtUtc ?? DateTimeOffset.UtcNow
        };
    }

    private async Task<QuickBooksPostResult> ParseXmlResultAsync(string path, CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllTextAsync(path, cancellationToken);
        var document = XDocument.Parse(payload);
        var responseElement = document.Descendants()
                                  .FirstOrDefault(element => string.Equals(
                                      element.Name.LocalName,
                                      "BillAddRs",
                                      StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidDataException("Unable to find BillAddRs in XML response.");

        var requestId = responseElement.Attribute("requestID")?.Value;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = Path.GetFileNameWithoutExtension(path);
        }

        var statusCode = responseElement.Attribute("statusCode")?.Value ?? "9999";
        var statusMessage = responseElement.Attribute("statusMessage")?.Value ?? "Unknown QuickBooks response status.";
        var success = string.Equals(statusCode, "0", StringComparison.OrdinalIgnoreCase);
        var txId = responseElement.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "TxnID", StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;

        return new QuickBooksPostResult
        {
            RequestId = requestId,
            Success = success,
            Recoverable = !success && IsRecoverableStatusCode(statusCode),
            ErrorCode = success ? string.Empty : statusCode,
            ErrorMessage = success ? string.Empty : statusMessage,
            QuickBooksTxnId = txId,
            ProcessedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static bool IsRecoverableStatusCode(string statusCode)
    {
        if (!int.TryParse(statusCode, out var code))
        {
            return true;
        }

        return code switch
        {
            3100 => false,
            3120 => false,
            3140 => false,
            _ => true
        };
    }

    private void ArchiveFile(string filePath)
    {
        var archiveName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Path.GetFileName(filePath)}";
        var destination = Path.Combine(_archiveDirectory, archiveName);
        if (File.Exists(destination))
        {
            destination = Path.Combine(_archiveDirectory, $"{Guid.NewGuid():N}_{archiveName}");
        }

        File.Move(filePath, destination);
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_baseDirectory);
        Directory.CreateDirectory(_outboxDirectory);
        Directory.CreateDirectory(_inboxDirectory);
        Directory.CreateDirectory(_archiveDirectory);
    }

    private static string MakeSafeFileName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return $"request-{Guid.NewGuid():N}";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(input.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return sanitized.Trim();
    }

    private sealed class QueueMetadata
    {
        public string RequestId { get; set; } = string.Empty;
        public Guid BillId { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string PayloadHash { get; set; } = string.Empty;
    }

    private sealed class JsonResultDto
    {
        public string RequestId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public bool Recoverable { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? QuickBooksTxnId { get; set; }
        public DateTimeOffset? ProcessedAtUtc { get; set; }
    }

    private sealed class BillPaySnapshotDto
    {
        public DateTimeOffset? SyncedAtUtc { get; set; }
        public string? OperatingAccountName { get; set; }
        public decimal OperatingAccountBalance { get; set; }
        public List<BillPayBillDto>? Bills { get; set; }
    }

    private sealed class BillPayBillDto
    {
        public string? VendorName { get; set; }
        public string? InvoiceNumber { get; set; }
        public DateTime? InvoiceDate { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal Amount { get; set; }
        public string? PoJob { get; set; }
        public string? ExpenseAccountName { get; set; }
        public string? QuickBooksTxnId { get; set; }
    }
}

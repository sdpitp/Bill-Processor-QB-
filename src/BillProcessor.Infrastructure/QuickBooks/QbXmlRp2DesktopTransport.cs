using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;
using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;

namespace BillProcessor.Infrastructure.QuickBooks;

public sealed class QbXmlRp2DesktopTransport : IQuickBooksDesktopTransport
{
    private const string RequestProcessorProgId = "QBXMLRP2.RequestProcessor";
    private const int ConnectionTypeLocalQbd = 1;
    private const int OpenModeDoNotCare = 0;
    private readonly string _appName;

    public QbXmlRp2DesktopTransport(string appName = "Vendor Bill Processor QB")
    {
        _appName = appName;
    }

    public string GetTransportName() => "QBXMLRP2";

    public Task<QuickBooksPostResult> SubmitBillAsync(
        QuickBooksDirectPostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => SubmitInternal(request), cancellationToken);
    }

    public Task<QuickBooksBillPaySnapshot> ReadBillPaySnapshotAsync(
        QuickBooksBillPaySyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ReadBillPaySnapshotInternal(request), cancellationToken);
    }

    private QuickBooksPostResult SubmitInternal(QuickBooksDirectPostRequest request)
    {
        try
        {
            return ExecuteWithSession(
                request.CompanyFileIdentifier,
                (requestProcessor, sessionTicket) =>
                {
                    var rawResponse = (string)requestProcessor.ProcessRequest(sessionTicket, request.QbXmlPayload);
                    return ParseBillAddResponse(request.RequestId, rawResponse);
                });
        }
        catch (COMException exception)
        {
            return BuildError(
                request.RequestId,
                $"COM_{exception.ErrorCode}",
                exception.Message,
                recoverable: true);
        }
        catch (Exception exception)
        {
            return BuildError(
                request.RequestId,
                "DIRECT_TRANSPORT_ERROR",
                exception.Message,
                recoverable: true);
        }
    }

    private QuickBooksBillPaySnapshot ReadBillPaySnapshotInternal(QuickBooksBillPaySyncRequest request)
    {
        return ExecuteWithSession(
            request.CompanyFileIdentifier,
            (requestProcessor, sessionTicket) =>
            {
                var billQueryResponse = (string)requestProcessor.ProcessRequest(
                    sessionTicket,
                    BuildOpenBillQueryRequest(request.MaxBillsToReturn));

                var balanceQueryResponse = (string)requestProcessor.ProcessRequest(
                    sessionTicket,
                    BuildAccountBalanceQueryRequest(request.OperatingAccountName));

                var bills = ParseOpenBillQueryResponse(billQueryResponse);
                var balance = ParseAccountBalanceQueryResponse(balanceQueryResponse, request.OperatingAccountName);

                return new QuickBooksBillPaySnapshot
                {
                    SyncedAtUtc = DateTimeOffset.UtcNow,
                    OperatingAccountName = request.OperatingAccountName,
                    OperatingAccountBalance = balance,
                    SourceDescription = "QuickBooks Desktop SDK",
                    WarningMessage = string.Empty,
                    Bills = bills
                };
            });
    }

    private T ExecuteWithSession<T>(string companyFileIdentifier, Func<dynamic, string, T> work)
    {
        var requestProcessorType = Type.GetTypeFromProgID(RequestProcessorProgId);
        if (requestProcessorType is null)
        {
            throw new InvalidOperationException(
                "QuickBooks Desktop SDK (QBXMLRP2) is not installed on this machine.");
        }

        object? processor = null;
        string? sessionTicket = null;

        try
        {
            processor = Activator.CreateInstance(requestProcessorType)
                        ?? throw new InvalidOperationException("Unable to initialize QuickBooks request processor.");

            dynamic requestProcessor = processor;
            requestProcessor.OpenConnection2(string.Empty, _appName, ConnectionTypeLocalQbd);
            sessionTicket = requestProcessor.BeginSession(ResolveCompanyFilePath(companyFileIdentifier), OpenModeDoNotCare);

            return work(requestProcessor, sessionTicket);
        }
        finally
        {
            if (processor is not null)
            {
                try
                {
                    dynamic requestProcessor = processor;
                    if (!string.IsNullOrWhiteSpace(sessionTicket))
                    {
                        requestProcessor.EndSession(sessionTicket);
                    }

                    requestProcessor.CloseConnection();
                }
                catch
                {
                    // Best effort cleanup
                }
                finally
                {
                    Marshal.FinalReleaseComObject(processor);
                }
            }
        }
    }

    private static string BuildOpenBillQueryRequest(int maxReturned)
    {
        maxReturned = Math.Clamp(maxReturned, 1, 5000);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
               "<?qbxml version=\"16.0\"?>\n" +
               "<QBXML>\n" +
               "  <QBXMLMsgsRq onError=\"continueOnError\">\n" +
               "    <BillQueryRq requestID=\"BILLPAY_SYNC\">\n" +
               "      <PaidStatus>NotPaidOnly</PaidStatus>\n" +
               "      <IncludeLineItems>false</IncludeLineItems>\n" +
               $"      <MaxReturned>{maxReturned}</MaxReturned>\n" +
               "    </BillQueryRq>\n" +
               "  </QBXMLMsgsRq>\n" +
               "</QBXML>\n";
    }

    private static string BuildAccountBalanceQueryRequest(string operatingAccountName)
    {
        var escapedAccountName = Escape(operatingAccountName);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
               "<?qbxml version=\"16.0\"?>\n" +
               "<QBXML>\n" +
               "  <QBXMLMsgsRq onError=\"continueOnError\">\n" +
               "    <AccountQueryRq requestID=\"ACCOUNT_BALANCE_SYNC\">\n" +
               $"      <FullName>{escapedAccountName}</FullName>\n" +
               "    </AccountQueryRq>\n" +
               "  </QBXMLMsgsRq>\n" +
               "</QBXML>\n";
    }

    private static List<BillRecord> ParseOpenBillQueryResponse(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return [];
        }

        var document = XDocument.Parse(responseXml);
        var billQueryResponse = document.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "BillQueryRs", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Unable to find BillQueryRs in QuickBooks response.");

        EnsureStatusSuccess(billQueryResponse, "BillQuery");
        var billResults = billQueryResponse.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "BillRet", StringComparison.OrdinalIgnoreCase));

        var records = new List<BillRecord>();
        foreach (var billResult in billResults)
        {
            var vendorName = GetNestedValue(billResult, "VendorRef", "FullName");
            var invoiceNumber = GetValue(billResult, "RefNumber");
            var invoiceDate = ParseDate(GetValue(billResult, "TxnDate"));
            var dueDate = ParseDate(GetValue(billResult, "DueDate"));
            var remainingBalance = ParseDecimal(GetValue(billResult, "BalanceRemaining"));
            if (remainingBalance <= 0m)
            {
                remainingBalance = ParseDecimal(GetValue(billResult, "AmountDue"));
            }

            var transactionId = GetValue(billResult, "TxnID");
            records.Add(new BillRecord
            {
                Id = Guid.NewGuid(),
                VendorName = vendorName,
                InvoiceNumber = string.IsNullOrWhiteSpace(invoiceNumber) ? transactionId : invoiceNumber,
                InvoiceDate = invoiceDate,
                DueDate = dueDate,
                Amount = remainingBalance,
                ExpenseAccountName = "Operating Expense",
                PurchaseOrderOrJobRaw = string.Empty,
                PurchaseOrderOrJobNormalized = string.Empty,
                QuickBooksTxnId = transactionId,
                SyncedFromQuickBooks = true,
                ApprovedForPrint = false,
                Status = BillProcessingStatus.ReadyToPost
            });
        }

        return records.OrderBy(record => record.DueDate ?? DateTime.MaxValue).ToList();
    }

    private static decimal ParseAccountBalanceQueryResponse(string responseXml, string expectedAccountName)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            throw new InvalidDataException("QuickBooks returned an empty account query response.");
        }

        var document = XDocument.Parse(responseXml);
        var accountQueryResponse = document.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "AccountQueryRs", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Unable to find AccountQueryRs in QuickBooks response.");

        EnsureStatusSuccess(accountQueryResponse, "AccountQuery");
        var accountResults = accountQueryResponse.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "AccountRet", StringComparison.OrdinalIgnoreCase))
            .ToList();

        XElement? matchedAccount = accountResults.FirstOrDefault(accountResult =>
            string.Equals(GetValue(accountResult, "FullName"), expectedAccountName, StringComparison.OrdinalIgnoreCase));

        matchedAccount ??= accountResults.FirstOrDefault();
        if (matchedAccount is null)
        {
            throw new InvalidDataException("No account rows returned from AccountQuery.");
        }

        var balanceValue = ParseDecimal(GetValue(matchedAccount, "Balance"));
        return balanceValue;
    }

    private static QuickBooksPostResult ParseBillAddResponse(string requestId, string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return BuildError(requestId, "EMPTY_RESPONSE", "QuickBooks returned an empty response.", recoverable: true);
        }

        var document = XDocument.Parse(responseXml);
        var responseElement = document.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "BillAddRs", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Unable to find BillAddRs in direct QuickBooks response.");

        var statusCode = responseElement.Attribute("statusCode")?.Value ?? "9999";
        var statusMessage = responseElement.Attribute("statusMessage")?.Value ?? "Unknown QuickBooks status.";
        var success = string.Equals(statusCode, "0", StringComparison.OrdinalIgnoreCase);
        var txId = responseElement.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "TxnID", StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;
        var responseRequestId = responseElement.Attribute("requestID")?.Value;

        return new QuickBooksPostResult
        {
            RequestId = string.IsNullOrWhiteSpace(responseRequestId) ? requestId : responseRequestId,
            Success = success,
            Recoverable = !success && IsRecoverableStatusCode(statusCode),
            ErrorCode = success ? string.Empty : statusCode,
            ErrorMessage = success ? string.Empty : statusMessage,
            QuickBooksTxnId = txId,
            ProcessedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static void EnsureStatusSuccess(XElement responseElement, string operationName)
    {
        var statusCode = responseElement.Attribute("statusCode")?.Value ?? "9999";
        if (string.Equals(statusCode, "0", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var statusMessage = responseElement.Attribute("statusMessage")?.Value ?? "Unknown status";
        throw new InvalidOperationException($"{operationName} failed with status {statusCode}: {statusMessage}");
    }

    private static string GetValue(XElement parent, string localName)
    {
        return parent.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim() ?? string.Empty;
    }

    private static string GetNestedValue(XElement parent, string containerName, string nestedName)
    {
        var container = parent.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, containerName, StringComparison.OrdinalIgnoreCase));
        if (container is null)
        {
            return string.Empty;
        }

        return container.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, nestedName, StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim() ?? string.Empty;
    }

    private static DateTime? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Date
            : null;
    }

    private static decimal ParseDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
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

    private static string ResolveCompanyFilePath(string companyFileIdentifier)
    {
        if (string.IsNullOrWhiteSpace(companyFileIdentifier))
        {
            return string.Empty;
        }

        var trimmed = companyFileIdentifier.Trim();
        if (trimmed.EndsWith(".QBW", StringComparison.OrdinalIgnoreCase) || trimmed.Contains('\\'))
        {
            return trimmed;
        }

        return string.Empty;
    }

    private static string Escape(string? value)
    {
        return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
    }

    private static QuickBooksPostResult BuildError(string requestId, string errorCode, string message, bool recoverable)
    {
        return new QuickBooksPostResult
        {
            RequestId = requestId,
            Success = false,
            Recoverable = recoverable,
            ErrorCode = errorCode,
            ErrorMessage = message,
            ProcessedAtUtc = DateTimeOffset.UtcNow
        };
    }
}

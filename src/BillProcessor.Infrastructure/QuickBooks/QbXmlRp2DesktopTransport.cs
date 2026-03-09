using System.Runtime.InteropServices;
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

    private QuickBooksPostResult SubmitInternal(QuickBooksDirectPostRequest request)
    {
        var requestProcessorType = Type.GetTypeFromProgID(RequestProcessorProgId);
        if (requestProcessorType is null)
        {
            return BuildError(
                request.RequestId,
                "SDK_NOT_INSTALLED",
                "QuickBooks Desktop SDK (QBXMLRP2) is not installed on this machine.",
                recoverable: false);
        }

        object? processor = null;
        string? sessionTicket = null;
        try
        {
            processor = Activator.CreateInstance(requestProcessorType);
            if (processor is null)
            {
                return BuildError(
                    request.RequestId,
                    "SDK_UNAVAILABLE",
                    "Unable to initialize QuickBooks request processor.",
                    recoverable: true);
            }

            dynamic rp = processor;
            rp.OpenConnection2(string.Empty, _appName, ConnectionTypeLocalQbd);
            var companyFile = ResolveCompanyFilePath(request.CompanyFileIdentifier);
            sessionTicket = rp.BeginSession(companyFile, OpenModeDoNotCare);
            var rawResponse = (string)rp.ProcessRequest(sessionTicket, request.QbXmlPayload);

            return ParseResponse(request.RequestId, rawResponse);
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
        finally
        {
            if (processor is not null)
            {
                try
                {
                    dynamic rp = processor;
                    if (!string.IsNullOrWhiteSpace(sessionTicket))
                    {
                        rp.EndSession(sessionTicket);
                    }

                    rp.CloseConnection();
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

    private static QuickBooksPostResult ParseResponse(string requestId, string responseXml)
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

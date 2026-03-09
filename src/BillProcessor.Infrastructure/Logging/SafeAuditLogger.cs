using System.Text;
using BillProcessor.Core.Models;
using BillProcessor.Infrastructure.Security;

namespace BillProcessor.Infrastructure.Logging;

public sealed class SafeAuditLogger
{
    private readonly string _logPath;

    public SafeAuditLogger(string? logPath = null)
    {
        _logPath = logPath ?? GetDefaultLogPath();
    }

    public string GetLogPath()
    {
        return _logPath;
    }

    public async Task LogProcessAsync(BillRecord bill, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bill);
        EnsureDirectoryExists();

        var logLine = $"{DateTimeOffset.UtcNow:O} status={bill.Status} " +
                      $"vendor={SensitiveDataRedactor.Redact(bill.VendorName)} " +
                      $"invoice={SensitiveDataRedactor.Redact(bill.InvoiceNumber)} " +
                      $"po={SensitiveDataRedactor.Redact(bill.PurchaseOrderOrJobNormalized, 0, 2)} " +
                      $"amount={bill.Amount:F2} errors={bill.ValidationErrors.Count}";

        await File.AppendAllTextAsync(_logPath, logLine + Environment.NewLine, Encoding.UTF8, cancellationToken);
    }

    private void EnsureDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string GetDefaultLogPath()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VendorBillProcessorQB",
            "logs");

        return Path.Combine(baseDirectory, "processor.log");
    }
}

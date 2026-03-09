using System.Globalization;
using System.Text;
using BillProcessor.Core.Models;

namespace BillProcessor.Infrastructure.Import;

public sealed class CsvBillImporter
{
    private static readonly string[] RequiredColumns = ["VendorName", "InvoiceNumber", "Amount", "PoJob"];

    public async Task<IReadOnlyList<BillRecord>> ImportAsync(string csvPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        if (!File.Exists(csvPath))
        {
            throw new FileNotFoundException("CSV file not found.", csvPath);
        }

        var lines = await File.ReadAllLinesAsync(csvPath, cancellationToken);
        if (lines.Length == 0)
        {
            return [];
        }

        var headers = ParseCsvLine(lines[0]);
        var headerMap = headers
            .Select((header, index) => new { Header = header.Trim(), Index = index })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Header))
            .ToDictionary(entry => entry.Header, entry => entry.Index, StringComparer.OrdinalIgnoreCase);

        foreach (var requiredColumn in RequiredColumns)
        {
            if (!headerMap.ContainsKey(requiredColumn))
            {
                throw new InvalidDataException(
                    $"CSV is missing required column '{requiredColumn}'. Required: {string.Join(", ", RequiredColumns)}.");
            }
        }

        var importedBills = new List<BillRecord>();
        for (var lineNumber = 2; lineNumber <= lines.Length; lineNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[lineNumber - 1];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            var record = new BillRecord
            {
                VendorName = GetField(fields, headerMap, "VendorName"),
                InvoiceNumber = GetField(fields, headerMap, "InvoiceNumber"),
                Amount = ParseAmount(GetField(fields, headerMap, "Amount")),
                PurchaseOrderOrJobRaw = GetField(fields, headerMap, "PoJob"),
                ExpenseAccountName = GetField(fields, headerMap, "ExpenseAccount"),
                InvoiceDate = ParseDate(GetField(fields, headerMap, "InvoiceDate")),
                DueDate = ParseDate(GetField(fields, headerMap, "DueDate")),
                Status = BillProcessingStatus.Imported
            };
            if (string.IsNullOrWhiteSpace(record.ExpenseAccountName))
            {
                record.ExpenseAccountName = "Uncategorized Expense";
            }

            record.AddAudit("imported", $"Imported from CSV line {lineNumber}.");
            importedBills.Add(record);
        }

        return importedBills;
    }

    private static string GetField(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> headerMap, string key)
    {
        if (!headerMap.TryGetValue(key, out var index))
        {
            return string.Empty;
        }

        return index >= 0 && index < fields.Count ? fields[index] : string.Empty;
    }

    private static decimal ParseAmount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var amountInvariant)
            ? amountInvariant
            : decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out var amountCurrent)
                ? amountCurrent
                : 0m;
    }

    private static DateTime? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsedCurrent)
            ? parsedCurrent.Date
            : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedInvariant)
                ? parsedInvariant.Date
                : null;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    currentField.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == ',' && !inQuotes)
            {
                fields.Add(currentField.ToString().Trim());
                currentField.Clear();
                continue;
            }

            currentField.Append(character);
        }

        fields.Add(currentField.ToString().Trim());
        return fields;
    }
}

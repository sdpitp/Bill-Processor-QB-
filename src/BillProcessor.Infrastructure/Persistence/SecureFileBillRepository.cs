using System.Text;
using System.Text.Json;
using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;
using BillProcessor.Infrastructure.Security;

namespace BillProcessor.Infrastructure.Persistence;

public sealed class SecureFileBillRepository : IBillRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _storageFilePath;

    public SecureFileBillRepository(string? storageFilePath = null)
    {
        _storageFilePath = storageFilePath ?? GetDefaultStoragePath();
    }

    public string GetStoragePath()
    {
        return _storageFilePath;
    }

    public async Task<IReadOnlyList<BillRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storageFilePath))
        {
            return [];
        }

        var protectedPayload = await File.ReadAllTextAsync(_storageFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(protectedPayload))
        {
            return [];
        }

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(protectedPayload);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Bill storage is corrupted (invalid base64 payload).", exception);
        }

        try
        {
            var clearBytes = DpapiProtector.Unprotect(encryptedBytes);

            var envelope = JsonSerializer.Deserialize<BillStorageEnvelope>(clearBytes, SerializerOptions);
            return envelope?.Bills ?? [];
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Unable to decrypt local bill storage for the current Windows user.",
                exception);
        }
    }

    public async Task SaveAsync(IEnumerable<BillRecord> bills, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bills);
        EnsureStorageDirectoryExists();

        var envelope = new BillStorageEnvelope
        {
            SavedAtUtc = DateTimeOffset.UtcNow,
            Bills = bills.ToList()
        };

        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        var encryptedBytes = DpapiProtector.Protect(clearBytes);
        var protectedPayload = Convert.ToBase64String(encryptedBytes);

        await File.WriteAllTextAsync(_storageFilePath, protectedPayload, Encoding.UTF8, cancellationToken);
    }

    private void EnsureStorageDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_storageFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string GetDefaultStoragePath()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VendorBillProcessorQB");

        return Path.Combine(baseDirectory, "bills.secure");
    }

    private sealed class BillStorageEnvelope
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<BillRecord> Bills { get; set; } = [];
    }
}

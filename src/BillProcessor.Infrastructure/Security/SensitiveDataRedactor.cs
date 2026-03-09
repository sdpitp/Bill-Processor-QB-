namespace BillProcessor.Infrastructure.Security;

public static class SensitiveDataRedactor
{
    public static string Redact(string? input, int visiblePrefix = 2, int visibleSuffix = 2)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim();
        visiblePrefix = Math.Max(0, visiblePrefix);
        visibleSuffix = Math.Max(0, visibleSuffix);

        if (trimmed.Length <= visiblePrefix + visibleSuffix)
        {
            return new string('*', trimmed.Length);
        }

        var prefix = visiblePrefix == 0 ? string.Empty : trimmed[..visiblePrefix];
        var suffix = visibleSuffix == 0 ? string.Empty : trimmed[^visibleSuffix..];
        return $"{prefix}{new string('*', trimmed.Length - visiblePrefix - visibleSuffix)}{suffix}";
    }
}

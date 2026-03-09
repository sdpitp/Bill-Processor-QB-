using System.Text;

namespace BillProcessor.Core.Services;

public static class PoJobNormalizer
{
    public const int RequiredDigits = 6;

    public static string Normalize(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        var preDash = rawValue.Split('-', 2, StringSplitOptions.TrimEntries)[0];
        var digits = new StringBuilder(RequiredDigits);

        foreach (var character in preDash)
        {
            if (!char.IsDigit(character))
            {
                continue;
            }

            digits.Append(character);
            if (digits.Length == RequiredDigits)
            {
                break;
            }
        }

        return digits.ToString();
    }
}

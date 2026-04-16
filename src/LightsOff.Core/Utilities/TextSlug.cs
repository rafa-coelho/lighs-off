using System.Globalization;
using System.Text;

namespace LightsOff.Core.Utilities;

public static class TextSlug
{
    public static string Slugify(string value, int maxLength = 40)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var previousDash = false;

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                previousDash = false;
            }
            else if (!previousDash && sb.Length > 0)
            {
                sb.Append('-');
                previousDash = true;
            }

            if (sb.Length >= maxLength)
            {
                break;
            }
        }

        return sb.ToString().Trim('-');
    }

    public static string BuildBranchName(string template, string ticketKey, string summary)
    {
        var slug = Slugify(summary);
        return template
            .Replace("{key}", ticketKey, StringComparison.Ordinal)
            .Replace("{slug}", slug, StringComparison.Ordinal);
    }
}

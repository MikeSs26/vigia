using System.Text;

namespace Vigia.Core;

/// <summary>
/// The identity of a time series. Labels are canonicalised to a stable string so
/// that two payloads carrying the same labels in different order resolve to one
/// series rather than two.
/// </summary>
public readonly record struct SeriesKey(
    int TenantId,
    int SourceId,
    string Name,
    string Unit,
    string CanonicalLabels)
{
    public static string CanonicaliseLabels(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null || labels.Count == 0)
        {
            return "{}";
        }

        var builder = new StringBuilder("{");
        var first = true;

        foreach (var pair in labels.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(Escape(pair.Key)).Append("\":\"")
                   .Append(Escape(pair.Value)).Append('"');
            first = false;
        }

        return builder.Append('}').ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);
}

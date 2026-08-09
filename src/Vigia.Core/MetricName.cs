namespace Vigia.Core;

/// <summary>
/// A validated metric identifier. Names are the primary driver of series
/// cardinality, so the format is restricted deliberately: lowercase only, and
/// dot-separated segments of letters, digits and underscores.
/// </summary>
public readonly record struct MetricName
{
    public const int MaxLength = 128;

    private MetricName(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? raw, out MetricName name)
    {
        name = default;

        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength)
        {
            return false;
        }

        foreach (var segment in raw.Split('.'))
        {
            if (segment.Length == 0)
            {
                return false;
            }

            foreach (var c in segment)
            {
                var valid = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '_';
                if (!valid)
                {
                    return false;
                }
            }
        }

        name = new MetricName(raw);
        return true;
    }

    public override string ToString() => Value;
}

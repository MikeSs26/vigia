using FluentValidation;
using Vigia.Api.Queue;
using Vigia.Core;

namespace Vigia.Api.Ingest;

public sealed class IngestRequestValidator : AbstractValidator<IngestRequest>
{
    /// <summary>
    /// Maximum points a single request may carry. This is the other half of
    /// the queue's memory budget alongside <see cref="QueueOptions.Capacity"/>
    /// — see the arithmetic on that property and in
    /// <see cref="QueueMemoryBudget"/>. Raising this number without also
    /// revisiting Capacity can silently blow that budget; the guard test
    /// (QueueMemoryBudgetTests, in Vigia.Core.Tests) exists specifically to
    /// catch that.
    /// </summary>
    public const int MaxPointsPerBatch = 1_000;
    public const int MaxLabels = 8;
    public const int MaxLabelKeyLength = 64;
    public const int MaxLabelValueLength = 128;

    /// <summary>
    /// Maximum characters of label text — every key plus every value, summed —
    /// a single point may carry.
    ///
    /// <see cref="MaxLabels"/>, <see cref="MaxLabelKeyLength"/> and
    /// <see cref="MaxLabelValueLength"/> bound each dimension separately, and
    /// their product is what a queued point can actually cost: 8 x (64 + 128)
    /// = 1,536 characters, measured at 4,504 retained bytes per point. At that
    /// figure no useful combination of queue capacity and batch size fits the
    /// container's memory limit, so the queue's bound stops being a bound and
    /// the process reaches an OOM kill before it ever sheds load with a 429.
    /// This cap is what makes the worst case computable and small: it is the
    /// single number <see cref="QueueMemoryBudget.WorstCaseBytesPerPoint"/> is
    /// measured against.
    ///
    /// 256 characters across at most 8 labels is 32 characters per label —
    /// several times what real label sets use (region, environment, instance
    /// id and the like), while cutting the worst case from 4,504 to 1,944
    /// bytes per point. The per-key and per-value caps stay: they keep any one
    /// label from consuming the whole allowance, and label text becomes part of
    /// a database uniqueness key, which wants its own bound regardless.
    /// </summary>
    public const int MaxTotalLabelChars = 256;

    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public IngestRequestValidator(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();

        RuleFor(r => r.Source)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(r => r.Points)
            .NotEmpty().WithMessage("A batch must contain at least one point.")
            .Must(p => p.Count <= MaxPointsPerBatch)
            .WithMessage($"A batch may not exceed {MaxPointsPerBatch} points.");

        RuleForEach(r => r.Points).ChildRules(point =>
        {
            point.RuleFor(p => p.Name)
                .Must(n => MetricName.TryCreate(n, out _))
                .WithMessage("Metric names must be lowercase, dot-separated segments of [a-z0-9_].");

            point.RuleFor(p => p.Unit).NotEmpty().MaximumLength(32);

            point.RuleFor(p => p.Value)
                .Must(v => !double.IsNaN(v) && !double.IsInfinity(v))
                .WithMessage("Values must be finite.");

            // A clock-skewed agent would otherwise create partitions years ahead,
            // and a replayed dump would resurrect data past its retention horizon.
            point.RuleFor(p => p.Ts)
                .Must(ts => ts <= now + MaxFutureSkew)
                .WithMessage("Timestamp is too far in the future.")
                .Must(ts => ts >= now - MaxAge)
                .WithMessage("Timestamp is older than the retention horizon.");

            point.RuleFor(p => p.Labels!)
                .Must(l => l is null || l.Count <= MaxLabels)
                .WithMessage($"At most {MaxLabels} labels per point.")
                // Label text becomes part of the series identity and therefore
                // part of a database uniqueness key, so it needs the same kind
                // of bound Source and Unit already have.
                .Must(l => l is null || l.All(kv =>
                    kv.Key.Length <= MaxLabelKeyLength && kv.Value.Length <= MaxLabelValueLength))
                .WithMessage(
                    $"Label keys must be at most {MaxLabelKeyLength} characters " +
                    $"and values at most {MaxLabelValueLength} characters.")
                // The cap that makes the queue's memory bound honest: without
                // it the caps above still permit 1,536 characters of label text
                // per point. See MaxTotalLabelChars.
                .Must(l => l is null || TotalLabelChars(l) <= MaxTotalLabelChars)
                .WithMessage(
                    $"Label keys and values combined must be at most " +
                    $"{MaxTotalLabelChars} characters per point.");
        });
    }

    private static int TotalLabelChars(Dictionary<string, string> labels)
    {
        var total = 0;
        foreach (var (key, value) in labels)
        {
            total += key.Length + value.Length;
        }

        return total;
    }
}

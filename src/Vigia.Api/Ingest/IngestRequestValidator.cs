using FluentValidation;
using Vigia.Core;

namespace Vigia.Api.Ingest;

public sealed class IngestRequestValidator : AbstractValidator<IngestRequest>
{
    public const int MaxPointsPerBatch = 10_000;
    public const int MaxLabels = 8;

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
                .WithMessage($"At most {MaxLabels} labels per point.");
        });
    }
}

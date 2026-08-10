using Vigia.Core;

namespace Vigia.Infrastructure.Writing;

public interface IMetricWriter
{
    /// <summary>Persists a batch. Returns the number of rows written.</summary>
    Task<int> WriteAsync(IReadOnlyList<ResolvedPoint> points, CancellationToken cancellationToken);
}

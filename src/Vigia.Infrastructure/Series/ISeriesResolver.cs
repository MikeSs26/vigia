using Vigia.Core;

namespace Vigia.Infrastructure.Series;

public interface ISeriesResolver
{
    Task<int> ResolveAsync(SeriesKey key, CancellationToken cancellationToken);

    int CachedCount { get; }
}

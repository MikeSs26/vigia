using Vigia.Infrastructure.Rollups;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class RollupWatermarkStoreTests(PostgresFixture postgres)
{
    private readonly string _granularity = $"test-{Guid.NewGuid():N}";

    private PostgresRollupWatermarkStore Store() => new(postgres.ConnectionString);

    [Fact]
    public async Task AnAbsentWatermarkReadsAsNull()
    {
        Assert.Null(await Store().ReadAsync(_granularity, default));
    }

    [Fact]
    public async Task AWrittenWatermarkReadsBackInUtc()
    {
        var watermark = new DateTimeOffset(2031, 6, 1, 12, 0, 0, TimeSpan.Zero);

        await Store().WriteAsync(_granularity, watermark, watermark, default);

        var read = await Store().ReadAsync(_granularity, default);

        Assert.NotNull(read);
        Assert.Equal(watermark, read!.Value);
        Assert.Equal(TimeSpan.Zero, read.Value.Offset);
    }

    [Fact]
    public async Task WritingTwiceUpdatesRatherThanDuplicating()
    {
        var first = new DateTimeOffset(2031, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var second = first.AddHours(1);

        await Store().WriteAsync(_granularity, first, first, default);
        await Store().WriteAsync(_granularity, second, second, default);

        Assert.Equal(second, (await Store().ReadAsync(_granularity, default))!.Value);
    }

    [Fact]
    public async Task AWatermarkWithANonZeroOffsetIsNormalisedRatherThanRejected()
    {
        // Npgsql refuses to write a non-zero offset to timestamptz. Normalising in
        // the store means a caller that forgets cannot produce a runtime failure
        // inside a background worker hours after deployment.
        var madrid = new DateTimeOffset(2031, 6, 1, 14, 0, 0, TimeSpan.FromHours(2));

        await Store().WriteAsync(_granularity, madrid, madrid, default);

        Assert.Equal(madrid.ToUniversalTime(), (await Store().ReadAsync(_granularity, default))!.Value);
    }
}

using Vigia.Api.Ingest;
using Vigia.Api.Queue;

namespace Vigia.Core.Tests;

/// <summary>
/// C2: the queue bounds batches, not memory. A full queue retains
/// QueueOptions.Capacity batches of up to IngestRequestValidator.MaxPointsPerBatch
/// points each, and if that product isn't kept well under the documented byte
/// budget, the api container gets OOM-killed before the queue ever reports
/// saturation — defeating the entire point of bounding it. This test pins
/// that relationship so a future edit to either number that reintroduces the
/// gap fails loudly here instead of silently in production.
/// </summary>
public class QueueMemoryBudgetTests
{
    [Fact]
    public void ConfiguredCapacityTimesMaxBatchSizeStaysUnderTheDocumentedByteBudget()
    {
        var capacity = new QueueOptions().Capacity;
        var maxPointsPerBatch = IngestRequestValidator.MaxPointsPerBatch;

        var worstCase = QueueMemoryBudget.WorstCaseBytes(capacity, maxPointsPerBatch);

        Assert.True(worstCase <= QueueMemoryBudget.MaxRetainedBytes,
            $"QueueOptions.Capacity ({capacity}) x IngestRequestValidator.MaxPointsPerBatch " +
            $"({maxPointsPerBatch}) x {QueueMemoryBudget.WorstCaseBytesPerPoint} " +
            $"bytes/point = {worstCase:N0} bytes, which exceeds the " +
            $"{QueueMemoryBudget.MaxRetainedBytes:N0}-byte budget. Retune one or both " +
            "settings, or raise the budget deliberately and document why.");
    }
}

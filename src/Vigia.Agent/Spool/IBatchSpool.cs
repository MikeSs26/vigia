namespace Vigia.Agent.Spool;

public readonly record struct SpooledBatch(string Path, string Payload, DateTimeOffset ParkedAt);

/// <summary>
/// Durable overflow storage for batches the API would not accept yet.
///
/// Taking a batch does not remove it — only <see cref="Discard"/> does. That
/// split is deliberate: a crash between reading a batch and having it accepted
/// must leave the batch on disk, or the spool loses precisely the data it exists
/// to protect.
/// </summary>
public interface IBatchSpool
{
    void Park(string payload, DateTimeOffset parkedAt);

    bool TryTakeOldest(out SpooledBatch batch);

    void Discard(SpooledBatch batch);

    int Count { get; }
}

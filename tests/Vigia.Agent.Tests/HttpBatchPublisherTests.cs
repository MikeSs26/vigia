using System.Net;
using Microsoft.Extensions.Logging;
using Vigia.Agent.Publishing;

namespace Vigia.Agent.Tests;

public class HttpBatchPublisherTests
{
    [Fact]
    public async Task Returns202YieldsAccepted()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));
        using var client = NewClient(handler);
        var publisher = new HttpBatchPublisher(client, new RecordingLogger<HttpBatchPublisher>());

        var outcome = await publisher.PublishAsync("{}", CancellationToken.None);

        Assert.Equal(PublishOutcome.Accepted, outcome);
    }

    [Fact]
    public async Task Returns400YieldsRejectedAndLogsTheResponseBody()
    {
        const string body = "metric name 'cpu usage' contains a space";
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body),
            }));
        using var client = NewClient(handler);
        var logger = new RecordingLogger<HttpBatchPublisher>();
        var publisher = new HttpBatchPublisher(client, logger);

        var outcome = await publisher.PublishAsync("{}", CancellationToken.None);

        Assert.Equal(PublishOutcome.Rejected, outcome);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains(body));
    }

    [Fact]
    public async Task ConnectionFailureYieldsRetry()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
        using var client = NewClient(handler);
        var publisher = new HttpBatchPublisher(client, new RecordingLogger<HttpBatchPublisher>());

        var outcome = await publisher.PublishAsync("{}", CancellationToken.None);

        Assert.Equal(PublishOutcome.Retry, outcome);
    }

    [Fact]
    public async Task InternalTimeoutYieldsRetryRatherThanPropagating()
    {
        // The handler never returns on its own; only HttpClient.Timeout ends the
        // wait. The caller's token (CancellationToken.None) is never signalled,
        // so this must be distinguished from a caller cancellation and caught.
        using var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var client = NewClient(handler);
        client.Timeout = TimeSpan.FromMilliseconds(50);
        var publisher = new HttpBatchPublisher(client, new RecordingLogger<HttpBatchPublisher>());

        var outcome = await publisher.PublishAsync("{}", CancellationToken.None);

        Assert.Equal(PublishOutcome.Retry, outcome);
    }

    [Fact]
    public async Task CallerCancellationPropagatesRatherThanBeingSwallowedAsRetry()
    {
        // Same exception type as the timeout case (TaskCanceledException), but
        // this time it is the caller's own token that fires — while the handler
        // is in flight, mirroring a shutdown mid-request. The catch filter must
        // let this one through rather than reporting it as a retryable failure.
        using var cts = new CancellationTokenSource();
        using var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var client = NewClient(handler);
        var publisher = new HttpBatchPublisher(client, new RecordingLogger<HttpBatchPublisher>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishAsync("{}", cts.Token));
    }

    private static HttpClient NewClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://127.0.0.1") };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

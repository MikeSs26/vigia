using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigia.Infrastructure.Notifications;

namespace Vigia.Core.Tests;

/// <summary>
/// The Discord webhook URL is a bearer credential: whoever holds it can post to
/// the channel. The design keeps it out of the database and out of source control,
/// so it must not leak into stdout either, which Docker captures and retains.
/// </summary>
public class WebhookLoggingTests
{
    private const string Token = "TESTWEBHOOKTOKEN9f3a2c";
    private const string WebhookUrl = $"https://example.invalid/api/webhooks/12345/{Token}";

    [Fact]
    public async Task PublishingNeverWritesTheWebhookUrlToTheLogs()
    {
        // IHttpClientFactory installs logging handlers that emit the FULL request
        // URI at Information ("Start processing HTTP request POST <uri>"), so the
        // credential reaches the log unless those handlers are removed.
        var captured = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(captured);
        });

        // Registered exactly as src/Vigia.Api/Program.cs registers it.
        services.AddHttpClient<IWebhookPublisher, DiscordWebhookPublisher>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(
                () => new NoContentHandler());

        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IWebhookPublisher>();

        Assert.Equal(
            PublishOutcome.Delivered,
            await publisher.PublishAsync(WebhookUrl, "{}", default));

        Assert.DoesNotContain(
            captured.Entries,
            entry => entry.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public void TheApiRegistersTheWebhookClientWithItsLoggersRemoved()
    {
        // The test above proves RemoveAllLoggers() suppresses the leak; this one
        // proves the shipping host actually applies it. Without this the production
        // registration could lose the call and nothing would notice.
        var program = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "src", "Vigia.Api", "Program.cs"));

        var start = program.IndexOf(
            "AddHttpClient<IWebhookPublisher, DiscordWebhookPublisher>",
            StringComparison.Ordinal);

        Assert.True(start >= 0, "Program.cs no longer registers the typed webhook client.");

        var tail = program[start..];
        var next = tail.IndexOf("\nbuilder.Services.", StringComparison.Ordinal);
        var registration = next >= 0 ? tail[..next] : tail;

        Assert.Contains("RemoveAllLoggers()", registration, StringComparison.Ordinal);
    }
}

file sealed class NoContentHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
}

/// <summary>
/// Records every formatted message and every state rendering, because the URI can
/// reach a sink through either the message template or the structured state.
/// </summary>
file sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            owner.Add(state.ToString() ?? string.Empty);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            owner.Add(formatter(state, exception));
            owner.Add(state?.ToString() ?? string.Empty);
        }
    }
}

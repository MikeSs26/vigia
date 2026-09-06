using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vigia.Api.Workers;

namespace Vigia.Integration.Tests;

public class AlertingOptionsBindingTests
{
    [Fact]
    public void WebhookUrlsBindFromTheEnvironmentByChannelName()
    {
        // The URL is a publishing credential and never enters the database, so
        // the binding path from the environment is what makes a channel usable.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifier:ChannelWebhooks:ops"] = "https://example.invalid/hook",
                ["Notifier:MaxAttempts"] = "4",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<NotifierOptions>(configuration.GetSection(NotifierOptions.SectionName));

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<NotifierOptions>>().Value;

        Assert.Equal("https://example.invalid/hook", options.ChannelWebhooks["ops"]);
        Assert.Equal(4, options.MaxAttempts);
    }

    [Fact]
    public void TheDefaultsAreTheDocumentedOnes()
    {
        var options = new NotifierOptions();

        Assert.Equal(10, options.IntervalSeconds);
        Assert.Equal(8, options.MaxAttempts);
        Assert.Equal(5_000, options.MaxOutboxRows);
    }

    [Fact]
    public void TheRuleWindowCapIsSixHours()
    {
        Assert.Equal(TimeSpan.FromHours(6), new AlertOptions().MaxRuleWindow);
    }
}

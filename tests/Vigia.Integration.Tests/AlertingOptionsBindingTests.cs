using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vigia.Api.Workers;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class AlertingOptionsBindingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task TheAlertingSectionBindsFromConfiguration()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            // 123 is clearly different from AlertOptions.IntervalSeconds' class
            // default of 30: this only passes if Program.cs genuinely binds the
            // Alerting section, not if IOptions<AlertOptions> is resolving the
            // class's own hardcoded defaults.
            builder.UseSetting("Alerting:IntervalSeconds", "123");
        });

        var options = factory.Services.GetRequiredService<IOptions<AlertOptions>>().Value;

        Assert.Equal(123, options.IntervalSeconds);
    }

    [Fact]
    public async Task TheNotifierSectionBindsFromConfigurationIncludingWebhooks()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            builder.UseSetting("Notifier:MaxAttempts", "42");
            builder.UseSetting("Notifier:MaxOutboxRows", "77");
            // The webhook URL never enters appsettings.json — it is a publishing
            // credential supplied through the environment. This pins the binding
            // path that makes that possible.
            builder.UseSetting("Notifier:ChannelWebhooks:ops", "https://example.invalid/hook");
        });

        var options = factory.Services.GetRequiredService<IOptions<NotifierOptions>>().Value;

        Assert.Equal(42, options.MaxAttempts);
        Assert.Equal(77, options.MaxOutboxRows);
        Assert.Equal("https://example.invalid/hook", options.ChannelWebhooks["ops"]);
    }

    [Fact]
    public async Task BothAlertingWorkersAreRegisteredWithTheHost()
    {
        // The actual deliverable of this task. Binding options in a hand-built
        // ServiceCollection proves the options classes work and says nothing
        // about whether the application ever starts these workers — delete
        // either AddHostedService line and this is the test that notices.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString));

        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, service => service is AlertWorker);
        Assert.Contains(hosted, service => service is NotifierWorker);
    }

    [Fact]
    public void TheRuleWindowCapIsSixHours()
    {
        // Alerts read raw points, which are retained seven days. This cap is what
        // keeps every alert query inside that horizon.
        Assert.Equal(TimeSpan.FromHours(6), new AlertOptions().MaxRuleWindow);
    }
}

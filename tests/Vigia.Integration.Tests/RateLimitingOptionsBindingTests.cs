using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vigia.Api.RateLimiting;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class RateLimitingOptionsBindingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RateLimitingSectionBindsFromConfiguration()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            // 7 is clearly different from RateLimitingOptions.PermitLimit's class
            // default of 120: this only passes if Program.cs genuinely binds the
            // RateLimiting section, not if IOptions<RateLimitingOptions> is
            // silently resolving the class's own hardcoded defaults.
            builder.UseSetting("RateLimiting:PermitLimit", "7");
        });

        var options = factory.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        Assert.Equal(7, options.PermitLimit);
    }
}

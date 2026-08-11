using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vigia.Api.Workers;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class MaintenanceOptionsBindingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MaintenanceSectionBindsFromConfiguration()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            // 99 is clearly different from MaintenanceOptions.RawRetentionDays'
            // class default of 7: this only passes if Program.cs genuinely binds
            // the Maintenance section, not if IOptions<MaintenanceOptions> is
            // silently resolving the class's own hardcoded defaults.
            builder.UseSetting("Maintenance:RawRetentionDays", "99");
        });

        var options = factory.Services.GetRequiredService<IOptions<MaintenanceOptions>>().Value;

        Assert.Equal(99, options.RawRetentionDays);
    }
}

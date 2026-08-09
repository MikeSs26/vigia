using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vigia.Infrastructure;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VigiaDbContext>
{
    public VigiaDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("VIGIA_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=vigia;Username=vigia;Password=vigia";

        var options = new DbContextOptionsBuilder<VigiaDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new VigiaDbContext(options);
    }
}

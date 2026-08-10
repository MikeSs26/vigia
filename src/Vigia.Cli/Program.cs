using Microsoft.EntityFrameworkCore;
using Vigia.Cli;
using Vigia.Infrastructure;

var connectionString = Environment.GetEnvironmentVariable("VIGIA_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set VIGIA_CONNECTION to the PostgreSQL connection string.");
    return 1;
}

var options = new DbContextOptionsBuilder<VigiaDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var context = new VigiaDbContext(options);
var now = TimeProvider.System.GetUtcNow();

return await CliRunner.RunAsync(args, context, now, Console.Out, Console.Error, default);

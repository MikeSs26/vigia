using Microsoft.Extensions.Options;
using Serilog;
using Vigia.Agent;
using Vigia.Agent.Collection;
using Vigia.Agent.Publishing;
using Vigia.Agent.Spool;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) =>
    configuration.ReadFrom.Configuration(builder.Configuration).WriteTo.Console());

builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMetricCollector, ProcMetricCollector>();

builder.Services.AddSingleton<IBatchSpool>(provider =>
{
    var options = provider.GetRequiredService<IOptions<AgentOptions>>().Value;
    return new FileBatchSpool(
        options.SpoolDirectory,
        options.SpoolMaxBatches,
        provider.GetRequiredService<ILogger<FileBatchSpool>>());
});

builder.Services.AddHttpClient<IBatchPublisher, HttpBatchPublisher>((provider, client) =>
{
    var options = provider.GetRequiredService<IOptions<AgentOptions>>().Value;

    client.BaseAddress = new Uri(options.Endpoint);
    client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);

    // Shorter than the reporting interval, so a stalled request cannot make
    // cycles overlap and pile up.
    client.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.IntervalSeconds - 2));
});

builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();

using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Vigia.Api.Auth;
using Vigia.Api.Ingest;
using Vigia.Api.Queue;
using Vigia.Api.Querying;
using Vigia.Api.RateLimiting;
using Vigia.Api.Workers;
using Vigia.Core;
using Vigia.Core.Querying;
using Vigia.Infrastructure;
using Vigia.Infrastructure.Alerting;
using Vigia.Infrastructure.Auth;
using Vigia.Infrastructure.Notifications;
using Vigia.Infrastructure.Partitions;
using Vigia.Infrastructure.Querying;
using Vigia.Infrastructure.Rollups;
using Vigia.Infrastructure.Series;
using Vigia.Infrastructure.Writing;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).WriteTo.Console());

var connectionString = builder.Configuration.GetConnectionString("Vigia")
    ?? throw new InvalidOperationException("ConnectionStrings:Vigia is not configured.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<VigiaDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.Configure<IngestionOptions>(
    builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.Configure<MaintenanceOptions>(
    builder.Configuration.GetSection(MaintenanceOptions.SectionName));
builder.Services.Configure<RateLimitingOptions>(
    builder.Configuration.GetSection(RateLimitingOptions.SectionName));
builder.Services.Configure<RollupOptions>(
    builder.Configuration.GetSection(RollupOptions.SectionName));
builder.Services.Configure<AlertOptions>(
    builder.Configuration.GetSection(AlertOptions.SectionName));
builder.Services.Configure<NotifierOptions>(
    builder.Configuration.GetSection(NotifierOptions.SectionName));
builder.Services.AddVigiaRateLimiting();

builder.Services.AddSingleton<IMetricQueue, BoundedChannelMetricQueue>();
builder.Services.AddSingleton<ISeriesResolver>(_ => new SeriesResolver(connectionString));
builder.Services.AddSingleton<ISourceResolver>(_ => new SourceResolver(connectionString));
builder.Services.AddSingleton<IMetricWriter>(sp => new NpgsqlCopyMetricWriter(
    connectionString, sp.GetRequiredService<ILogger<NpgsqlCopyMetricWriter>>()));
builder.Services.AddSingleton<IIngestionMetrics, IngestionMetrics>();
builder.Services.AddSingleton<IPartitionMaintenance>(
    _ => new PostgresPartitionMaintenance(connectionString));

builder.Services.AddSingleton(new GranularityResolver(QueryLimits.Default));
builder.Services.AddSingleton<IMetricQueryReader>(_ => new PostgresMetricQueryReader(connectionString));
builder.Services.AddSingleton<IRollupWatermarkStore>(
    _ => new PostgresRollupWatermarkStore(connectionString));
builder.Services.AddSingleton<IRollupAggregator>(
    _ => new PostgresRollupAggregator(connectionString));

builder.Services.AddSingleton<IAlertStore>(_ => new PostgresAlertStore(connectionString));
builder.Services.AddSingleton<IOutboxStore>(_ => new PostgresOutboxStore(connectionString));
builder.Services.AddHttpClient<IWebhookPublisher, DiscordWebhookPublisher>(client =>
{
    // A hung webhook must not hold a drain cycle open.
    client.Timeout = TimeSpan.FromSeconds(10);
})
    // IHttpClientFactory's default logging handlers write the full request URI at
    // Information. For this client the URI *is* the credential: whoever holds the
    // Discord webhook URL can post to the channel. The whole design keeps it out of
    // the database and out of source control, so it must not land in stdout either,
    // which Docker captures and retains. Removing the loggers here is scoped to this
    // client and survives someone raising log levels elsewhere.
    .RemoveAllLoggers();

builder.Services.AddScoped<IApiKeyLookup, ApiKeyLookup>();
builder.Services.AddScoped<IValidator<IngestRequest>, IngestRequestValidator>();

builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHostedService<RollupWorker>();
builder.Services.AddHostedService<AlertWorker>();
builder.Services.AddHostedService<NotifierWorker>();

builder.Services
    .AddAuthentication(ApiKeyDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyDefaults.Scheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiKeyScopes.Ingest, policy => policy.RequireAssertion(context =>
        ApiKeyScopes.Satisfies(
            context.User.FindFirst(ApiKeyDefaults.ScopeClaim)?.Value ?? string.Empty,
            ApiKeyScopes.Ingest)))
    .AddPolicy(ApiKeyScopes.Read, policy => policy.RequireAssertion(context =>
        ApiKeyScopes.Satisfies(
            context.User.FindFirst(ApiKeyDefaults.ScopeClaim)?.Value ?? string.Empty,
            ApiKeyScopes.Read)))
    .AddPolicy(ApiKeyScopes.Control, policy => policy.RequireAssertion(context =>
        ApiKeyScopes.Satisfies(
            context.User.FindFirst(ApiKeyDefaults.ScopeClaim)?.Value ?? string.Empty,
            ApiKeyScopes.Control)));

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapIngest();
app.MapSeries();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

/// <summary>Exposed so WebApplicationFactory can boot this host in tests.</summary>
public partial class Program;

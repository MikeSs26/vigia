using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Vigia.Api.Auth;
using Vigia.Api.Ingest;
using Vigia.Api.Queue;
using Vigia.Api.Workers;
using Vigia.Core;
using Vigia.Infrastructure;
using Vigia.Infrastructure.Auth;
using Vigia.Infrastructure.Partitions;
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

builder.Services.AddSingleton<IMetricQueue, BoundedChannelMetricQueue>();
builder.Services.AddSingleton<ISeriesResolver>(_ => new SeriesResolver(connectionString));
builder.Services.AddSingleton<ISourceResolver>(_ => new SourceResolver(connectionString));
builder.Services.AddSingleton<IMetricWriter>(_ => new NpgsqlCopyMetricWriter(connectionString));
builder.Services.AddSingleton<IPartitionMaintenance>(
    _ => new PostgresPartitionMaintenance(connectionString));

builder.Services.AddScoped<IApiKeyLookup, ApiKeyLookup>();
builder.Services.AddScoped<IValidator<IngestRequest>, IngestRequestValidator>();

// MaintenanceWorker is registered in Task 13, once it exists.
builder.Services.AddHostedService<IngestionWorker>();

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

app.MapIngest();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

/// <summary>Exposed so WebApplicationFactory can boot this host in tests.</summary>
public partial class Program;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Vigia.Infrastructure;

namespace Vigia.Integration.Tests;

/// <summary>
/// One PostgreSQL container shared by every integration test class. Starting a
/// container per class would multiply a multi-second cost across the suite.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("vigia")
        .WithUsername("vigia")
        .WithPassword("vigia")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public VigiaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<VigiaDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new VigiaDbContext(options);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

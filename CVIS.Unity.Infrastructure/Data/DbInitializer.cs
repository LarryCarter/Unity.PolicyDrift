using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using CVIS.Unity.Core.Interfaces;

namespace CVIS.Unity.Infrastructure.Data;

public class DbInitializer
{
    private readonly PolicyDbContext _context;
    private readonly IConfiguration _config;
    private readonly IUnityEventPublisher _publisher;

    public DbInitializer(PolicyDbContext context, IConfiguration config, IUnityEventPublisher publisher)
    {
        _context = context;
        _config = config;
        _publisher = publisher;
    }

    public async Task InitializeAsync()
    {
        var schema = _config["Infrastructure:DbSchema"] ?? "unity";

        // 1. Ensure Schema exists first. 
        // Migrations will fail if the schema in [Table("Name", Schema="unity")] is missing.
        await _context.Database.ExecuteSqlRawAsync(
            $"IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{schema}') " +
            $"BEGIN EXEC('CREATE SCHEMA {schema}') END");

        // 2. Apply Migrations
        // This is the "Smart" way. It checks what's already there and only applies diffs.
        _publisher.LogInfo("Checking for pending migrations...");

        var pendingMigrations = await _context.Database.GetPendingMigrationsAsync();
        if (pendingMigrations.Any())
        {
            _publisher.LogInfo($"Applying {pendingMigrations.Count()} pending migrations to {schema}...");
            await _context.Database.MigrateAsync();
            _publisher.LogInfo("Database schema is now up to date.");
        }
        else
        {
            _publisher.LogInfo("Database schema is already at the latest version.");
        }
    }
}
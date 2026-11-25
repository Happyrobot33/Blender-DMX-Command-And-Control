using FluentMigrator.Runner;
using MySqlConnector;

namespace Server.Data;

public static class DatabaseBootstrapper
{
    public const string ConnectionStringName = "JobsDb";

    public static void InitializeDatabase(this WebApplication app)
    {
        var connectionString = app.Configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        if (app.Configuration.GetValue("Database:CreateIfMissing", true))
            EnsureDatabaseExists(connectionString, app.Logger);

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    private static void EnsureDatabaseExists(string connectionString, ILogger logger)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = builder.Database;
        if (string.IsNullOrEmpty(databaseName))
            throw new InvalidOperationException($"Connection string '{ConnectionStringName}' must specify a Database.");

        builder.Database = string.Empty;
        using var connection = new MySqlConnection(builder.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE DATABASE IF NOT EXISTS `{databaseName.Replace("`", "``")}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
        if (command.ExecuteNonQuery() > 0)
            logger.LogInformation("Created database {Database}", databaseName);
    }
}

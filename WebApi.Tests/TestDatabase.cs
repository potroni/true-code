using Npgsql;

namespace WebApi.Tests;

[TestClass]
public static class TestDatabase
{
    private const string DefaultServerConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=club;Password=club";

    private const string DefaultRedisConnectionString = "localhost:6380,defaultDatabase=1";

    private static readonly string ServerConnectionString =
        Environment.GetEnvironmentVariable("TEST_POSTGRES") ?? DefaultServerConnectionString;

    private static string? _databaseName;

    public static string PostgresConnectionString { get; private set; } = null!;

    public static string RedisConnectionString { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        RedisConnectionString =
            Environment.GetEnvironmentVariable("TEST_REDIS") ?? DefaultRedisConnectionString;

        _databaseName = $"club_tests_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
        await command.ExecuteNonQueryAsync();

        PostgresConnectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString)
        {
            Database = _databaseName
        }.ConnectionString;
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        if (_databaseName == null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}

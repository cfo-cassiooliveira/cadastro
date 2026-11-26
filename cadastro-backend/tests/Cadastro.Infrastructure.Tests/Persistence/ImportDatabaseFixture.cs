using Cadastro.Infrastructure.Persistence;
using Cadastro.Tests.Shared;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace Cadastro.Infrastructure.Tests.Persistence;

/// <summary>
/// SQL Server real (Testcontainers) imitando o ambiente do Legado: banco <c>LegadoUsers</c> com uma tabela no <c>dbo</c>, o
/// esquema <c>import</c> e os dois logins. Em produção o agente do Legado cria esquema e logins; aqui o fixture faz o mesmo
/// (só o essencial, com os mesmos GRANTs do create-import-schema.sql do Legado) para provar que o Cadastro cria as tabelas como migrator e opera como app sem tocar no <c>dbo</c>.
/// </summary>
public sealed class ImportDatabaseFixture : IAsyncLifetime
{
    // Mesma imagem (tag + digest) do SQL Server do Legado.
    private const string Image = "mcr.microsoft.com/mssql/server:2025-latest@sha256:05644dad1266ca3d445d1e8f6d2ebb9a6a03df9a39376e0f7b48917356dca9d9";
    private const string Database = "LegadoUsers";

    // Senhas só deste processo de teste, no formato exigido pela política do SQL Server.
    private readonly string _migratorPassword = $"Mg{Guid.NewGuid():N}!a1";
    private readonly string _appPassword = $"Ap{Guid.NewGuid():N}!a1";
    private MsSqlContainer _container = new MsSqlBuilder(Image).Build();

    /// <summary>Connection string do login <c>cadastro_migrator</c>.</summary>
    public string MigratorConnectionString { get; private set; } = string.Empty;

    /// <summary>Connection string do login <c>cadastro_app</c> (runtime).</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    /// <summary>Aplica as migrations como <c>cadastro_migrator</c> (idempotente).</summary>
    public async Task MigrateAsync()
    {
        await using var context = CreateContext(MigratorConnectionString);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Cria um contexto para a connection string indicada.</summary>
    public static CadastroDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CadastroDbContext>()
            .UseSqlServer(connectionString, CadastroDbContext.ConfigureSqlServer)
            .Options);

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // Um container por vez entre os projetos de teste (evita a disputa de recursos do Docker).
        using (await ContainerStartLock.AcquireAsync(TestContext.Current.CancellationToken))
        {
            try
            {
                await _container.StartAsync(TestContext.Current.CancellationToken);
            }
            catch (ContainerNotRunningException)
            {
                // O SQL Server às vezes sai ao subir sob pressão de memória do Docker: uma segunda tentativa, num container novo.
                // Falha real (imagem, senha, config) repete e a segunda exceção sobe para o teste.
                await _container.DisposeAsync();
                _container = new MsSqlBuilder(Image).Build();
                await _container.StartAsync(TestContext.Current.CancellationToken);
            }
        }

        var admin = new SqlConnectionStringBuilder(_container.GetConnectionString());
        await ExecuteAsync(admin.ConnectionString, $"CREATE DATABASE [{Database}]");

        MigratorConnectionString = ConnectionFor(admin, "cadastro_migrator", _migratorPassword);
        AppConnectionString = ConnectionFor(admin, "cadastro_app", _appPassword);

        admin.InitialCatalog = Database;
        var setup = $"""
            CREATE TABLE dbo.legacy_probe (Id int NOT NULL PRIMARY KEY, Email nvarchar(200) NOT NULL);
            INSERT INTO dbo.legacy_probe VALUES (1, N'legacy@example.com');
            """;
        await ExecuteAsync(admin.ConnectionString, setup);
        // CREATE SCHEMA exige ser o primeiro comando do batch.
        await ExecuteAsync(admin.ConnectionString, "CREATE SCHEMA [import]");

        await ExecuteAsync(_container.GetConnectionString(), $"""
            CREATE LOGIN cadastro_migrator WITH PASSWORD = '{_migratorPassword}', CHECK_POLICY = OFF;
            CREATE LOGIN cadastro_app WITH PASSWORD = '{_appPassword}', CHECK_POLICY = OFF;
            """);
        await ExecuteAsync(admin.ConnectionString, """
            CREATE USER cadastro_migrator FOR LOGIN cadastro_migrator;
            CREATE USER cadastro_app FOR LOGIN cadastro_app;
            GRANT CREATE TABLE TO cadastro_migrator;
            GRANT CONTROL ON SCHEMA::[import] TO cadastro_migrator;
            GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[import] TO cadastro_app;
            """);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string ConnectionFor(SqlConnectionStringBuilder admin, string user, string password) =>
        new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            InitialCatalog = Database,
            UserID = user,
            Password = password,
            Pooling = false,
        }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

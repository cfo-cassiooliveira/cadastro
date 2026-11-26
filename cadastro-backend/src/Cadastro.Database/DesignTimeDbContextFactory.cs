using Cadastro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cadastro.Database;

/// <summary>
/// Usado pelas ferramentas do EF (<c>migrations add</c>, <c>database update</c>, <c>bundle</c>). A connection string do
/// login <c>cadastro_migrator</c> vem da variável de ambiente; o padrão aqui não contém senha.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CadastroDbContext>
{
    /// <summary>Variável de ambiente com a connection string do <c>cadastro_migrator</c> (nunca versionada).</summary>
    public const string ConnectionEnvironmentVariable = "CADASTRO_MIGRATOR_CONNECTION";

    private const string DefaultConnection = "Server=localhost,1433;Database=LegadoUsers;User Id=cadastro_migrator;TrustServerCertificate=True";

    /// <summary>Cria o contexto apontando as migrations para este assembly.</summary>
    public CadastroDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable) ?? DefaultConnection;
        var options = new DbContextOptionsBuilder<CadastroDbContext>()
            .UseSqlServer(connectionString, CadastroDbContext.ConfigureSqlServer)
            .Options;
        return new CadastroDbContext(options);
    }
}

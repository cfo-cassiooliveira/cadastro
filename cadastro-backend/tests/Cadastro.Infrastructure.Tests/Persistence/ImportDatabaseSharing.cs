using Cadastro.Infrastructure.Tests.Messaging;

namespace Cadastro.Infrastructure.Tests.Persistence;

/// <summary>
/// Compartilha um único SQL Server, um único RabbitMQ e um único Keycloak (com Mailpit) de teste entre as classes de persistência e de mensageria
/// (evita subir vários containers em paralelo).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ImportDatabaseSharing : ICollectionFixture<ImportDatabaseFixture>, ICollectionFixture<RabbitMqFixture>, ICollectionFixture<Cadastro.Infrastructure.Tests.UserImports.KeycloakMailFixture>
{
    /// <summary>Nome da coleção.</summary>
    public const string Name = "ImportDatabase";
}

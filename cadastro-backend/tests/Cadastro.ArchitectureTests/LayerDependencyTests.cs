using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Cadastro.ArchitectureTests;

/// <summary>Regra de dependência da Clean Architecture (Constituição Art. III.1): dependências apontam para dentro.</summary>
public sealed class LayerDependencyTests
{
    // WithoutRequiringPositiveResults: a regra vale desde já, mesmo antes de a camada ganhar o primeiro tipo.
    [Fact]
    public void Domain_ShouldNotDependOnOuterLayersOrFrameworks() =>
        Types().That().Are(CadastroArchitecture.Domain).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceOrBelow("Cadastro.Application")
                .Or().ResideInNamespaceOrBelow("Cadastro.Infrastructure")
                .Or().ResideInNamespaceOrBelow("Cadastro.Api")
                .Or().ResideInNamespaceOrBelow("Microsoft.AspNetCore")
                .Or().ResideInNamespaceOrBelow("System.Net.Http"))
            .Because("Domain não depende de nada (Constituição Art. III.1)")
            .WithoutRequiringPositiveResults()
            .Check(CadastroArchitecture.Value);

    // architecture.md: "Api → Infrastructure só para DI". A Api conhece apenas as extensões de registro.
    [Fact]
    public void Api_ShouldUseInfrastructureOnlyThroughItsCompositionRoot() =>
        Types().That().Are(CadastroArchitecture.Api).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceOrBelow("Cadastro.Infrastructure")
                .And().DoNotHaveFullName("Cadastro.Infrastructure.DependencyInjection"))
            .Because("endpoints falam com casos de uso; adaptadores de infraestrutura só entram pelo composition root")
            .Check(CadastroArchitecture.Value);

    [Fact]
    public void Application_ShouldNotDependOnInfrastructureOrWebFrameworks() =>
        Types().That().Are(CadastroArchitecture.Application).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceOrBelow("Cadastro.Infrastructure")
                .Or().ResideInNamespaceOrBelow("Cadastro.Api")
                .Or().ResideInNamespaceOrBelow("Microsoft.AspNetCore")
                .Or().ResideInNamespaceOrBelow("System.Net.Http"))
            .Because("Application só conhece portas; HTTP, Keycloak e ASP.NET Core são detalhes das camadas externas")
            .WithoutRequiringPositiveResults()
            .Check(CadastroArchitecture.Value);
}

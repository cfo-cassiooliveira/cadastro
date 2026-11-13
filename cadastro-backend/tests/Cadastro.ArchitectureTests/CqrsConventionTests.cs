using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Cadastro.ArchitectureTests;

/// <summary>Convenções de CQRS leve e nomenclatura (architecture.md).</summary>
public sealed class CqrsConventionTests
{
    private static readonly IObjectProvider<IType> WritePorts =
        Types().That().HaveNameEndingWith("Writer").Or().HaveNameEndingWith("Repository").As("Portas de escrita");

    // WithoutRequiringPositiveResults: as convenções valem desde já, antes do primeiro handler existir.
    [Fact]
    public void Handlers_ShouldBeSealed() =>
        Classes().That().Are(CadastroArchitecture.Application).And().HaveNameEndingWith("Handler")
            .Should().BeSealed()
            .WithoutRequiringPositiveResults()
            .Check(CadastroArchitecture.Value);

    [Fact]
    public void Commands_ShouldBeSealed() =>
        Classes().That().Are(CadastroArchitecture.Application).And().HaveNameEndingWith("Command")
            .Should().BeSealed()
            .WithoutRequiringPositiveResults()
            .Check(CadastroArchitecture.Value);

    [Fact]
    public void QueryHandlers_ShouldNotDependOnWritePorts() =>
        Classes().That().Are(CadastroArchitecture.Application).And().HaveNameMatching("^(Get|List|Search).*Handler$")
            .Should().NotDependOnAny(WritePorts)
            .Because("query não altera estado (CQRS leve)")
            .WithoutRequiringPositiveResults()
            .Check(CadastroArchitecture.Value);

    [Fact]
    public void Classes_ShouldNotBeNamedHelperOrManager() =>
        Classes().That().ResideInNamespaceOrBelow("Cadastro")
            .Should().NotHaveNameEndingWith("Helper").AndShould().NotHaveNameEndingWith("Manager")
            .Because("nomes genéricos escondem responsabilidades misturadas (SRP, architecture.md)")
            .Check(CadastroArchitecture.Value);
}

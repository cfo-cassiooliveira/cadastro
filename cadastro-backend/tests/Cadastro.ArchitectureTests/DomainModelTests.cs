using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Cadastro.ArchitectureTests;

/// <summary>Modelo de domínio protege suas invariantes (DDD tático, architecture.md).</summary>
public sealed class DomainModelTests
{
    // Inclui setter `init`: os VOs usam `{ get; }` + construtor/factory (record posicional gera `init`).
    [Fact]
    public void DomainProperties_ShouldNotHavePublicSetters() =>
        PropertyMembers().That().AreDeclaredIn(CadastroArchitecture.Domain).And().HavePublicSetter()
            .Should().NotExist()
            .Because("invariantes do domínio só mudam por métodos com intenção (DDD tático)")
            .Check(CadastroArchitecture.Value);
}

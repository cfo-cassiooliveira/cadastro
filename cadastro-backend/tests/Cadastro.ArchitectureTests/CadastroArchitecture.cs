using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Cadastro.ArchitectureTests;

/// <summary>Arquitetura carregada uma vez só para todas as regras (performance).</summary>
internal static class CadastroArchitecture
{
    // Cada camada entra aqui pelo seu primeiro tipo (typeof(...).Assembly), como no legado, assim que ganhar código.
    // Assembly sem tipo não é carregado: o Smart App Control do Windows bloqueia DLL vazia.
    public static readonly Architecture Value = new ArchLoader()
        .LoadAssemblies(
            typeof(Cadastro.Application.Users.IGetMyProfileHandler).Assembly,
            typeof(Cadastro.Infrastructure.DependencyInjection).Assembly,
            typeof(Program).Assembly)
        .Build();

    public static readonly IObjectProvider<IType> Domain =
        Types().That().ResideInNamespaceOrBelow("Cadastro.Domain").As("Domain");

    public static readonly IObjectProvider<IType> Application =
        Types().That().ResideInNamespaceOrBelow("Cadastro.Application").As("Application");

    public static readonly IObjectProvider<IType> Api =
        Types().That().ResideInNamespaceOrBelow("Cadastro.Api").As("Api");
}

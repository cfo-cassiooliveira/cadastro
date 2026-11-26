using System.Security.Cryptography;
using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Protection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Tests.Protection;

/// <summary>Fail fast da configuração: sem connection string ou sem chave válida a API não pode subir.</summary>
public sealed class ImportPersistenceRegistrationTests
{
    private static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddImportPersistence(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WithoutConnectionString_Throws() =>
        Should.Throw<InvalidOperationException>(() => Build(Config(
            ("PersonalData:Protection:CurrentKeyVersion", "k1"), ("PersonalData:Protection:Keys:k1", Key))));

    [Fact]
    public void WithoutKey_ProtectorCannotBeResolved()
    {
        using var provider = Build(Config(("ConnectionStrings:ImportDb", "Server=x;Database=y")));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PersonalDataProtectionOptions>>().Value);
    }

    [Fact]
    public void WithValidKey_ResolvesProtectorAndDbContext()
    {
        using var provider = Build(Config(
            ("ConnectionStrings:ImportDb", "Server=x;Database=y"),
            ("PersonalData:Protection:CurrentKeyVersion", "k1"),
            ("PersonalData:Protection:Keys:k1", Key)));

        var protector = provider.GetRequiredService<IPersonalDataProtector>();

        protector.CurrentKeyVersion.ShouldBe("k1");
        protector.Unprotect(protector.Protect("x", "ctx"), "k1", "ctx").ShouldBe("x");
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CadastroDbContext>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("AAEC")]
    public void SummaryTokenKey_MissingOrWeak_FailsFast(string? key)
    {
        using var provider = Build(Config(("ConnectionStrings:ImportDb", "Server=x;Database=y"), ("UserImports:SummaryTokenKey", key)));

        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<Cadastro.Infrastructure.UserImports.ImportSummaryOptions>>().Value);
    }

    [Fact]
    public void SummaryTokenKey_Valid_ResolvesTheSigner()
    {
        using var provider = Build(Config(("ConnectionStrings:ImportDb", "Server=x;Database=y"), ("UserImports:SummaryTokenKey", Key)));

        provider.GetRequiredService<IImportSummarySigner>().Sign(Guid.NewGuid(), "a", new string('a', 64), 1, 0, 0).Length.ShouldBe(64);
    }
}

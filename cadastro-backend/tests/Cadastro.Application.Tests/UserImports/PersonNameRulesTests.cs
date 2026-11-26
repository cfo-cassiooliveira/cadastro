using Cadastro.Application.UserImports;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Divisão do nome completo e limites do User Profile do Keycloak, compartilhados pelo upload e pelo consumidor.</summary>
public sealed class PersonNameRulesTests
{
    [Theory]
    [InlineData("Ana", "Ana", "")]
    [InlineData("Ana Souza", "Ana", "Souza")]
    [InlineData("  Ana   Maria  de Souza ", "Ana", "Maria  de Souza")]
    public void Split_UsesTheFirstSpaceAndTrims(string full, string first, string last) =>
        PersonNameRules.Split(full).ShouldBe((first, last));

    [Fact]
    public void Limits_MatchTheKeycloakUserProfile()
    {
        PersonNameRules.FirstNameMaxLength.ShouldBe(100);
        PersonNameRules.LastNameMaxLength.ShouldBe(150);
    }

    [Theory]
    [InlineData(100, 150, false)]
    [InlineData(101, 0, true)]
    [InlineData(10, 151, true)]
    public void ExceedsKeycloakLimits_ChecksEachPartSeparately(int firstLength, int lastLength, bool expected)
    {
        var name = new string('A', firstLength) + (lastLength > 0 ? " " + new string('B', lastLength) : string.Empty);

        PersonNameRules.ExceedsKeycloakLimits(name).ShouldBe(expected);
    }

    [Fact]
    public void Split_WhenNull_Throws() => Should.Throw<ArgumentNullException>(() => PersonNameRules.Split(null!));
}

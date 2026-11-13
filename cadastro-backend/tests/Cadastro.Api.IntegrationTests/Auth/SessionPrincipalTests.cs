using System.Security.Claims;
using System.Text;
using Cadastro.Api.Auth;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>Identidade mínima da sessão (LGPD) e leitura das roles do realm.</summary>
public sealed class SessionPrincipalTests
{
    [Fact]
    public void Create_KeepsOnlyTechnicalIdLoginSessionAndRealmRoles()
    {
        var idToken = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "f1d2"), new Claim("preferred_username", "ana.souza"), new Claim("sid", "s-1"),
            new Claim("email", "ana@example.com"), new Claim("name", "Ana Souza"),
        ]));

        var principal = SessionPrincipal.Create(idToken, Jwt("""{"realm_access":{"roles":["user","admin"]}}"""), "Cookies");

        principal.Claims.Select(claim => claim.Type).Distinct().ShouldBe(["sub", "preferred_username", "sid", "role"], ignoreOrder: true);
        principal.IsInRole("admin").ShouldBeTrue();
        principal.Identity!.Name.ShouldBe("ana.souza");
    }

    [Theory]
    [InlineData("""{"sub":"x"}""")]
    [InlineData("""{"realm_access":"invalid"}""")]
    [InlineData("""{"realm_access":{"roles":"invalid"}}""")]
    public void ReadRealmRoles_WhenClaimIsMissingOrMalformed_ReturnsNoRoles(string payload) =>
        SessionPrincipal.ReadRealmRoles(Jwt(payload)).ShouldBeEmpty();

    [Fact]
    public void ReadRealmRoles_WhenThereIsNoAccessToken_ReturnsNoRoles() =>
        SessionPrincipal.ReadRealmRoles(null).ShouldBeEmpty();

    [Theory]
    [InlineData("/", true)]
    [InlineData("/profile", true)]
    [InlineData("/users?page=2", true)]
    [InlineData("", false)]
    [InlineData("profile", false)]
    [InlineData("//evil.example.com", false)]
    [InlineData("/\\evil.example.com", false)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("/\r\nSet-Cookie:x", false)]
    public void LocalReturnUrl_AcceptsOnlyLocalPaths(string url, bool expected) =>
        LocalReturnUrl.IsLocal(url).ShouldBe(expected);

    private static string Jwt(string payload) =>
        $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(payload)}.";

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

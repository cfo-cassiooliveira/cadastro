using System.Security.Cryptography;
using Cadastro.Infrastructure.Protection;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Tests.Protection;

public sealed class AesGcmPersonalDataProtectorEdgeTests
{
    private static AesGcmPersonalDataProtector Create() =>
        new(Options.Create(new PersonalDataProtectionOptions
        {
            CurrentKeyVersion = "k1",
            Keys = new() { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) },
        }));

    [Theory]
    [InlineData("")]
    [InlineData("João da Conceição Ünïcode 日本語")]
    public void RoundTrip_EmptyAndUnicode(string value)
    {
        var protector = Create();

        protector.Unprotect(protector.Protect(value, "ctx"), "k1", "ctx").ShouldBe(value);
    }

    [Fact]
    public void MissingContextOrValue_Throws()
    {
        var protector = Create();

        Should.Throw<ArgumentException>(() => protector.Protect("x", ""));
        Should.Throw<ArgumentNullException>(() => protector.Protect(null!, "ctx"));
        Should.Throw<ArgumentException>(() => protector.Unprotect([1], "k1", ""));
        Should.Throw<ArgumentException>(() => protector.ComputeEmailIndex(" "));
    }

    [Fact]
    public void Protect_ContextSwap_BetweenFieldsOfSameLine_IsDetected()
    {
        var protector = Create();
        var emailCipher = protector.Protect("a@b.com", "line-1:email");

        Should.Throw<CryptographicException>(() => protector.Unprotect(emailCipher, "k1", "line-1:name"));
    }
}

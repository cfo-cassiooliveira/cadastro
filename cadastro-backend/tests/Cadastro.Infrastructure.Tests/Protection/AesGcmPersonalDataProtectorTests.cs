using System.Security.Cryptography;
using Cadastro.Infrastructure.Protection;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Tests.Protection;

public sealed class AesGcmPersonalDataProtectorTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmPersonalDataProtector Create(params (string Version, string Key)[] keys) =>
        new(Options.Create(new PersonalDataProtectionOptions
        {
            CurrentKeyVersion = keys[0].Version,
            Keys = keys.ToDictionary(k => k.Version, k => k.Key),
        }));

    [Fact]
    public void ProtectUnprotect_RoundTrips()
    {
        var protector = Create(("k1", NewKey()));

        var payload = protector.Protect("Maria da Silva", "line:name");

        protector.Unprotect(payload, "k1", "line:name").ShouldBe("Maria da Silva");
        protector.CurrentKeyVersion.ShouldBe("k1");
    }

    [Fact]
    public void Protect_SameValueTwice_UsesDifferentNonce()
    {
        var protector = Create(("k1", NewKey()));

        var first = protector.Protect("same", "ctx");
        var second = protector.Protect("same", "ctx");

        first.ShouldNotBe(second);
        first.AsSpan(0, 12).SequenceEqual(second.AsSpan(0, 12)).ShouldBeFalse();
    }

    [Fact]
    public void Protect_DoesNotLeakPlaintext()
    {
        var protector = Create(("k1", NewKey()));
        var plaintext = System.Text.Encoding.UTF8.GetBytes("123.456.789-09 Maria da Silva");

        var payload = protector.Protect(System.Text.Encoding.UTF8.GetString(plaintext), "ctx");

        // Comparação em bytes: o texto cifrado (depois de nonce + tag) nunca contém nem repete o texto claro.
        payload.AsSpan().IndexOf(plaintext).ShouldBe(-1);
        payload.AsSpan(28).SequenceEqual(plaintext).ShouldBeFalse();
    }

    [Theory]
    [InlineData("a")]
    [InlineData("João da Conceição")] // multibyte: o tamanho é em bytes UTF-8, não em caracteres
    public void Protect_PayloadHasNonceTagAndCiphertextOfPlaintextSize(string plaintext)
    {
        var protector = Create(("k1", NewKey()));

        var payload = protector.Protect(plaintext, "ctx");

        payload.Length.ShouldBe(12 + 16 + System.Text.Encoding.UTF8.GetByteCount(plaintext));
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111:email", "22222222-2222-2222-2222-222222222222:email")] // outra linha
    [InlineData("11111111-1111-1111-1111-111111111111:email", "11111111-1111-1111-1111-111111111111:cpf")] // outro campo
    public void Unprotect_WithContextOfAnotherLineOrField_Throws(string protectContext, string unprotectContext)
    {
        var protector = Create(("k1", NewKey()));
        var payload = protector.Protect("maria@example.com", protectContext);

        protector.Unprotect(payload, "k1", protectContext).ShouldBe("maria@example.com");
        Should.Throw<CryptographicException>(() => protector.Unprotect(payload, "k1", unprotectContext));
    }

    [Theory]
    [InlineData(5)] // nonce
    [InlineData(20)] // tag
    [InlineData(40)] // texto cifrado
    public void Unprotect_TamperedPayload_Throws(int index)
    {
        var protector = Create(("k1", NewKey()));
        var payload = protector.Protect("a-value-long-enough-to-tamper-with-xx", "ctx");
        payload[index] ^= 0x01;

        Should.Throw<CryptographicException>(() => protector.Unprotect(payload, "k1", "ctx"));
    }

    [Fact]
    public void Unprotect_WithDifferentContext_Throws()
    {
        var protector = Create(("k1", NewKey()));
        var payload = protector.Protect("value", "line-1:email");

        Should.Throw<CryptographicException>(() => protector.Unprotect(payload, "k1", "line-2:email"));
    }

    [Fact]
    public void Unprotect_TruncatedPayload_Throws()
    {
        var protector = Create(("k1", NewKey()));

        Should.Throw<CryptographicException>(() => protector.Unprotect(new byte[10], "k1", "ctx"));
    }

    [Fact]
    public void Unprotect_UnknownKeyVersion_Throws()
    {
        var protector = Create(("k1", NewKey()));
        var payload = protector.Protect("value", "ctx");

        Should.Throw<CryptographicException>(() => protector.Unprotect(payload, "k9", "ctx"));
    }

    [Fact]
    public void Unprotect_WithOldKeyVersionStillConfigured_Works()
    {
        var oldKey = NewKey();
        var payload = Create(("k1", oldKey)).Protect("value", "ctx");
        var rotated = Create(("k2", NewKey()), ("k1", oldKey));

        rotated.Unprotect(payload, "k1", "ctx").ShouldBe("value");
        rotated.CurrentKeyVersion.ShouldBe("k2");
    }

    [Fact]
    public void Unprotect_WithWrongKey_Throws()
    {
        var payload = Create(("k1", NewKey())).Protect("value", "ctx");

        Should.Throw<CryptographicException>(() => Create(("k1", NewKey())).Unprotect(payload, "k1", "ctx"));
    }

    [Fact]
    public void Constructor_WithoutCurrentKey_Throws()
    {
        var options = Options.Create(new PersonalDataProtectionOptions { CurrentKeyVersion = "k1" });

        Should.Throw<InvalidOperationException>(() => new AesGcmPersonalDataProtector(options));
    }

    [Fact]
    public void Constructor_WithShortKey_Throws() =>
        Should.Throw<InvalidOperationException>(() => Create(("k1", Convert.ToBase64String(new byte[16]))));

    [Fact]
    public void ComputeEmailIndex_NormalizesAndIsDeterministic()
    {
        var protector = Create(("k1", NewKey()));

        protector.ComputeEmailIndex("  Maria@Example.COM ").ShouldBe(protector.ComputeEmailIndex("maria@example.com"));
        protector.ComputeEmailIndex("maria@example.com").Length.ShouldBe(32);
        protector.ComputeEmailIndex("other@example.com").ShouldNotBe(protector.ComputeEmailIndex("maria@example.com"));
    }

    [Fact]
    public void ComputeEmailIndex_DependsOnKey() =>
        Create(("k1", NewKey())).ComputeEmailIndex("a@b.com").ShouldNotBe(Create(("k1", NewKey())).ComputeEmailIndex("a@b.com"));

    [Fact]
    public void ComputeEmailIndex_NormalizesUnicodeToNfc()
    {
        var protector = Create(("k1", NewKey()));

        // "é" composto (U+00E9) e decomposto (e + U+0301) são o mesmo e-mail para o usuário.
        protector.ComputeEmailIndex("josé@example.com").ShouldBe(protector.ComputeEmailIndex("josé@example.com"));
    }

    [Fact]
    public void ComputeEmailIndex_AfterRotation_WithRowKeyVersionStillMatchesOldIndex()
    {
        var oldKey = NewKey();
        var oldIndex = Create(("k1", oldKey)).ComputeEmailIndex("maria@example.com");
        var rotated = Create(("k2", NewKey()), ("k1", oldKey));

        // A busca usa a versão gravada na linha: a duplicidade continua detectada depois da rotação.
        rotated.ComputeEmailIndex("maria@example.com", "k1").ShouldBe(oldIndex);
        // Com a chave nova o índice é outro: por isso a versão da linha precisa ser usada.
        rotated.ComputeEmailIndex("maria@example.com").ShouldNotBe(oldIndex);
        rotated.ComputeEmailIndex("maria@example.com", "k2").ShouldBe(rotated.ComputeEmailIndex("maria@example.com"));
    }

    [Fact]
    public void ComputeEmailIndex_WithUnknownKeyVersion_ThrowsInsteadOfFailingSilently()
    {
        var protector = Create(("k2", NewKey()));

        Should.Throw<CryptographicException>(() => protector.ComputeEmailIndex("maria@example.com", "k1"));
    }
}

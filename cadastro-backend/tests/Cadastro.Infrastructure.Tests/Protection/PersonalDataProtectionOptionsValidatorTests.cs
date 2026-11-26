using System.Security.Cryptography;
using Cadastro.Infrastructure.Protection;

namespace Cadastro.Infrastructure.Tests.Protection;

public sealed class PersonalDataProtectionOptionsValidatorTests
{
    private readonly PersonalDataProtectionOptionsValidator _validator = new();

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Validate_WithCurrentKey_Succeeds() =>
        _validator.Validate(null, new PersonalDataProtectionOptions { CurrentKeyVersion = "k1", Keys = { ["k1"] = NewKey() } })
            .Succeeded.ShouldBeTrue();

    [Fact]
    public void Validate_WithoutVersion_Fails() =>
        _validator.Validate(null, new PersonalDataProtectionOptions()).Failed.ShouldBeTrue();

    [Fact]
    public void Validate_MissingCurrentKey_Fails() =>
        _validator.Validate(null, new PersonalDataProtectionOptions { CurrentKeyVersion = "k2", Keys = { ["k1"] = NewKey() } })
            .Failed.ShouldBeTrue();

    [Fact]
    public void Validate_InvalidKey_FailsWithoutEchoingValue()
    {
        const string invalid = "not-base64!!";

        var result = _validator.Validate(null, new PersonalDataProtectionOptions { CurrentKeyVersion = "k1", Keys = { ["k1"] = invalid } });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotContain(invalid);
    }

    [Fact]
    public void Validate_WrongKeySize_Fails() =>
        _validator.Validate(null, new PersonalDataProtectionOptions { CurrentKeyVersion = "k1", Keys = { ["k1"] = Convert.ToBase64String(new byte[16]) } })
            .Failed.ShouldBeTrue();
}

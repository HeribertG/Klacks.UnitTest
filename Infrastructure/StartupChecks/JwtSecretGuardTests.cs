using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.StartupChecks;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.StartupChecks;

[TestFixture]
internal class JwtSecretGuardTests
{
    private const string ValidSecret = "tqXc2HF1RDsi/N1LMkGIVrgFSVuJ9PBmFg/QrgzqlfQ=";

    [Test]
    public void Validate_WhenSecretIsValid_DoesNotThrow()
    {
        var settings = new JwtSettings { Secret = ValidSecret };

        Should.NotThrow(() => JwtSecretGuard.Validate(settings));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Validate_WhenSecretIsEmptyOrWhitespace_Throws(string secret)
    {
        var settings = new JwtSettings { Secret = secret };

        Should.Throw<InvalidOperationException>(() => JwtSecretGuard.Validate(settings));
    }

    [Test]
    public void Validate_WhenSecretIsCommittedPlaceholder_Throws()
    {
        var settings = new JwtSettings { Secret = "REPLACE_VIA_USER_SECRETS_OR_ENV" };

        Should.Throw<InvalidOperationException>(() => JwtSecretGuard.Validate(settings));
    }

    [Test]
    public void Validate_WhenSecretIsTooShort_Throws()
    {
        var settings = new JwtSettings { Secret = new string('a', JwtSecretGuard.MinSecretByteLength - 1) };

        Should.Throw<InvalidOperationException>(() => JwtSecretGuard.Validate(settings));
    }

    [Test]
    public void Validate_WhenSecretIsExactlyMinimumLength_DoesNotThrow()
    {
        var settings = new JwtSettings { Secret = new string('a', JwtSecretGuard.MinSecretByteLength) };

        Should.NotThrow(() => JwtSecretGuard.Validate(settings));
    }
}

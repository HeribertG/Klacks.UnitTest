using System.Security.Cryptography;
using Klacks.Api.Domain.Services.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class SettingsEncryptionServiceTests
{
    private const string SensitiveType = "incomingserverPassword";
    private const string EncryptedPrefix = "ENC:";

    private IDataProtector _protector = null!;
    private SettingsEncryptionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _protector = Substitute.For<IDataProtector>();
        var provider = Substitute.For<IDataProtectionProvider>();
        provider.CreateProtector("Klacks.Settings.Encryption").Returns(_protector);
        var logger = Substitute.For<ILogger<SettingsEncryptionService>>();
        _service = new SettingsEncryptionService(provider, logger);
    }

    [Test]
    public void Decrypt_WhenEncPrefixedButKeyMissing_ReturnsEmptyNotCipherText()
    {
        var cipherText = $"{EncryptedPrefix}CfDJ8FJC5Stg7nGAExgfJad2dlw";
        _protector.Unprotect(Arg.Any<byte[]>())
            .Returns(_ => throw new CryptographicException("The key 5242e52b was not found in the key ring"));

        var result = _service.Decrypt(cipherText);

        result.ShouldBe(string.Empty);
        result.ShouldNotContain(EncryptedPrefix);
    }

    [Test]
    public void Decrypt_WhenNotEncPrefixed_ReturnsValueUnchanged()
    {
        var legacyPlainText = "plain-legacy-password";

        var result = _service.Decrypt(legacyPlainText);

        result.ShouldBe(legacyPlainText);
        _protector.DidNotReceive().Unprotect(Arg.Any<byte[]>());
    }

    [Test]
    public void ProcessForReading_WhenSensitiveAndUndecryptable_ReturnsEmpty()
    {
        var cipherText = $"{EncryptedPrefix}CfDJ8FJC5Stg7nGAExgfJad2dlw";
        _protector.Unprotect(Arg.Any<byte[]>())
            .Returns(_ => throw new CryptographicException("key missing"));

        var result = _service.ProcessForReading(SensitiveType, cipherText);

        result.ShouldBe(string.Empty);
    }

    [Test]
    public void ProcessForReading_WhenNotSensitive_ReturnsValueUnchanged()
    {
        var value = $"{EncryptedPrefix}CfDJ8FJC5Stg7nGAExgfJad2dlw";

        var result = _service.ProcessForReading("incomingserver", value);

        result.ShouldBe(value);
        _protector.DidNotReceive().Unprotect(Arg.Any<byte[]>());
    }
}

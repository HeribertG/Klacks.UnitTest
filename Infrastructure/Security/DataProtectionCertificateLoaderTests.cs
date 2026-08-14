// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DataProtectionCertificateLoader. Without a certificate the caller must fall back to the
/// file system key ring - storing the key ring in the database unprotected would put the key that
/// opens every stored secret into the same dump as the ciphertext.
/// </summary>

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Security;

[TestFixture]
public class DataProtectionCertificateLoaderTests
{
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = Substitute.For<ILogger>();

    [Test]
    public void Load_WithNoCertificateConfigured_ShouldReturnNull()
    {
        var result = DataProtectionCertificateLoader.Load(new DataProtectionKeyRingOptions(), _logger);

        result.ShouldBeNull("no certificate means the file system key ring, never an unprotected database ring");
    }

    [Test]
    public void Load_WithBlankValues_ShouldReturnNull()
    {
        var options = new DataProtectionKeyRingOptions
        {
            CertificateBase64 = "   ",
            CertificatePath = string.Empty,
        };

        var result = DataProtectionCertificateLoader.Load(options, _logger);

        result.ShouldBeNull();
    }

    [Test]
    public void Load_WithAMissingFile_ShouldThrowInsteadOfSilentlyDowngrading()
    {
        var options = new DataProtectionKeyRingOptions
        {
            CertificatePath = Path.Combine(Path.GetTempPath(), $"klacks-missing-{Guid.NewGuid():N}.pfx"),
        };

        Should.Throw<FileNotFoundException>(() => DataProtectionCertificateLoader.Load(options, _logger));
    }

    [Test]
    public void Load_WithAnUnreadableBase64Value_ShouldThrowInsteadOfSilentlyDowngrading()
    {
        var options = new DataProtectionKeyRingOptions
        {
            CertificateBase64 = "this is not base64 !!",
        };

        Should.Throw<FormatException>(() => DataProtectionCertificateLoader.Load(options, _logger));
    }

    [Test]
    public void Load_WithAGeneratedCertificate_ShouldCarryThePrivateKey()
    {
        // The installers generate exactly this shape. Without the private key the key ring could be
        // written but never read again, which would destroy every stored secret on the next restart.
        const string password = "installer-generated-password";
        var options = new DataProtectionKeyRingOptions
        {
            CertificateBase64 = Convert.ToBase64String(CreateSelfSignedPfx(password)),
            CertificatePassword = password,
        };

        using var certificate = DataProtectionCertificateLoader.Load(options, _logger);

        certificate.ShouldNotBeNull();
        certificate!.HasPrivateKey.ShouldBeTrue(
            "DataProtection needs the private key to unwrap the key ring");
        certificate.NotAfter.ShouldBeGreaterThan(DateTime.Now.AddYears(5),
            "the certificate must outlive normal operation, it does not rotate by itself");
    }

    [Test]
    public void Load_WithTheWrongPassword_ShouldThrowInsteadOfSilentlyDowngrading()
    {
        var options = new DataProtectionKeyRingOptions
        {
            CertificateBase64 = Convert.ToBase64String(CreateSelfSignedPfx("the-right-one")),
            CertificatePassword = "the-wrong-one",
        };

        Should.Throw<CryptographicException>(() => DataProtectionCertificateLoader.Load(options, _logger));
    }

    private static byte[] CreateSelfSignedPfx(string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Klacks DataProtection",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        return certificate.Export(X509ContentType.Pfx, password);
    }
}

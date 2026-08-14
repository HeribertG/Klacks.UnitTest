// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DataProtectionCertificateLoader. Without a certificate the caller must fall back to the
/// file system key ring - storing the key ring in the database unprotected would put the key that
/// opens every stored secret into the same dump as the ciphertext.
/// </summary>

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
}

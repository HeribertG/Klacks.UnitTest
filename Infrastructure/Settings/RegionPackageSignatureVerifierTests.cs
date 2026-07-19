// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the region-package signature verifier: a valid RSA-SHA256 signature is accepted,
/// a tampered payload or malformed signature is always rejected (independent of the requirement
/// flag), a missing signature is always rejected once a public key is configured (no downgrade by
/// stripping the header), and without a configured public key downloads are only rejected when
/// RequireSignedRegionPackages is set.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionPackageSignatureVerifierTests
{
    private const int RsaKeySizeBits = 2048;

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{ "version": 1 }""");

    private RSA _rsa = null!;
    private string _publicKeyPem = null!;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(RsaKeySizeBits);
        _publicKeyPem = _rsa.ExportSubjectPublicKeyInfoPem();
    }

    [TearDown]
    public void TearDown()
    {
        _rsa.Dispose();
    }

    private RegionPackageSignatureVerifier CreateVerifier(bool requireSigned, string? publicKeyPem)
    {
        var options = new UpdateTrustOptions
        {
            SignaturePublicKey = publicKeyPem ?? string.Empty,
            RequireSignedRegionPackages = requireSigned
        };

        return new RegionPackageSignatureVerifier(
            Options.Create(options),
            NullLogger<RegionPackageSignatureVerifier>.Instance);
    }

    private string Sign(byte[] payload)
    {
        return Convert.ToBase64String(_rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Test]
    public void Verify_ValidSignature_IsAccepted()
    {
        var verifier = CreateVerifier(requireSigned: true, _publicKeyPem);

        var result = verifier.Verify(Payload, Sign(Payload));

        result.Accepted.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Test]
    public void Verify_TamperedPayload_IsRejectedEvenWhenSignaturesAreNotRequired()
    {
        var verifier = CreateVerifier(requireSigned: false, _publicKeyPem);
        var tamperedPayload = Encoding.UTF8.GetBytes("""{ "version": 1, "injected": true }""");

        var result = verifier.Verify(tamperedPayload, Sign(Payload));

        result.Accepted.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Verify_MalformedBase64Signature_IsRejected()
    {
        var verifier = CreateVerifier(requireSigned: false, _publicKeyPem);

        var result = verifier.Verify(Payload, "not-base64!!");

        result.Accepted.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Verify_SignatureFromForeignKey_IsRejected()
    {
        var verifier = CreateVerifier(requireSigned: false, _publicKeyPem);
        using var foreignRsa = RSA.Create(RsaKeySizeBits);
        var foreignSignature = Convert.ToBase64String(
            foreignRsa.SignData(Payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var result = verifier.Verify(Payload, foreignSignature);

        result.Accepted.ShouldBeFalse();
    }

    [Test]
    public void Verify_MissingSignature_NotRequired_NoKeyConfigured_IsAccepted()
    {
        var verifier = CreateVerifier(requireSigned: false, publicKeyPem: null);

        var result = verifier.Verify(Payload, null);

        result.Accepted.ShouldBeTrue();
    }

    [Test]
    public void Verify_MissingSignature_NotRequired_KeyConfigured_IsRejected()
    {
        var verifier = CreateVerifier(requireSigned: false, _publicKeyPem);

        var result = verifier.Verify(Payload, null);

        result.Accepted.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Verify_MissingSignature_Required_IsRejected()
    {
        var verifier = CreateVerifier(requireSigned: true, _publicKeyPem);

        var result = verifier.Verify(Payload, null);

        result.Accepted.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Verify_MissingSignature_Required_NoKeyConfigured_IsRejected()
    {
        var verifier = CreateVerifier(requireSigned: true, publicKeyPem: null);

        var result = verifier.Verify(Payload, null);

        result.Accepted.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Verify_NoPublicKeyConfigured_NotRequired_IsAcceptedUnverified()
    {
        var verifier = CreateVerifier(requireSigned: false, publicKeyPem: null);

        var result = verifier.Verify(Payload, Sign(Payload));

        result.Accepted.ShouldBeTrue();
    }

    [Test]
    public void Verify_NoPublicKeyConfigured_Required_IsRejected()
    {
        var verifier = CreateVerifier(requireSigned: true, publicKeyPem: null);

        var result = verifier.Verify(Payload, Sign(Payload));

        result.Accepted.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Verify_GarbagePublicKey_IsRejectedInsteadOfThrowing()
    {
        var verifier = CreateVerifier(requireSigned: false, publicKeyPem: "not a pem key");

        var result = verifier.Verify(Payload, Sign(Payload));

        result.Accepted.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }
}

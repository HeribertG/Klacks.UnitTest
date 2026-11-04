// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real RSA key pair for marketplace signature tests: signs payloads the way the marketplace does
/// (RSA-SHA256, PKCS#1 v1.5, Base64; raw bytes or the identity-bound statement) and builds a verifier trusting its public half (or no key at all).
/// </summary>
using System.Security.Cryptography;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Marketplace;

internal sealed class PackageSigningTestKey : IDisposable
{
    private const int RsaKeySizeBits = 2048;

    private readonly RSA _rsa = RSA.Create(RsaKeySizeBits);

    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    public string Sign(byte[] payload) =>
        Convert.ToBase64String(_rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    public string SignBound(string artifactType, string name, byte[] payload) =>
        Sign(PackageSignatureStatement.Build(artifactType, name, payload));

    public PackageSignatureVerifier CreateTrustingVerifier() => CreateVerifier(PublicKeyPem);

    public static PackageSignatureVerifier CreateVerifier(string? publicKeyPem) =>
        new(
            Options.Create(new UpdateTrustOptions { SignaturePublicKey = publicKeyPem ?? string.Empty }),
            NullLogger<PackageSignatureVerifier>.Instance);

    public void Dispose() => _rsa.Dispose();
}

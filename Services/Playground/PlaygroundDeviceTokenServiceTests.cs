// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundDeviceTokenService: a token round-trips to its device id, every kind of manipulation,
/// garbage, oversize input and a token signed for another purpose or key ring is simply invalid and never throws.
/// </summary>

using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.DataProtection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundDeviceTokenServiceTests
{
    private IDataProtectionProvider _provider = null!;
    private PlaygroundDeviceTokenService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new EphemeralDataProtectionProvider();
        _sut = new PlaygroundDeviceTokenService(_provider);
    }

    [Test]
    public void Issue_ThenTryGetDeviceId_RoundTrips()
    {
        var token = _sut.Issue();

        var deviceId = _sut.TryGetDeviceId(token);

        deviceId.ShouldNotBeNull();
        Guid.TryParseExact(deviceId, PlaygroundTokenLimitConstants.DeviceIdFormat, out _).ShouldBeTrue();
    }

    [Test]
    public void Issue_TwiceInARow_YieldsDifferentDevices()
    {
        _sut.TryGetDeviceId(_sut.Issue()).ShouldNotBe(_sut.TryGetDeviceId(_sut.Issue()));
    }

    [Test]
    public void TryGetDeviceId_SameTokenTwice_YieldsTheSameDevice()
    {
        var token = _sut.Issue();

        _sut.TryGetDeviceId(token).ShouldBe(_sut.TryGetDeviceId(token));
    }

    [Test]
    public void TryGetDeviceId_TamperedToken_IsInvalid()
    {
        var token = _sut.Issue();
        var middle = token.Length / 2;
        var flipped = token[middle] == 'A' ? 'B' : 'A';
        var tampered = token[..middle] + flipped + token[(middle + 1)..];

        _sut.TryGetDeviceId(tampered).ShouldBeNull();
    }

    [Test]
    public void TryGetDeviceId_TruncatedToken_IsInvalid()
    {
        var token = _sut.Issue();

        _sut.TryGetDeviceId(token[..^4]).ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-a-token")]
    [TestCase("%%%%")]
    public void TryGetDeviceId_Garbage_IsInvalid(string? garbage)
    {
        _sut.TryGetDeviceId(garbage).ShouldBeNull();
    }

    [Test]
    public void TryGetDeviceId_OversizedValue_IsInvalid()
    {
        _sut.TryGetDeviceId(new string('A', PlaygroundTokenLimitConstants.MaxDeviceTokenLength + 1)).ShouldBeNull();
    }

    [Test]
    public void TryGetDeviceId_TokenOfAnotherPurpose_IsInvalid()
    {
        var foreign = _provider.CreateProtector("some-other-purpose").Protect(Guid.NewGuid().ToString("N"));

        _sut.TryGetDeviceId(foreign).ShouldBeNull();
    }

    [Test]
    public void TryGetDeviceId_ProtectedPayloadThatIsNoGuid_IsInvalid()
    {
        var protector = _provider.CreateProtector(PlaygroundTokenLimitConstants.DeviceTokenPurpose);

        _sut.TryGetDeviceId(protector.Protect("hello")).ShouldBeNull();
    }

    [Test]
    public void TryGetDeviceId_TokenFromAnotherKeyRing_IsInvalid()
    {
        var other = new PlaygroundDeviceTokenService(new EphemeralDataProtectionProvider());

        _sut.TryGetDeviceId(other.Issue()).ShouldBeNull();
    }
}

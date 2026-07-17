// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OAuth2StateStore — verifies the CSRF-protection contract for the OAuth2 login
/// flow: a freshly issued state is accepted, an unknown state is rejected, a state past its TTL is
/// rejected, and replaying an already-consumed state is rejected (single-use).
/// </summary>

using System.Globalization;
using Klacks.Api.Infrastructure.Services.Identity;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Identity;

[TestFixture]
public class OAuth2StateStoreTests
{
    private DateTimeOffset _now;
    private OAuth2StateStore _sut = null!;

    [SetUp]
    public void Setup()
    {
        _now = DateTimeOffset.Parse(
            "2026-07-17T10:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);

        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(_ => _now);

        _sut = new OAuth2StateStore(timeProvider);
    }

    [Test]
    public void ValidateAndConsume_Accepts_A_Freshly_Issued_State()
    {
        var state = _sut.CreateState(Guid.NewGuid());

        _sut.ValidateAndConsume(state).ShouldBeTrue();
    }

    [Test]
    public void ValidateAndConsume_Rejects_An_Unknown_State()
    {
        _sut.ValidateAndConsume($"{Guid.NewGuid()}_neverissued").ShouldBeFalse();
    }

    [Test]
    public void ValidateAndConsume_Rejects_A_State_Past_Its_Ttl()
    {
        var state = _sut.CreateState(Guid.NewGuid());

        _now = _now.AddMinutes(OAuth2StateStore.StateTimeToLiveMinutes + 1);

        _sut.ValidateAndConsume(state).ShouldBeFalse();
    }

    [Test]
    public void ValidateAndConsume_Accepts_A_State_Issued_Just_Under_The_Ttl()
    {
        var state = _sut.CreateState(Guid.NewGuid());

        _now = _now.AddMinutes(OAuth2StateStore.StateTimeToLiveMinutes - 1);

        _sut.ValidateAndConsume(state).ShouldBeTrue();
    }

    [Test]
    public void ValidateAndConsume_Rejects_A_Replayed_State_After_First_Use()
    {
        var state = _sut.CreateState(Guid.NewGuid());
        _sut.ValidateAndConsume(state).ShouldBeTrue();

        _sut.ValidateAndConsume(state).ShouldBeFalse();
    }

    [Test]
    public void CreateState_Embeds_The_Provider_Id_As_The_First_Segment()
    {
        var providerId = Guid.NewGuid();

        var state = _sut.CreateState(providerId);

        state.ShouldStartWith($"{providerId}_");
    }

    [Test]
    public void CreateState_Produces_Distinct_Values_For_Repeated_Calls()
    {
        var providerId = Guid.NewGuid();

        var first = _sut.CreateState(providerId);
        var second = _sut.CreateState(providerId);

        first.ShouldNotBe(second);
    }
}

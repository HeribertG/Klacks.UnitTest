// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OAuth2StateStore — verifies the CSRF-protection contract for the OAuth2 login
/// flow: a freshly issued state is accepted, an unknown state is rejected, a state past its TTL is
/// rejected and is consumed while being rejected, and replaying an already-consumed state is
/// rejected (single-use). Additionally verifies the reason the store became database-backed: a state
/// issued by one store instance is accepted by a second, independent instance on the same database,
/// and remains single-use across both. Uses a shared in-memory DataBaseContext, the pattern the other
/// DB-backed store tests use.
/// </summary>

using System.Globalization;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Identity;

[TestFixture]
public class OAuth2StateStoreTests
{
    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private TimeProvider _timeProvider = null!;
    private DateTimeOffset _now;
    private OAuth2StateStore _sut = null!;

    [SetUp]
    public void Setup()
    {
        _now = DateTimeOffset.Parse(
            "2026-07-17T10:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);

        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();

        _timeProvider = Substitute.For<TimeProvider>();
        _timeProvider.GetUtcNow().Returns(_ => _now);

        _sut = CreateStore();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private OAuth2StateStore CreateStore() => new(CreateContext(), _timeProvider);

    [Test]
    public async Task ValidateAndConsume_Accepts_A_Freshly_Issued_State()
    {
        var state = await _sut.CreateStateAsync(Guid.NewGuid());

        (await _sut.ValidateAndConsumeAsync(state)).ShouldBeTrue();
    }

    [Test]
    public async Task ValidateAndConsume_Rejects_An_Unknown_State()
    {
        (await _sut.ValidateAndConsumeAsync($"{Guid.NewGuid()}_neverissued")).ShouldBeFalse();
    }

    [Test]
    public async Task ValidateAndConsume_Rejects_A_State_Past_Its_Ttl()
    {
        var state = await _sut.CreateStateAsync(Guid.NewGuid());

        _now = _now.AddMinutes(OAuth2StateStore.StateTimeToLiveMinutes + 1);

        using (var beforeConsume = CreateContext())
        {
            beforeConsume.OAuth2States.Count(row => row.State == state).ShouldBe(1);
        }

        (await _sut.ValidateAndConsumeAsync(state)).ShouldBeFalse();

        using var verify = CreateContext();
        verify.OAuth2States.Count().ShouldBe(0);
    }

    [Test]
    public async Task ValidateAndConsume_Accepts_A_State_Issued_Just_Under_The_Ttl()
    {
        var state = await _sut.CreateStateAsync(Guid.NewGuid());

        _now = _now.AddMinutes(OAuth2StateStore.StateTimeToLiveMinutes - 1);

        (await _sut.ValidateAndConsumeAsync(state)).ShouldBeTrue();
    }

    [Test]
    public async Task ValidateAndConsume_Rejects_A_Replayed_State_After_First_Use()
    {
        var state = await _sut.CreateStateAsync(Guid.NewGuid());
        (await _sut.ValidateAndConsumeAsync(state)).ShouldBeTrue();

        (await _sut.ValidateAndConsumeAsync(state)).ShouldBeFalse();
    }

    [Test]
    public async Task CreateState_Embeds_The_Provider_Id_As_The_First_Segment()
    {
        var providerId = Guid.NewGuid();

        var state = await _sut.CreateStateAsync(providerId);

        state.ShouldStartWith($"{providerId}_");
    }

    [Test]
    public async Task CreateState_Produces_Distinct_Values_For_Repeated_Calls()
    {
        var providerId = Guid.NewGuid();

        var first = await _sut.CreateStateAsync(providerId);
        var second = await _sut.CreateStateAsync(providerId);

        first.ShouldNotBe(second);
    }

    [Test]
    public async Task ValidateAndConsume_Accepts_A_State_Issued_By_Another_Store_Instance()
    {
        var issuingInstance = CreateStore();
        var consumingInstance = CreateStore();

        var state = await issuingInstance.CreateStateAsync(Guid.NewGuid());

        (await consumingInstance.ValidateAndConsumeAsync(state)).ShouldBeTrue();
    }

    [Test]
    public async Task ValidateAndConsume_Rejects_A_Replay_On_Any_Instance_After_Another_Instance_Consumed_It()
    {
        var issuingInstance = CreateStore();
        var consumingInstance = CreateStore();

        var state = await issuingInstance.CreateStateAsync(Guid.NewGuid());
        (await consumingInstance.ValidateAndConsumeAsync(state)).ShouldBeTrue();

        (await consumingInstance.ValidateAndConsumeAsync(state)).ShouldBeFalse();
        (await issuingInstance.ValidateAndConsumeAsync(state)).ShouldBeFalse();
    }
}

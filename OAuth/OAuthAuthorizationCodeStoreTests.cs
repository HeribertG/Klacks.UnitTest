// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OAuthAuthorizationCodeStore — verifies the single-use contract for MCP authorization
/// codes: an unknown code yields nothing, a stored code returns its payload exactly once, a code just
/// under its TTL is still redeemable, and a code past its TTL yields nothing while still being consumed.
/// Additionally verifies the reason the store became database-backed: a code issued by one store
/// instance is redeemable by a second, independent instance on the same database, and stays single-use
/// across both. Uses a shared in-memory DataBaseContext and a fake clock, the pattern the other
/// DB-backed store tests use.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthAuthorizationCodeStoreTests
{
    private const string Code = "code-1";
    private const string UnknownCode = "code-unknown";

    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PastTheTtl = OAuthConstants.AuthorizationCodeLifetime + Margin;
    private static readonly TimeSpan JustUnderTheTtl = OAuthConstants.AuthorizationCodeLifetime - Margin;

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private TimeProvider _timeProvider = null!;
    private DateTimeOffset _now;
    private OAuthAuthorizationCodeStore _store = null!;

    [SetUp]
    public void SetUp()
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

        _store = CreateStore();
    }

    [Test]
    public async Task Consume_UnknownCode_ReturnsNull()
    {
        (await _store.ConsumeAsync(UnknownCode)).ShouldBeNull();
    }

    [Test]
    public async Task Consume_StoredCode_ReturnsDataExactlyOnce()
    {
        var data = Data();
        await _store.StoreAsync(Code, data);

        (await _store.ConsumeAsync(Code)).ShouldBe(data);
        (await _store.ConsumeAsync(Code)).ShouldBeNull();
    }

    [Test]
    public async Task Consume_CodeJustUnderItsTtl_ReturnsData()
    {
        var data = Data();
        await _store.StoreAsync(Code, data);

        _now = _now.Add(JustUnderTheTtl);

        (await _store.ConsumeAsync(Code)).ShouldBe(data);
    }

    [Test]
    public async Task Consume_CodePastItsTtl_ReturnsNullAndConsumesTheCode()
    {
        await _store.StoreAsync(Code, Data());

        _now = _now.Add(PastTheTtl);

        StoredRowCount().ShouldBe(1);

        (await _store.ConsumeAsync(Code)).ShouldBeNull();

        StoredRowCount().ShouldBe(0);
        (await _store.ConsumeAsync(Code)).ShouldBeNull();
    }

    [Test]
    public async Task Consume_CodeStoredByAnotherInstance_ReturnsData()
    {
        var issuingInstance = CreateStore();
        var redeemingInstance = CreateStore();
        var data = Data();

        await issuingInstance.StoreAsync(Code, data);

        (await redeemingInstance.ConsumeAsync(Code)).ShouldBe(data);
    }

    [Test]
    public async Task Consume_CodeAlreadyConsumedByAnotherInstance_ReturnsNullOnEveryInstance()
    {
        var issuingInstance = CreateStore();
        var redeemingInstance = CreateStore();

        await issuingInstance.StoreAsync(Code, Data());
        (await redeemingInstance.ConsumeAsync(Code)).ShouldNotBeNull();

        (await redeemingInstance.ConsumeAsync(Code)).ShouldBeNull();
        (await issuingInstance.ConsumeAsync(Code)).ShouldBeNull();
    }

    private int StoredRowCount()
    {
        using var probe = CreateContext();
        return probe.OAuthAuthorizationCodes.Count(row => row.Code == Code);
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private OAuthAuthorizationCodeStore CreateStore() => new(CreateContext(), _timeProvider);

    private static OAuthAuthorizationCodeData Data()
    {
        return new OAuthAuthorizationCodeData(
            UserId: "user-1",
            ClientId: "client-1",
            ClientName: "Claude",
            RedirectUri: "https://claude.ai/api/mcp/auth_callback",
            CodeChallenge: "challenge",
            Scope: null);
    }
}

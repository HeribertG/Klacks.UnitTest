// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PersonalAccessTokenRepository.RevokeAllByUserAsync, the revocation a password reset runs: every token of the
/// user is soft-deleted (rows stay, IsDeleted and DeletedTime set), OAuth access tokens issued as personal access tokens
/// ("oauth:" prefix) included, tokens of other users stay active, and the method reports how many tokens it revoked.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Authentification;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Authentification;

[TestFixture]
public class PersonalAccessTokenRepositoryRevokeAllTests
{
    private const string UserId = "11111111-aaaa-bbbb-cccc-000000000001";
    private const string OtherUserId = "22222222-aaaa-bbbb-cccc-000000000002";
    private const int RevokedTokenCount = 2;

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static PersonalAccessToken Token(string userId, string name) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        TokenHash = Guid.NewGuid().ToString("N"),
        TokenPrefix = "klk_"
    };

    [Test]
    public async Task RevokeAllByUserAsync_SoftDeletesEveryTokenOfTheUser_IncludingOAuthTokens_AndLeavesOtherUsersAlone()
    {
        var personal = Token(UserId, "cli");
        var oauth = Token(UserId, OAuthConstants.AccessTokenNamePrefix + "claude");
        var foreign = Token(OtherUserId, "cli");
        await using (var seed = CreateContext())
        {
            seed.PersonalAccessTokens.AddRange(personal, oauth, foreign);
            await seed.SaveChangesAsync();
        }

        int revoked;
        await using (var context = CreateContext())
        {
            revoked = await new PersonalAccessTokenRepository(context).RevokeAllByUserAsync(UserId, CancellationToken.None);
        }

        revoked.ShouldBe(RevokedTokenCount);
        await using var verify = CreateContext();
        var rows = await verify.PersonalAccessTokens.IgnoreQueryFilters().ToListAsync();
        rows.Count.ShouldBe(3);
        var personalRow = rows.Single(t => t.Id == personal.Id);
        var oauthRow = rows.Single(t => t.Id == oauth.Id);
        var foreignRow = rows.Single(t => t.Id == foreign.Id);
        personalRow.IsDeleted.ShouldBeTrue();
        personalRow.DeletedTime.ShouldNotBeNull();
        oauthRow.IsDeleted.ShouldBeTrue();
        foreignRow.IsDeleted.ShouldBeFalse();
        (await verify.PersonalAccessTokens.Select(t => t.Id).ToListAsync()).ShouldBe(new[] { foreign.Id });
    }

    [Test]
    public async Task RevokeAllByUserAsync_WithoutTokens_ReturnsZero()
    {
        await using var context = CreateContext();

        var revoked = await new PersonalAccessTokenRepository(context).RevokeAllByUserAsync(UserId, CancellationToken.None);

        revoked.ShouldBe(0);
    }
}

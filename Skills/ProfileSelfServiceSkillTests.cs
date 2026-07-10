// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the profile self-service skills — update_my_account (self-scoping,
/// partial update, verify/rollback), list_personal_access_tokens and
/// revoke_personal_access_token (own-token guard, verify).
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Commands.Authentification;
using Klacks.Api.Application.DTOs.Authentification;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Application.Queries.Authentification;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs;
using Klacks.Api.Domain.DTOs.Registrations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ProfileSelfServiceSkillTests
{
    private IMediator _mediator = null!;
    private Guid _userId;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _userId = Guid.NewGuid();
    }

    private SkillExecutionContext Ctx() => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private UserResource OwnAccount(string firstName = "Heri", string email = "heri@klacks.ch") => new()
    {
        Id = _userId.ToString(),
        UserName = "heri",
        FirstName = firstName,
        LastName = "Gasparoli",
        Email = email
    };

    [Test]
    public async Task UpdateMyAccount_UpdatesOwnAccount_AndReportsVerified()
    {
        _mediator.Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<UserResource> { OwnAccount() },
                new List<UserResource> { OwnAccount(firstName: "Heribert") });
        _mediator.Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>())
            .Returns(new HttpResultResource { Success = true });

        var skill = new UpdateMyAccountSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Heribert"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<UpdateAccountCommand>(c =>
                c.UpdateAccount.Id == _userId && c.UpdateAccount.FirstName == "Heribert"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMyAccount_NoOp_WhenNothingSupplied()
    {
        _mediator.Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserResource> { OwnAccount() });

        var skill = new UpdateMyAccountSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("No fields");
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMyAccount_ReturnsError_OnInvalidEmail()
    {
        _mediator.Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserResource> { OwnAccount() });

        var skill = new UpdateMyAccountSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["email"] = "not-an-email"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid email");
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMyAccount_ReturnsError_WhenVerificationShowsOldValues()
    {
        _mediator.Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<UserResource> { OwnAccount() },
                new List<UserResource> { OwnAccount() });
        _mediator.Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>())
            .Returns(new HttpResultResource { Success = true });

        var skill = new UpdateMyAccountSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Heribert"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task ListTokens_ListsOwnTokensWithoutValues()
    {
        _mediator.Send(Arg.Any<GetPersonalAccessTokensQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<PersonalAccessTokenListItemDto>
            {
                new(Guid.NewGuid(), "Claude Desktop", "pat_ab12",
                    new DateTime(2026, 6, 1), new DateTime(2027, 6, 1), null)
            });

        var skill = new ListPersonalAccessTokensSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("1 personal access token(s)");
        await _mediator.Received(1).Send(
            Arg.Is<GetPersonalAccessTokensQuery>(q => q.UserId == _userId.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RevokeToken_ReturnsError_WhenTokenNotOwn()
    {
        _mediator.Send(Arg.Any<GetPersonalAccessTokensQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<PersonalAccessTokenListItemDto>());

        var skill = new RevokePersonalAccessTokenSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["tokenId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found among your personal access tokens");
        await _mediator.DidNotReceive().Send(
            Arg.Any<RevokePersonalAccessTokenCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RevokeToken_RevokesAndVerifies()
    {
        var tokenId = Guid.NewGuid();
        var token = new PersonalAccessTokenListItemDto(
            tokenId, "Claude Desktop", "pat_ab12", new DateTime(2026, 6, 1), new DateTime(2027, 6, 1), null);
        _mediator.Send(Arg.Any<GetPersonalAccessTokensQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<PersonalAccessTokenListItemDto> { token },
                new List<PersonalAccessTokenListItemDto>());
        _mediator.Send(Arg.Any<RevokePersonalAccessTokenCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var skill = new RevokePersonalAccessTokenSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["tokenId"] = tokenId.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<RevokePersonalAccessTokenCommand>(c => c.Id == tokenId && c.UserId == _userId.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RevokeToken_ReturnsError_WhenTokenStillListedAfterRevoke()
    {
        var tokenId = Guid.NewGuid();
        var token = new PersonalAccessTokenListItemDto(
            tokenId, "Claude Desktop", "pat_ab12", new DateTime(2026, 6, 1), new DateTime(2027, 6, 1), null);
        _mediator.Send(Arg.Any<GetPersonalAccessTokensQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<PersonalAccessTokenListItemDto> { token },
                new List<PersonalAccessTokenListItemDto> { token });
        _mediator.Send(Arg.Any<RevokePersonalAccessTokenCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var skill = new RevokePersonalAccessTokenSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["tokenId"] = tokenId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }
}

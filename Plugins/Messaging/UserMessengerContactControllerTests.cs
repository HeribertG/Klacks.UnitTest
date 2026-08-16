// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the rights model of the user-owned messenger channels. The load-bearing property is not a
/// check that could be forgotten but the shape of the route: the pairing code endpoint takes no user
/// id at all, so a code for a foreign account cannot even be requested. Deletion is the case that
/// does need a check, and it is pinned here for owner, administrator and stranger.
/// </summary>
using System.Security.Claims;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.DTOs;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class UserMessengerContactControllerTests
{
    private const string UserId = "3f9a2b10-77c5-4de1-9a02-5b1c8e4d6a33";
    private const string OtherUserId = "b1d4e7c2-3a56-4f80-8c19-2d7e5a0b4c66";
    private const string ChatId = "884411223";

    private IUserMessengerContactRepository _repository = null!;
    private IUserMessengerPairingService _pairingService = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private UserMessengerContactController _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IUserMessengerContactRepository>();
        _pairingService = Substitute.For<IUserMessengerPairingService>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();

        _sut = new UserMessengerContactController(_repository, _pairingService, _unitOfWork);
        SignIn(UserId);
    }

    /// <summary>
    /// The structural guarantee: no overload of this route accepts a user id, so the only account a
    /// code can be issued for is the authenticated one.
    /// </summary>
    [Test]
    public async Task CreatePairingCode_IssuesForTheAuthenticatedUserOnly()
    {
        _pairingService
            .IssueCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UserMessengerPairingCode(
                "K7M4PQRS", UserId, MessengerType.Telegram, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15), null));

        var result = await _sut.CreatePairingCode(CancellationToken.None);

        await _pairingService.Received(1).IssueCodeAsync(UserId, Arg.Any<CancellationToken>());
        var dto = ((OkObjectResult)result.Result!).Value.ShouldBeOfType<UserMessengerPairingCodeDto>();
        dto.Code.ShouldBe("K7M4PQRS");
    }

    [Test]
    public async Task CreatePairingCode_WithoutAUserClaim_IsUnauthorized()
    {
        SignInWithoutClaims();

        var result = await _sut.CreatePairingCode(CancellationToken.None);

        result.Result.ShouldBeOfType<UnauthorizedResult>();
        await _pairingService.DidNotReceive().IssueCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetMine_ReadsOnlyTheAuthenticatedUsersChannels()
    {
        _repository
            .GetByUserIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserMessengerContact>)new[]
            {
                new UserMessengerContact { Id = Guid.NewGuid(), UserId = UserId, Type = MessengerType.Telegram, Value = ChatId, IsPreferred = true }
            });

        var result = await _sut.GetMine(CancellationToken.None);

        var contacts = ((OkObjectResult)result.Result!).Value.ShouldBeOfType<List<UserMessengerContactDto>>();
        contacts.Count.ShouldBe(1);
        contacts[0].IsPreferred.ShouldBeTrue();
        await _repository.Received(1).GetByUserIdAsync(UserId, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().GetByUserIdAsync(OtherUserId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_OwnChannel_IsRemoved()
    {
        var id = GiveContact(UserId);

        var result = await _sut.Delete(id, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await _repository.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    /// <summary>
    /// The refusal is asserted as a real 403 rather than only as a ForbidResult: a bare Forbid() would
    /// resolve the default authentication scheme, which AddIdentity moves to cookie authentication,
    /// and that handler answers a forbidden request with a redirect.
    /// </summary>
    [Test]
    public async Task Delete_ForeignChannelAsPlainUser_IsForbiddenAndNothingIsRemoved()
    {
        var id = GiveContact(OtherUserId);

        var result = await _sut.Delete(id, CancellationToken.None);

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    /// <summary>
    /// The card names the preferred channel because a night-time wake-up call goes exactly there. If
    /// removing the preferred one left every remaining row unflagged, the card would show no preferred
    /// channel while GetPreferredAsync still routed to the oldest row.
    /// </summary>
    [Test]
    public async Task Delete_PreferredChannel_HandsTheFlagToTheOldestRemainingOne()
    {
        var id = GivePreferredContact(UserId);
        var older = new UserMessengerContact
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Type = MessengerType.Slack,
            Value = "U4PLANNER",
            IsPreferred = false,
            CreateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var newer = new UserMessengerContact
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Type = MessengerType.Signal,
            Value = "+41791234567",
            IsPreferred = false,
            CreateTime = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _repository
            .GetByUserIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserMessengerContact>)new[] { newer, older });

        await _sut.Delete(id, CancellationToken.None);

        await _repository.Received(1).UpdateAsync(
            Arg.Is<UserMessengerContact>(c => c.Id == older.Id && c.IsPreferred),
            Arg.Any<CancellationToken>());
        newer.IsPreferred.ShouldBeFalse();
    }

    [Test]
    public async Task Delete_NonPreferredChannel_LeavesTheFlagAlone()
    {
        var id = GiveContact(UserId);

        await _sut.Delete(id, CancellationToken.None);

        await _repository.DidNotReceive().UpdateAsync(
            Arg.Any<UserMessengerContact>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_LastRemainingChannel_PromotesNothing()
    {
        var id = GivePreferredContact(UserId);
        _repository
            .GetByUserIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserMessengerContact>)Array.Empty<UserMessengerContact>());

        var result = await _sut.Delete(id, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await _repository.DidNotReceive().UpdateAsync(
            Arg.Any<UserMessengerContact>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ForeignChannelAsAdmin_IsRemoved()
    {
        SignIn(UserId, MessagingConstants.RoleAdmin);
        var id = GiveContact(OtherUserId);

        var result = await _sut.Delete(id, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await _repository.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_UnknownChannel_IsNotFound()
    {
        var result = await _sut.Delete(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private Guid GiveContact(string ownerUserId) => GiveContact(ownerUserId, isPreferred: false);

    private Guid GivePreferredContact(string ownerUserId) => GiveContact(ownerUserId, isPreferred: true);

    private Guid GiveContact(string ownerUserId, bool isPreferred)
    {
        var id = Guid.NewGuid();
        _repository
            .GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact
            {
                Id = id,
                UserId = ownerUserId,
                Type = MessengerType.Telegram,
                Value = ChatId,
                IsPreferred = isPreferred
            });
        return id;
    }

    private void SignIn(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role);
        SetPrincipal(new ClaimsPrincipal(identity));
    }

    private void SignInWithoutClaims()
    {
        SetPrincipal(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    private void SetPrincipal(ClaimsPrincipal principal)
    {
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }
}

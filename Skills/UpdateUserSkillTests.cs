// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_user. Regression coverage for a real bug: the skill used to rebuild the
/// UpdateAccountResource sent to UpdateAccountCommand from scratch on every call, without carrying
/// PhoneNumber over — so changing e.g. only the first name silently reset a previously stored phone
/// number to null (AccountManagementService.UpdateAccountAsync overwrites unconditionally). Fixed by
/// copying PhoneNumber from the freshly loaded account into the new UpdateAccountResource.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs;
using Klacks.Api.Domain.DTOs.Registrations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateUserSkillTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string OriginalPhoneNumber = "+41 79 111 22 33";

    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static UserResource Account(string firstName = "Alt", string? phoneNumber = OriginalPhoneNumber) => new()
    {
        Id = UserId,
        UserName = "muster",
        FirstName = firstName,
        LastName = "Muster",
        Email = "muster@example.com",
        PhoneNumber = phoneNumber
    };

    [Test]
    public async Task UpdateUser_ChangingOnlyFirstName_PreservesPreviouslyStoredPhoneNumber()
    {
        _mediator.Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<UserResource> { Account() },
                new List<UserResource> { Account(firstName: "Neu") });
        _mediator.Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>())
            .Returns(new HttpResultResource { Success = true });

        var skill = new UpdateUserSkill(_mediator);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = UserId,
            ["firstName"] = "Neu"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<UpdateAccountCommand>(c =>
                c.UpdateAccount.FirstName == "Neu" &&
                c.UpdateAccount.PhoneNumber == OriginalPhoneNumber),
            Arg.Any<CancellationToken>());
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateUserSkill.ExecuteAsync, written before its long body was split into private
/// steps: the account-identification errors (missing, unknown id, no match with the candidate list, ambiguous match),
/// the invalid-email error after earlier fields were already applied to the loaded account, the no-op text, the failed
/// command and failed read-back texts, the exact success text and data, and the payload sent to UpdateAccountCommand.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs;
using Klacks.Api.Domain.DTOs.Registrations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateUserSkillCharacterizationTests
{
    private const string FirstId = "11111111-1111-1111-1111-111111111111";
    private const string SecondId = "22222222-2222-2222-2222-222222222222";

    private IMediator _mediator = null!;
    private UpdateUserSkill _skill = null!;
    private List<UserResource> _users = null!;
    private List<UserResource> _reloaded = null!;
    private UpdateAccountResource? _sent;
    private HttpResultResource? _commandResult;

    [SetUp]
    public void Setup()
    {
        _sent = null;
        _commandResult = new HttpResultResource { Success = true };
        _users = [First(), Second()];
        _reloaded = [];

        _mediator = Substitute.For<IMediator>();
        var listCalls = 0;
        _mediator.Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => listCalls++ == 0 ? _users : _reloaded);
        _mediator.Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent = ci.Arg<UpdateAccountCommand>().UpdateAccount;
                return _commandResult!;
            });

        _skill = new UpdateUserSkill(_mediator);
    }

    private static UserResource First() => new()
    {
        Id = FirstId,
        UserName = "muster",
        FirstName = "Max",
        LastName = "Muster",
        Email = "max@example.com",
        PhoneNumber = "+41 79 000 00 00"
    };

    private static UserResource Second() => new()
    {
        Id = SecondId,
        UserName = "beispiel",
        FirstName = "Bea",
        LastName = "Beispiel",
        Email = "bea@example.com"
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    [Test]
    public async Task NeitherIdNorName_IsRefused_AfterTheAccountListWasLoaded()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["firstName"] = "Neu" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Either 'userId' or 'userName' is required to identify the account.");
        await _mediator.Received(1).Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownId_IsRefused()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = " nope ",
            ["firstName"] = "Neu"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("No login account with id 'nope'.");
    }

    [Test]
    public async Task UnknownName_ListsTheAvailableAccounts()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "niemand",
            ["firstName"] = "Neu"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("No login account matches 'niemand'. Available accounts: muster, beispiel");
    }

    [Test]
    public async Task AmbiguousName_ListsTheMatches()
    {
        _users[1].UserName = "max@example.com";

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "MAX@example.com",
            ["firstName"] = "Neu"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "'MAX@example.com' matches 2 accounts: muster (max@example.com), max@example.com (bea@example.com). Repeat the call with userId.");
    }

    [Test]
    public async Task InvalidEmail_IsRefused_AfterNamesWereAppliedToTheLoadedAccount()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = FirstId,
            ["firstName"] = " Neu ",
            ["lastName"] = "Neumann",
            ["email"] = " kaputt "
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid email address:  kaputt ");
        _users[0].FirstName.ShouldBe("Neu");
        _users[0].LastName.ShouldBe("Neumann");
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnchangedValues_ReturnNoOp_WithoutCommand()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "Muster",
            ["firstName"] = " Max ",
            ["email"] = "max@example.com",
            ["newUserName"] = "muster"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No fields supplied for update — the account was left unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe("{\"AccountId\":\"" + FirstId + "\",\"ChangedFields\":[]}");
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailedCommand_RelaysItsMessages()
    {
        _commandResult = new HttpResultResource { Success = false, Messages = "Email already taken" };

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = FirstId,
            ["email"] = "bea@example.com"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Updating the account failed: Email already taken");
        await _mediator.Received(1).Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingCommandResult_IsReportedAsUnknownError()
    {
        _commandResult = null;

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = FirstId,
            ["lastName"] = "Neumann"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Updating the account failed: unknown error");
    }

    [Test]
    public async Task ReadBackWithOldValues_IsReportedAsNotPersisted()
    {
        _reloaded = [First(), Second()];

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = FirstId,
            ["lastName"] = "Neumann"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Database verification failed: the account does not show the new values after the write — treat the update as not persisted.");
        await _mediator.Received(2).Send(Arg.Any<GetUserListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReadBackWithoutTheAccount_IsReportedAsNotPersisted()
    {
        _reloaded = [Second()];

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = FirstId,
            ["lastName"] = "Neumann"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Database verification failed: the account does not show the new values after the write — treat the update as not persisted.");
    }

    [Test]
    public async Task AllFieldsChanged_SendsTheMergedAccount_AndReportsVerified()
    {
        var persisted = First();
        persisted.FirstName = "Moritz";
        persisted.LastName = "Meier";
        persisted.Email = "moritz@example.com";
        persisted.UserName = "meier";
        _reloaded = [Second(), persisted];

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "max@example.com",
            ["newUserName"] = " meier ",
            ["email"] = " moritz@example.com ",
            ["lastName"] = "Meier",
            ["firstName"] = "Moritz"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Account 'meier' updated (firstName, lastName, email, userName) and confirmed in the database (verified).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"AccountId\":\"" + FirstId + "\","
            + "\"ChangedFields\":[\"firstName\",\"lastName\",\"email\",\"userName\"],"
            + "\"FirstName\":\"Moritz\",\"LastName\":\"Meier\",\"Email\":\"moritz@example.com\",\"UserName\":\"meier\"}");
        _sent.ShouldNotBeNull();
        _sent!.Id.ShouldBe(Guid.Parse(FirstId));
        _sent.FirstName.ShouldBe("Moritz");
        _sent.LastName.ShouldBe("Meier");
        _sent.Email.ShouldBe("moritz@example.com");
        _sent.UserName.ShouldBe("meier");
        _sent.PhoneNumber.ShouldBe("+41 79 000 00 00");
    }
}

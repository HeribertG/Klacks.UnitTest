// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// start_software_update runs only as the confirmed replay of its preview: without the binding (a plan step, a bypassed
/// gate) or with one of another skill it starts nothing. With the binding it sends exactly the trigger command the update
/// card sends, with the caller's id and the confirmed version, and never the automatic requester. Parameters cannot name
/// a version or an address, the answer says that Klacks restarts and sends nobody to a console.
/// </summary>

using Klacks.Api.Application.Commands.Update;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using NSubstitute;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class StartSoftwareUpdateSkillTests
{
    private const string TargetVersion = "1.0.53";

    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private IMediator _mediator = null!;
    private StartSoftwareUpdateSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _skill = new StartSoftwareUpdateSkill(_mediator);
    }

    private static SkillExecutionContext Context(ConfirmationBinding? binding) => new()
    {
        UserId = UserId,
        TenantId = Guid.Empty,
        UserName = "admin",
        UserPermissions = [Roles.Admin],
        ConfirmedActionBinding = binding
    };

    private void GivenTrigger(UpdateTriggerResult result) =>
        _mediator.Send(Arg.Any<TriggerUpdateCommand>(), Arg.Any<CancellationToken>()).Returns(result);

    [Test]
    public async Task WithoutConfirmedBinding_RefusesAndStartsNothing()
    {
        var result = await _skill.ExecuteAsync(Context(null), []);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(StartSoftwareUpdateSkill.SkillName);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(TriggerUpdateCommand)!, default);
    }

    [Test]
    public async Task WithTheBindingOfAnotherKind_RefusesAndStartsNothing()
    {
        var foreign = ConfirmationBinding.Of(ConfirmationBindingKinds.OrderSeal, [TargetVersion]);

        var result = await _skill.ExecuteAsync(Context(foreign), []);

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(TriggerUpdateCommand)!, default);
    }

    [Test]
    public async Task WithConfirmedBinding_SendsTheCardsCommandWithTheCallerAndTheConfirmedVersion()
    {
        GivenTrigger(new UpdateTriggerResult { Enqueued = true, OperationId = Guid.NewGuid(), Reason = UpdateReasons.UpdateEnqueued });

        var result = await _skill.ExecuteAsync(Context(StartSoftwareUpdateSkill.BindingFor(TargetVersion)), []);

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<TriggerUpdateCommand>(command =>
                command.RequestedBy == UserId.ToString() && command.ExpectedTargetVersion == TargetVersion),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Requester_IsNeverTheAutomaticPolicy()
    {
        GivenTrigger(new UpdateTriggerResult { Enqueued = true });

        await _skill.ExecuteAsync(Context(StartSoftwareUpdateSkill.BindingFor(TargetVersion)), []);

        await _mediator.DidNotReceive().Send(
            Arg.Is<TriggerUpdateCommand>(command => command.RequestedBy == UpdateRequesters.Automatic),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Parameters_CannotNameAVersionOrAnAddress()
    {
        GivenTrigger(new UpdateTriggerResult { Enqueued = true });
        var parameters = new Dictionary<string, object>
        {
            ["version"] = "6.6.6",
            ["targetVersion"] = "6.6.6",
            ["url"] = "https://attacker.example/stable.json"
        };

        await _skill.ExecuteAsync(Context(StartSoftwareUpdateSkill.BindingFor(TargetVersion)), parameters);

        await _mediator.Received(1).Send(
            Arg.Is<TriggerUpdateCommand>(command => command.ExpectedTargetVersion == TargetVersion),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Answer_SaysKlacksRestartsAndSendsNobodyToAConsole()
    {
        GivenTrigger(new UpdateTriggerResult { Enqueued = true });

        var result = await _skill.ExecuteAsync(Context(StartSoftwareUpdateSkill.BindingFor(TargetVersion)), []);

        result.Message.ShouldContain(SoftwareUpdateNotices.Restart);
        result.Message.ShouldContain(TargetVersion);
        result.Message!.ToLowerInvariant().ShouldNotContain("powershell");
        result.Message.ToLowerInvariant().ShouldNotContain("cmd");
    }

    [Test]
    public async Task RejectedTrigger_IsReportedAsNotStarted()
    {
        GivenTrigger(new UpdateTriggerResult { Enqueued = false, Reason = UpdateReasons.TargetVersionChanged });

        var result = await _skill.ExecuteAsync(Context(StartSoftwareUpdateSkill.BindingFor(TargetVersion)), []);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(UpdateReasons.TargetVersionChanged);
    }
}

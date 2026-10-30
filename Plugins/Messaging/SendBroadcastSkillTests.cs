// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// What send_broadcast answers: refused before anything is touched without a binding of its own kind, and after a confirmed run the
/// counts sent, failed and skipped without any address or the text. A run that reached nobody is an error, and an unusable call or
/// a provider that went away between compare and send sends nothing and answers cleanly. Plan and send run under the run lock: it is
/// taken before the plan, a run in progress leaves nothing planned or sent and is named in the answer, and the lock is released.
/// </summary>

using System.Text.Json;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Contracts.Skills;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Models;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Skills;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

using SkillExecutionContext = Klacks.Plugin.Contracts.Skills.SkillExecutionContext;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SendBroadcastSkillTests
{
    private const string Text = "Vertraulicher Rundbrief";
    private const string Address = "chat-4711";

    private IBroadcastPlanner _planner = null!;
    private IMessagingService _messagingService = null!;
    private IBroadcastRunLock _runLock = null!;
    private IAsyncDisposable _runHold = null!;
    private BroadcastRecipientSet _recipients = null!;
    private SendBroadcastSkill _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _recipients = new BroadcastRecipientSet(
            Guid.NewGuid(), "Telegram", [new BroadcastRecipient(Guid.NewGuid(), Address, null, false)], 1);
        _planner = Substitute.For<IBroadcastPlanner>();
        _planner.PlanAsync(Arg.Any<SendBroadcastParameters>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(BroadcastPlan.Ready(_recipients, 2));
        _messagingService = Substitute.For<IMessagingService>();
        _runHold = Substitute.For<IAsyncDisposable>();
        _runLock = Substitute.For<IBroadcastRunLock>();
        _runLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(_runHold);
        _sut = new SendBroadcastSkill(_planner, _messagingService, _runLock);
    }

    [TearDown]
    public async Task TearDown() => await _runHold.DisposeAsync();

    private SkillExecutionContext Context(PluginConfirmedBinding? binding) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new[] { MessagingConstants.RoleAdmin },
        ConfirmedBinding = binding
    };

    private SkillExecutionContext Confirmed() => Context(
        new PluginConfirmedBinding(MessagingConstants.BroadcastBindingKind, SendBroadcastBinding.ItemsFor(_recipients, Text)));

    [Test]
    public async Task ARunInProgress_PlansNothing_SendsNothing_AndSaysSo()
    {
        _runLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns((IAsyncDisposable?)null);

        var result = await _sut.ExecuteAsync(Confirmed(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MessagingConstants.BroadcastAlreadyRunningError);
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default);
        await _messagingService.DidNotReceiveWithAnyArgs().SendBroadcastToRecipientsAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task TheLock_IsTakenBeforeThePlan_AndReleasedAfterTheSend()
    {
        _messagingService.SendBroadcastToRecipientsAsync(
                Arg.Any<BroadcastRecipientSet>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BroadcastSendResult(Guid.NewGuid(), 1, 1, 0, 0));

        await _sut.ExecuteAsync(Confirmed(), Parameters());

        Received.InOrder(() =>
        {
            _runLock.TryAcquireAsync(Arg.Any<CancellationToken>());
            _planner.PlanAsync(Arg.Any<SendBroadcastParameters>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            _messagingService.SendBroadcastToRecipientsAsync(
                Arg.Any<BroadcastRecipientSet>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            _ = _runHold.DisposeAsync();
        });
    }

    private static Dictionary<string, object> Parameters() => new()
    {
        ["groupId"] = Guid.NewGuid().ToString(),
        ["content"] = Text
    };

    private void GiveResult(int sent, int failed) =>
        _messagingService.SendBroadcastToRecipientsAsync(_recipients, Text, MessagingConstants.DefaultContentType, Arg.Any<CancellationToken>())
            .Returns(new BroadcastSendResult(Guid.NewGuid(), 2, sent, failed, 1));

    [Test]
    public async Task WithoutABinding_NothingIsTouched()
    {
        var result = await _sut.ExecuteAsync(Context(null), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(PluginConfirmationTexts.BindingRequired, MessagingConstants.SkillSendBroadcast));
        _planner.ReceivedCalls().ShouldBeEmpty();
        _messagingService.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task TheBindingOfAMessage_DoesNotOpenABroadcast()
    {
        var result = await _sut.ExecuteAsync(
            Context(new PluginConfirmedBinding(MessagingConstants.MessageBindingKind, ["x"])), Parameters());

        result.Success.ShouldBeFalse();
        _planner.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task AChangedPlan_SendsNothing()
    {
        var result = await _sut.ExecuteAsync(
            Context(new PluginConfirmedBinding(MessagingConstants.BroadcastBindingKind, ["provider:Other"])), Parameters());

        result.Message.ShouldBe(PluginConfirmationTexts.ScopeChanged);
        await _messagingService.DidNotReceiveWithAnyArgs().SendBroadcastToRecipientsAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task ACompletedRun_ReportsCounts_WithoutAddressOrText()
    {
        GiveResult(sent: 1, failed: 1);

        var result = await _sut.ExecuteAsync(Confirmed(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldContain("1 sent, 1 failed, 1 skipped");
        result.Message.ShouldContain("2 left out");
        var json = JsonSerializer.Serialize(result.Data) + result.Message;
        json.ShouldNotContain(Address);
        json.ShouldNotContain(Text);
    }

    [Test]
    public async Task ARunThatReachedNobody_IsAnError()
    {
        GiveResult(sent: 0, failed: 1);

        var result = await _sut.ExecuteAsync(Confirmed(), Parameters());

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task AProviderSwitchedOffBeforeTheSend_AnswersCleanly()
    {
        _messagingService.SendBroadcastToRecipientsAsync(_recipients, Text, MessagingConstants.DefaultContentType, Arg.Any<CancellationToken>())
            .Returns<Task<BroadcastSendResult>>(_ => throw new InvalidOperationException("Provider 'Telegram' is disabled"));

        var result = await _sut.ExecuteAsync(Confirmed(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("disabled");
    }

    [Test]
    public async Task AMissingText_IsRefusedBeforeThePlan()
    {
        var result = await _sut.ExecuteAsync(Confirmed(), new Dictionary<string, object> { ["groupId"] = Guid.NewGuid().ToString() });

        result.Success.ShouldBeFalse();
        _planner.ReceivedCalls().ShouldBeEmpty();
    }
}

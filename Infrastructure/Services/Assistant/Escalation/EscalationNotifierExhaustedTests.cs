// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// EscalationNotifier.NotifyExhaustedAsync, the close-out of a chain nobody answered. Two claims are
/// separable and separately asserted here. First, the inbox row of every stage that was still waiting is
/// acknowledged, for BOTH chain purposes - an open row asking a question that can no longer be answered
/// is wrong whether the question was about a shift or about a remediation. Second, the note that says the
/// window lapsed: for a ProactiveApproval chain it goes to the stages that were waiting; for an
/// AbsenceCoverage chain it goes to every stage that was ever notified plus every administrator
/// (deduplicated, case-insensitive), also when nobody was reached at all, because the shift may still be
/// uncovered. A failing admin lookup must not abort the close-out.
/// The dispatch rows written for escalation stages carry neither ConditionId nor NextReminderAtUtc, so
/// they were never in the reminder loop (IProactiveTriggerDispatchRepository.GetDueForReminderAsync needs
/// both) - the acknowledgement closes the row, it does not stop a reminder.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Infrastructure.Services.Assistant.Escalation;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Escalation;

[TestFixture]
public class EscalationNotifierExhaustedTests
{
    private const string FirstUserId = "planner-first";
    private const string SecondUserId = "planner-second";
    private const string TriggerKind = "empty_container";
    private const string AdminUserId = "admin-one";
    private const string PendingUserId = "planner-never-reached";

    private static readonly Guid ConditionId = Guid.NewGuid();
    private static readonly Guid FirstRowId = Guid.NewGuid();
    private static readonly Guid SecondRowId = Guid.NewGuid();
    private static readonly DateTime DeadlineUtc = new(2026, 9, 21, 23, 0, 0, DateTimeKind.Utc);

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAssistantNotificationService _notificationService = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private EscalationNotifier _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        _notificationService.GetConnectedUserIdsAsync().Returns(new List<string>());
        _conditionRepository.GetByIdAsync(ConditionId, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = ConditionId, TriggerKind = TriggerKind });

        _sut = new EscalationNotifier(
            _dispatchRepository,
            _notificationService,
            Substitute.For<IOfflineMessengerNotifier>(),
            Substitute.For<IProactiveMessengerTextComposer>(),
            new EscalationHandoffTextService(
                new InstallationLanguageResolver(settingsReader, Substitute.For<ILogger<InstallationLanguageResolver>>()),
                Substitute.For<ILogger<EscalationHandoffTextService>>()),
            new FixedCompanyClock(new DateTimeOffset(DeadlineUtc, TimeSpan.Zero)),
            _conditionRepository,
            Substitute.For<IConditionRemediationRegistry>(),
            _audienceResolver,
            Substitute.For<ILogger<EscalationNotifier>>());
    }

    [Test]
    public async Task NotifyExhaustedAsync_ApprovalChain_AcknowledgesEveryStagesInboxRow()
    {
        var chain = ApprovalChain();

        await _sut.NotifyExhaustedAsync(chain, Stages(), CancellationToken.None);

        await _dispatchRepository.Received(1).AcknowledgeAsync(FirstRowId, FirstUserId, Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).AcknowledgeAsync(SecondRowId, SecondUserId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NotifyExhaustedAsync_ApprovalChain_LeavesOneQuietNotePerStage()
    {
        var chain = ApprovalChain();

        await _sut.NotifyExhaustedAsync(chain, Stages(), CancellationToken.None);

        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(row => row.UserId == FirstUserId && row.ContentKey!.Contains(TriggerKind)),
            Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(row => row.UserId == SecondUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NotifyExhaustedAsync_AbsenceChain_ClosesTheRowsAndNotesEveryNotifiedStagePlusEveryAdmin()
    {
        var stages = Stages();
        var chain = AbsenceChain([.. stages, NeverNotifiedStage()]);
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { AdminUserId });

        await _sut.NotifyExhaustedAsync(chain, stages, CancellationToken.None);

        await _dispatchRepository.Received(1).AcknowledgeAsync(FirstRowId, FirstUserId, Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).AcknowledgeAsync(SecondRowId, SecondUserId, Arg.Any<CancellationToken>());
        Assert.That(RecordedRecipients(), Is.EquivalentTo(new[] { FirstUserId, SecondUserId, AdminUserId }));
    }

    [Test]
    public async Task NotifyExhaustedAsync_AbsenceChain_AnAdminWhoAlsoHeldAStage_GetsOneNoteOnly()
    {
        var stages = Stages();
        var chain = AbsenceChain(stages);
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { FirstUserId.ToUpperInvariant(), AdminUserId });

        await _sut.NotifyExhaustedAsync(chain, stages, CancellationToken.None);

        var recipients = RecordedRecipients();
        Assert.That(recipients, Has.Count.EqualTo(3), "The admin who held a stage is deduplicated case-insensitively.");
        Assert.That(recipients.Count(r => string.Equals(r, FirstUserId, StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
    }

    [Test]
    public async Task NotifyExhaustedAsync_AbsenceChain_NobodyWasEverNotified_StillTellsTheAdmins()
    {
        var chain = AbsenceChain([NeverNotifiedStage()]);
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { AdminUserId });

        await _sut.NotifyExhaustedAsync(chain, [], CancellationToken.None);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!, default);
        Assert.That(RecordedRecipients(), Is.EqualTo(new[] { AdminUserId }));
    }

    [Test]
    public void NotifyExhaustedAsync_AbsenceChain_AdminLookupThrows_StillNotesTheStagesWithoutThrowing()
    {
        var stages = Stages();
        var chain = AbsenceChain(stages);
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("identity store unavailable"));

        Assert.DoesNotThrowAsync(() => _sut.NotifyExhaustedAsync(chain, stages, CancellationToken.None));

        Assert.That(RecordedRecipients(), Is.EquivalentTo(new[] { FirstUserId, SecondUserId }));
    }

    [Test]
    public async Task NotifyExhaustedAsync_AbsenceChain_NoNotifiedStageAndNoAdmin_WritesNothing()
    {
        await _sut.NotifyExhaustedAsync(AbsenceChain([NeverNotifiedStage()]), [], CancellationToken.None);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task NotifyExhaustedAsync_ApprovalChain_NeverAsksForTheAdmins()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { AdminUserId });

        await _sut.NotifyExhaustedAsync(ApprovalChain(), Stages(), CancellationToken.None);

        await _audienceResolver.DidNotReceiveWithAnyArgs().GetAdminUserIdsAsync(default);
        Assert.That(RecordedRecipients(), Is.EquivalentTo(new[] { FirstUserId, SecondUserId }));
    }

    [Test]
    public async Task NotifyExhaustedAsync_StageWithoutADispatchRow_IsSkippedWithoutAnAcknowledgement()
    {
        var chain = ApprovalChain();
        var stage = new EscalationStage
        {
            Id = Guid.NewGuid(),
            Rank = 1,
            UserId = FirstUserId,
            UserDisplayName = "First Planner",
            Status = EscalationStageStatus.Cancelled,
            NotifiedAtUtc = DeadlineUtc,
            DispatchRowId = null
        };

        await _sut.NotifyExhaustedAsync(chain, [stage], CancellationToken.None);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!, default);
    }

    [Test]
    public async Task NotifyExhaustedAsync_NoStageWasWaiting_DoesNothingAtAll()
    {
        await _sut.NotifyExhaustedAsync(ApprovalChain(), [], CancellationToken.None);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task NotifyExhaustedAsync_AcknowledgementThrows_StillClosesTheRemainingStages()
    {
        _dispatchRepository
            .AcknowledgeAsync(FirstRowId, FirstUserId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("dispatch row is gone"));

        await _sut.NotifyExhaustedAsync(ApprovalChain(), Stages(), CancellationToken.None);

        await _dispatchRepository.Received(1).AcknowledgeAsync(SecondRowId, SecondUserId, Arg.Any<CancellationToken>());
    }

    private List<string> RecordedRecipients() =>
        _dispatchRepository.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IProactiveTriggerDispatchRepository.RecordAsync))
            .Select(call => ((ProactiveTriggerDispatchRow)call.GetArguments()[0]!).UserId)
            .ToList();

    private static EscalationChain AbsenceChain(IEnumerable<EscalationStage> stages) => new()
    {
        Id = Guid.NewGuid(),
        Purpose = EscalationChainPurpose.AbsenceCoverage,
        Status = EscalationChainStatus.Exhausted,
        AbsentClientName = "Absent Employee",
        ShiftStartUtc = DeadlineUtc,
        DeadlineUtc = DeadlineUtc,
        Stages = stages.ToList()
    };

    private static EscalationStage NeverNotifiedStage() => new()
    {
        Id = Guid.NewGuid(),
        Rank = 3,
        UserId = PendingUserId,
        UserDisplayName = "Never Reached",
        Status = EscalationStageStatus.Cancelled,
        NotifiedAtUtc = null
    };

    private static EscalationChain ApprovalChain() => new()
    {
        Id = Guid.NewGuid(),
        Purpose = EscalationChainPurpose.ProactiveApproval,
        Status = EscalationChainStatus.Exhausted,
        ConditionId = ConditionId,
        DeadlineUtc = DeadlineUtc
    };

    private static IReadOnlyList<EscalationStage> Stages() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            Rank = 1,
            UserId = FirstUserId,
            UserDisplayName = "First Planner",
            Status = EscalationStageStatus.Cancelled,
            NotifiedAtUtc = DeadlineUtc,
            DispatchRowId = FirstRowId
        },
        new()
        {
            Id = Guid.NewGuid(),
            Rank = 2,
            UserId = SecondUserId,
            UserDisplayName = "Second Planner",
            Status = EscalationStageStatus.Cancelled,
            NotifiedAtUtc = DeadlineUtc,
            DispatchRowId = SecondRowId
        }
    ];
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GateExplainOfferService. Offering: the chip appears exactly once, only when the turn ended at a gate and the
/// topic is allowed (setting on, kill switch off, audience fits, preference lets it through, no offer of the kind within the
/// cooldown, an offer channel), it joins a COPY of the model's single-choice options or a synthesized yes/no pair, never mixes
/// into a date or number field, and the model's own object is never modified. The counter row remembers the gate skill and
/// the fingerprint of the held token. Settling: the chip's own label (in any language) accepts and grounds the turn in the
/// happen; anything else is dismissed with an outcome - proceeded (explicit yes), declined (leading refusal), other, or expired
/// (older than the maximum age, even for the chip label). Only ignored outcomes count towards the long snooze. After an
/// accepted offer exactly the offered confirmation is renewed (preview gates and offers without a fingerprint renew nothing)
/// and yes/no is offered again. Failures never reach the turn.
/// </summary>

using Klacks.Api.Application.Services.Assistant.ExplainOffers;
using Klacks.Api.Application.Skills.Generic;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Security;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.ExplainOffers;

[TestFixture]
public class GateExplainOfferServiceTests
{
    private const string ConversationId = "conv-1";
    private const string GermanChip = "Erst kurz erklären";
    private const string EnglishChip = "Explain briefly first";
    private const string DeleteSkill = "bulk_delete_works";
    private const string HeldToken = "held-token";
    private const string UnrelatedMessage = "Zeig mir die Dienste von morgen";
    private const string DeleteKind = ExplainOfferTopics.TriggerKindPrefix + ExplainOfferTopics.DeleteFinalKey;
    private const string PlanKind = ExplainOfferTopics.TriggerKindPrefix + ExplainOfferTopics.PlanKey;
    private const string HappenContent = "# Final\n<!-- level:effects -->\nDeleting is final.\n<!-- level:pitfalls -->\nNo undo.";

    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private IProactiveTriggerDispatchRepository _dispatches = null!;
    private IAgentTriggerPreferenceService _preferences = null!;
    private IProactiveGovernanceResolver _governance = null!;
    private ISettingsReader _settings = null!;
    private IAgentMemoryRepository _memory = null!;
    private IPendingConfirmationRenewal _renewal = null!;
    private GateExplainOfferService _sut = null!;
    private string _userId = null!;

    [SetUp]
    public void SetUp()
    {
        AssistantTextCatalogues.ResetAll();
        _userId = Guid.NewGuid().ToString();
        _dispatches = Substitute.For<IProactiveTriggerDispatchRepository>();
        _preferences = Substitute.For<IAgentTriggerPreferenceService>();
        _governance = Substitute.For<IProactiveGovernanceResolver>();
        _settings = Substitute.For<ISettingsReader>();
        _memory = Substitute.For<IAgentMemoryRepository>();
        _renewal = Substitute.For<IPendingConfirmationRenewal>();

        _preferences.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(false);
        _dispatches.HasRecentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _memory.GetByKeyAsync(SkillNames.ExplainFinalVsCorrectable, Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = SkillNames.ExplainFinalVsCorrectable, Content = HappenContent });

        _sut = new GateExplainOfferService(
            _dispatches, _preferences, new ExplainOffersSwitch(_settings, _governance),
            new KnowledgeHappenExecutor(_memory, Substitute.For<ILogger<KnowledgeHappenExecutor>>()),
            _renewal, new SettableTimeProvider(Now), Substitute.For<ILogger<GateExplainOfferService>>());
    }

    [TearDown]
    public void TearDown() => AssistantTextCatalogues.ResetAll();

    private LLMContext Context(
        string? language = "de",
        string message = "Lösche die Einträge",
        ExplainExperienceLevel level = ExplainExperienceLevel.Beginner) => new()
    {
        UserId = _userId,
        ConversationId = ConversationId,
        Language = language,
        Message = message,
        TurnId = Guid.NewGuid(),
        ExplainDepth = new ExplainDepthProfile(level, false)
    };

    private static List<LLMFunctionCall> HeldGate(string skill) =>
    [
        new LLMFunctionCall
        {
            FunctionName = skill,
            Success = false,
            RequiresConfirmation = true,
            IssuedConfirmationToken = HeldToken
        }
    ];

    private static SuggestedRepliesConfig YesNo() => new()
    {
        SelectionMode = SuggestedReplySelectionModes.Single,
        Options =
        [
            new SuggestedReply { Label = "Ja", Value = "Ja" },
            new SuggestedReply { Label = "Nein", Value = "Nein" }
        ]
    };

    private static ProactiveTriggerDispatchRow Settled(ProactiveReaction reaction, string? outcome) => new()
    {
        Reaction = reaction,
        ContentParamsJson = new ExplainOfferMarker { Outcome = outcome }.ToJson()
    };

    private static List<ProactiveTriggerDispatchRow> Ignored(int count) =>
        Enumerable.Range(0, count).Select(_ => Settled(ProactiveReaction.Dismissed, ExplainOfferOutcomes.Other)).ToList();

    private ProactiveTriggerDispatchRow Pending(string kind = DeleteKind, DateTime? offeredAt = null) => new()
    {
        UserId = _userId,
        TriggerKind = kind,
        DedupKey = ConversationId + ExplainOfferDefaults.DedupKeySeparator + Guid.NewGuid().ToString("N"),
        CreateTime = offeredAt ?? Now.AddMinutes(-1),
        ContentParamsJson = new ExplainOfferMarker
        {
            Level = "beginner",
            GateSkill = DeleteSkill,
            TokenFingerprint = ConfirmationTokenHasher.Hash(HeldToken)
        }.ToJson()
    };

    private void GivenPending(ProactiveTriggerDispatchRow? row) =>
        _dispatches.FindPendingExplainOfferAsync(_userId, ConversationId, Arg.Any<CancellationToken>()).Returns(row);

    private void GivenSetting(string? value) =>
        _settings.GetSetting(SettingKeys.KlacksyExplainOffersEnabled)
            .Returns(value == null
                ? null
                : new Klacks.Api.Domain.Models.Settings.Settings
                {
                    Type = SettingKeys.KlacksyExplainOffersEnabled,
                    Value = value
                });

    private static string? OutcomeOf(ProactiveTriggerDispatchRow row) => ExplainOfferMarker.Parse(row.ContentParamsJson).Outcome;

    [Test]
    public async Task AtAGate_TheChipJoinsACopyOfTheModelsOptions_AndTheOfferIsRecordedWithTheGate()
    {
        var replies = YesNo();
        ProactiveTriggerDispatchRow? recorded = null;
        await _dispatches.RecordAsync(Arg.Do<ProactiveTriggerDispatchRow>(row => recorded = row), Arg.Any<CancellationToken>());

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), replies);

        result!.Options.Select(option => option.Label).ShouldBe(["Ja", "Nein", GermanChip]);
        result.Options[2].Value.ShouldBe(GermanChip);
        result.ShouldNotBeSameAs(replies);
        replies.Options.Select(option => option.Label).ShouldBe(["Ja", "Nein"]);

        recorded.ShouldNotBeNull();
        recorded!.UserId.ShouldBe(_userId);
        recorded.TriggerKind.ShouldBe(DeleteKind);
        recorded.ContentKey.ShouldBeNull();
        recorded.DedupKey.ShouldStartWith(ConversationId + ExplainOfferDefaults.DedupKeySeparator);
        var marker = ExplainOfferMarker.Parse(recorded.ContentParamsJson);
        marker.Level.ShouldBe("beginner");
        marker.GateSkill.ShouldBe(DeleteSkill);
        marker.TokenFingerprint.ShouldBe(ConfirmationTokenHasher.Hash(HeldToken));
        marker.Outcome.ShouldBeNull();
        recorded.ContentParamsJson!.ShouldNotContain(HeldToken);
    }

    [Test]
    public async Task WhenTheModelOfferedNoOptions_AYesNoPairWithTheChipIsSynthesized()
    {
        var result = await _sut.AugmentRepliesAsync(Context("en"), ConversationId, HeldGate(DeleteSkill), null);

        result!.SelectionMode.ShouldBe(SuggestedReplySelectionModes.Single);
        result.Options.Select(option => option.Label).ShouldBe(["Yes", "No", EnglishChip]);
    }

    [Test]
    public async Task ASuccessfulSealingPreview_IsAGateToo_WithoutAToken()
    {
        var calls = new List<LLMFunctionCall> { new() { FunctionName = "seal_open_orders", Success = true } };

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, calls, YesNo());

        result!.Options.Last().Label.ShouldBe(GermanChip);
        await _dispatches.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(row =>
                row.TriggerKind == ExplainOfferTopics.TriggerKindPrefix + ExplainOfferTopics.SealingKey
                && ExplainOfferMarker.Parse(row.ContentParamsJson).TokenFingerprint == null
                && ExplainOfferMarker.Parse(row.ContentParamsJson).GateSkill == "seal_open_orders"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutAGate_RepliesStayUntouched_AndNothingIsRecorded()
    {
        var replies = YesNo();
        var calls = new List<LLMFunctionCall> { new() { FunctionName = "get_scenarios", Success = true } };

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, calls, replies);

        result.ShouldBeSameAs(replies);
        result!.Options.Count.ShouldBe(2);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [TestCase(SuggestedReplySelectionModes.Date)]
    [TestCase(SuggestedReplySelectionModes.Number)]
    [TestCase(SuggestedReplySelectionModes.Multi)]
    public async Task AFieldOtherThanSingleChoice_GetsNoChip(string mode)
    {
        var replies = new SuggestedRepliesConfig { SelectionMode = mode };

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), replies);

        result.ShouldBeSameAs(replies);
        result!.Options.ShouldBeEmpty();
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task TheChipIsNotAddedTwice()
    {
        var replies = YesNo();
        replies.Options.Add(new SuggestedReply { Label = GermanChip, Value = GermanChip });

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), replies);

        result.ShouldBeSameAs(replies);
        result!.Options.Count.ShouldBe(3);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task AFullOptionList_GetsNoChip()
    {
        var replies = new SuggestedRepliesConfig { SelectionMode = SuggestedReplySelectionModes.Single };
        for (var i = 0; i < LlmRepliesFormat.MaxOptions; i++)
        {
            replies.Options.Add(new SuggestedReply { Label = $"Option {i}", Value = $"Option {i}" });
        }

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), replies);

        result!.Options.Count.ShouldBe(LlmRepliesFormat.MaxOptions);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [TestCase("false")]
    [TestCase("False")]
    public async Task TheSettingOff_SuppressesTheOffer(string value)
    {
        GivenSetting(value);

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), YesNo());

        result!.Options.Count.ShouldBe(2);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("true")]
    [TestCase("not-a-bool")]
    public async Task AbsentOrUnparseableOrTrueSetting_MeansOn(string? value)
    {
        GivenSetting(value);

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), YesNo());

        result!.Options.Count.ShouldBe(3);
    }

    [Test]
    public async Task TheKillSwitch_SuppressesTheOffer()
    {
        _governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), YesNo());

        result!.Options.Count.ShouldBe(2);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task AMutedOrSnoozedKind_SuppressesTheOffer()
    {
        _preferences.IsAllowedAsync(_userId, DeleteKind, AgentTriggerSeverity.Low).Returns(false);

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), YesNo());

        result!.Options.Count.ShouldBe(2);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task AnOfferOfTheKindWithinTheCooldown_SuppressesTheNext()
    {
        var expectedSince = Now.AddHours(-ExplainOfferDefaults.OfferCooldownHours);
        _dispatches.HasRecentAsync(_userId, DeleteKind, expectedSince, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), YesNo());

        result!.Options.Count.ShouldBe(2);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task AnAdvancedOnlyTopic_IsOfferedToAdvancedUsersAndNeverToBeginners()
    {
        var beginner = await _sut.AugmentRepliesAsync(
            Context(level: ExplainExperienceLevel.Beginner), ConversationId, HeldGate("create_plan"), YesNo());
        var advanced = await _sut.AugmentRepliesAsync(
            Context(level: ExplainExperienceLevel.Advanced), ConversationId, HeldGate("create_plan"), YesNo());

        beginner!.Options.Count.ShouldBe(2);
        advanced!.Options.Count.ShouldBe(3);
        await _dispatches.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(row =>
                row.TriggerKind == PlanKind && ExplainOfferMarker.Parse(row.ContentParamsJson).Level == "advanced"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUnknownLanguage_GetsTheEnglishChip()
    {
        var result = await _sut.AugmentRepliesAsync(Context("xx-XX"), ConversationId, HeldGate(DeleteSkill), YesNo());

        result!.Options.Last().Label.ShouldBe(EnglishChip);
    }

    [Test]
    public async Task VoiceCappedChannelAndUnidentifiedTurns_GetNoChip()
    {
        var voice = Context();
        voice.IsVoiceMode = true;
        var capped = Context();
        capped.CappedChannel = "telegram";
        var anonymous = Context();
        anonymous.UserId = "not-a-guid";

        foreach (var context in new[] { voice, capped, anonymous })
        {
            var result = await _sut.AugmentRepliesAsync(context, ConversationId, HeldGate(DeleteSkill), YesNo());
            result!.Options.Count.ShouldBe(2);
        }

        var noConversation = await _sut.AugmentRepliesAsync(Context(), null, HeldGate(DeleteSkill), YesNo());
        noConversation!.Options.Count.ShouldBe(2);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task AFailingRepository_NeverBreaksTheTurn_AndLeavesNoChipBehind()
    {
        var replies = YesNo();
        _dispatches.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("db down")));

        var result = await _sut.AugmentRepliesAsync(Context(), ConversationId, HeldGate(DeleteSkill), replies);

        result.ShouldBeSameAs(replies);
        replies.Options.Select(option => option.Label).ShouldBe(["Ja", "Nein"]);
    }

    [Test]
    public async Task TheChipLabelOfAnyLanguage_AcceptsThePendingOffer()
    {
        foreach (var message in new[] { GermanChip, EnglishChip, "  erst kurz erklären.  " })
        {
            var pending = Pending();
            GivenPending(pending);
            var context = Context(message: message);

            await _sut.PrepareTurnAsync(context);

            context.ExplainOfferAcceptance.ShouldBe(new ExplainOfferAcceptance(
                ExplainOfferTopics.DeleteFinalKey, DeleteSkill, ConfirmationTokenHasher.Hash(HeldToken)));
            context.ExplainOfferNote.ShouldNotBeNull();
            context.ExplainOfferNote.ShouldContain("Deleting is final.");
            context.ExplainOfferNote.ShouldContain("No undo.");
            context.ExplainOfferNote.ShouldContain(KnowledgeHappenExecutor.AnswerInstruction);
            pending.Reaction.ShouldBe(ProactiveReaction.Helpful);
            pending.ReactionAtUtc.ShouldBe(Now);
            OutcomeOf(pending).ShouldBe(ExplainOfferOutcomes.Accepted);
            ExplainOfferMarker.Parse(pending.ContentParamsJson).GateSkill.ShouldBe(DeleteSkill);
        }

        await _dispatches.DidNotReceive().GetRecentReactionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [TestCase("Ja", ExplainOfferOutcomes.Proceeded)]
    [TestCase("Ja, mach das", ExplainOfferOutcomes.Proceeded)]
    [TestCase("Nein", ExplainOfferOutcomes.Declined)]
    [TestCase("Nein danke, lieber nicht", ExplainOfferOutcomes.Declined)]
    [TestCase(UnrelatedMessage, ExplainOfferOutcomes.Other)]
    public async Task AnyOtherMessage_DismissesTheOffer_WithItsOutcome(string message, string outcome)
    {
        var pending = Pending();
        GivenPending(pending);
        var context = Context(message: message);

        await _sut.PrepareTurnAsync(context);

        context.ExplainOfferNote.ShouldBeNull();
        context.ExplainOfferAcceptance.ShouldBeNull();
        pending.Reaction.ShouldBe(ProactiveReaction.Dismissed);
        OutcomeOf(pending).ShouldBe(outcome);
        await _dispatches.Received(1).UpdateAsync(pending, Arg.Any<CancellationToken>());
    }

    [TestCase("Ja")]
    [TestCase("Nein")]
    public async Task AnswersToTheGate_NeverCountTowardsTheSnooze(string message)
    {
        GivenPending(Pending());
        _dispatches.GetRecentReactionsAsync(
                _userId, DeleteKind, ExplainOfferDefaults.IgnoredOffersBeforeLongSnooze, Arg.Any<CancellationToken>())
            .Returns(Ignored(3));

        await _sut.PrepareTurnAsync(Context(message: message));

        await _preferences.DidNotReceiveWithAnyArgs().SnoozeAsync(default!, default!, default);
    }

    [Test]
    public async Task AnOfferOlderThanTheMaximumAge_IsSettledAsExpired_EvenForTheChipLabel()
    {
        var pending = Pending(offeredAt: Now.AddMinutes(-(ExplainOfferDefaults.PendingOfferMaxAgeMinutes + 1)));
        GivenPending(pending);
        var context = Context(message: GermanChip);

        await _sut.PrepareTurnAsync(context);

        context.ExplainOfferNote.ShouldBeNull();
        context.ExplainOfferAcceptance.ShouldBeNull();
        pending.Reaction.ShouldBe(ProactiveReaction.Dismissed);
        OutcomeOf(pending).ShouldBe(ExplainOfferOutcomes.Expired);
    }

    [Test]
    public async Task AnOfferJustInsideTheMaximumAge_IsStillAccepted()
    {
        var pending = Pending(offeredAt: Now.AddMinutes(-(ExplainOfferDefaults.PendingOfferMaxAgeMinutes - 1)));
        GivenPending(pending);
        var context = Context(message: GermanChip);

        await _sut.PrepareTurnAsync(context);

        context.ExplainOfferAcceptance.ShouldNotBeNull();
        pending.Reaction.ShouldBe(ProactiveReaction.Helpful);
    }

    [Test]
    public async Task AHappenThatCannotBeRead_CountsTheOfferAsIgnoredInsteadOfAcceptingBlindly()
    {
        _memory.GetByKeyAsync(SkillNames.ExplainFinalVsCorrectable, Arg.Any<CancellationToken>()).Returns((AgentMemory?)null);
        var pending = Pending();
        GivenPending(pending);
        var context = Context(message: GermanChip);

        await _sut.PrepareTurnAsync(context);

        context.ExplainOfferNote.ShouldBeNull();
        context.ExplainOfferAcceptance.ShouldBeNull();
        pending.Reaction.ShouldBe(ProactiveReaction.Dismissed);
        OutcomeOf(pending).ShouldBe(ExplainOfferOutcomes.Other);
    }

    [Test]
    public async Task AnOfferOfAKindThatIsNoTopicAnymore_IsSettledAsIgnored()
    {
        var pending = Pending(ExplainOfferTopics.TriggerKindPrefix + "retired-topic");
        GivenPending(pending);
        var context = Context(message: GermanChip);

        await _sut.PrepareTurnAsync(context);

        context.ExplainOfferNote.ShouldBeNull();
        pending.Reaction.ShouldBe(ProactiveReaction.Dismissed);
    }

    [Test]
    public async Task WithoutAPendingOffer_NothingIsSettled()
    {
        GivenPending(null);
        var context = Context(message: GermanChip);

        await _sut.PrepareTurnAsync(context);

        context.ExplainOfferNote.ShouldBeNull();
        await _dispatches.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public async Task WithoutAConversation_NothingIsLookedUp()
    {
        var context = Context(message: GermanChip);
        context.ConversationId = null;

        await _sut.PrepareTurnAsync(context);

        await _dispatches.DidNotReceiveWithAnyArgs().FindPendingExplainOfferAsync(default!, default!, default);
    }

    [Test]
    public async Task ThreeIgnoredOffersInARow_SnoozeTheTopicForThirtyDays()
    {
        GivenPending(Pending());
        _dispatches.GetRecentReactionsAsync(
                _userId, DeleteKind, ExplainOfferDefaults.IgnoredOffersBeforeLongSnooze, Arg.Any<CancellationToken>())
            .Returns(Ignored(3));

        await _sut.PrepareTurnAsync(Context(message: UnrelatedMessage));

        await _preferences.Received(1).SnoozeAsync(
            _userId, DeleteKind, Now.AddDays(ExplainOfferDefaults.LongSnoozeDays));
    }

    [Test]
    public async Task TwoIgnored_OrAHelpfulOrAnAnsweredOneAmongThree_DoNotSnooze()
    {
        GivenPending(Pending());
        var withHelpful = Ignored(2);
        withHelpful.Add(Settled(ProactiveReaction.Helpful, ExplainOfferOutcomes.Accepted));
        var withProceeded = Ignored(2);
        withProceeded.Add(Settled(ProactiveReaction.Dismissed, ExplainOfferOutcomes.Proceeded));
        _dispatches.GetRecentReactionsAsync(
                _userId, DeleteKind, ExplainOfferDefaults.IgnoredOffersBeforeLongSnooze, Arg.Any<CancellationToken>())
            .Returns(Ignored(2), withHelpful, withProceeded);

        await _sut.PrepareTurnAsync(Context(message: UnrelatedMessage));
        await _sut.PrepareTurnAsync(Context(message: UnrelatedMessage));
        await _sut.PrepareTurnAsync(Context(message: UnrelatedMessage));

        await _preferences.DidNotReceiveWithAnyArgs().SnoozeAsync(default!, default!, default);
    }

    [Test]
    public async Task AfterAnAcceptedOffer_ExactlyTheOfferedConfirmationIsRenewed_AndTheModelsOptionsKept()
    {
        var context = Context();
        var fingerprint = ConfirmationTokenHasher.Hash(HeldToken);
        context.ExplainOfferAcceptance = new ExplainOfferAcceptance(ExplainOfferTopics.DeleteFinalKey, DeleteSkill, fingerprint);
        var replies = YesNo();

        var result = await _sut.AugmentRepliesAsync(context, ConversationId, [], replies);

        result.ShouldBeSameAs(replies);
        await _renewal.Received(1).RenewOfferedAsync(
            Guid.Parse(_userId), fingerprint, DeleteSkill,
            Arg.Is<IReadOnlyCollection<string>>(purposes =>
                purposes.SequenceEqual(new[] { PendingConfirmationPurposes.GateReplay })),
            PendingConfirmationChannels.Chat, Arg.Any<CancellationToken>());
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task TheScenarioTopic_AlsoRenewsAStaleOverwrite()
    {
        var context = Context();
        var fingerprint = ConfirmationTokenHasher.Hash(HeldToken);
        context.ExplainOfferAcceptance = new ExplainOfferAcceptance(
            ExplainOfferTopics.ScenarioAcceptKey, ScenarioAcceptDefaults.SkillName, fingerprint);

        await _sut.AugmentRepliesAsync(context, ConversationId, [], YesNo());

        await _renewal.Received(1).RenewOfferedAsync(
            Guid.Parse(_userId), fingerprint, ScenarioAcceptDefaults.SkillName,
            Arg.Is<IReadOnlyCollection<string>>(purposes =>
                purposes.Contains(PendingConfirmationPurposes.GateReplay)
                && purposes.Contains(PendingConfirmationPurposes.StaleScenarioOverwrite)
                && purposes.Count == 2),
            PendingConfirmationChannels.Chat, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASealingPreview_AnOfferWithoutFingerprint_OrAForeignGateSkill_RenewNothing()
    {
        var fingerprint = ConfirmationTokenHasher.Hash(HeldToken);
        var acceptances = new[]
        {
            new ExplainOfferAcceptance(ExplainOfferTopics.SealingKey, ExplainOfferTopics.SealOpenOrdersSkillName, null),
            new ExplainOfferAcceptance(ExplainOfferTopics.SealingKey, ExplainOfferTopics.SealOpenOrdersSkillName, fingerprint),
            new ExplainOfferAcceptance(ExplainOfferTopics.DeleteFinalKey, DeleteSkill, null),
            new ExplainOfferAcceptance(ExplainOfferTopics.DeleteFinalKey, null, fingerprint),
            new ExplainOfferAcceptance(ExplainOfferTopics.DeleteFinalKey, ScenarioAcceptDefaults.SkillName, fingerprint),
            new ExplainOfferAcceptance("retired-topic", DeleteSkill, fingerprint)
        };

        foreach (var acceptance in acceptances)
        {
            var context = Context();
            context.ExplainOfferAcceptance = acceptance;
            await _sut.AugmentRepliesAsync(context, ConversationId, [], YesNo());
        }

        await _renewal.DidNotReceiveWithAnyArgs().RenewOfferedAsync(default, default!, default!, default!, default!, default);
    }

    [Test]
    public async Task AfterAnAcceptedOffer_YesNoIsOfferedAgain_WhenTheModelGaveNoOptions()
    {
        var context = Context();
        context.ExplainOfferAcceptance = new ExplainOfferAcceptance(
            ExplainOfferTopics.DeleteFinalKey, DeleteSkill, ConfirmationTokenHasher.Hash(HeldToken));

        var result = await _sut.AugmentRepliesAsync(context, ConversationId, HeldGate(DeleteSkill), null);

        result!.Options.Select(option => option.Label).ShouldBe(["Ja", "Nein"]);
        await _dispatches.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }
}

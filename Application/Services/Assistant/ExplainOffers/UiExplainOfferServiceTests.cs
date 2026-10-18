// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for UiExplainOfferService (T5). State: offered only when the setting is not "false" (missing or unparseable =
/// on), the kill switch is off, the user is a beginner (advanced and an unresolved level get nothing) and the kind is not
/// muted; an unknown topic or a failing lookup yields "off" (fail-closed). Reactions: one counter row per outcome under
/// the "explain.ui." kind, without ContentKey, carrying only the outcome; yes and no mute at once, the second "later"
/// mutes, the third unanswered "shown" mutes; an unknown topic or outcome writes nothing; while the offers are off or
/// the topic is already muted a reaction is accepted but writes nothing, except the one answer to the offer whose "shown"
/// muted the topic (the user's newest row is that unanswered "shown").
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.ExplainOffers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.ExplainOffers;

[TestFixture]
public class UiExplainOfferServiceTests
{
    private const string Topic = UiExplainOfferTopics.ShiftHolidayOptionsKey;
    private const string Kind = UiExplainOfferTopics.TriggerKindPrefix + UiExplainOfferTopics.ShiftHolidayOptionsKey;

    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private IProactiveTriggerDispatchRepository _dispatches = null!;
    private IAgentTriggerPreferenceService _preferences = null!;
    private IProactiveGovernanceResolver _governance = null!;
    private ISettingsReader _settings = null!;
    private IExplainDepthResolver _depth = null!;
    private UiExplainOfferService _sut = null!;
    private string _userId = null!;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid().ToString();
        _dispatches = Substitute.For<IProactiveTriggerDispatchRepository>();
        _preferences = Substitute.For<IAgentTriggerPreferenceService>();
        _governance = Substitute.For<IProactiveGovernanceResolver>();
        _settings = Substitute.For<ISettingsReader>();
        _depth = Substitute.For<IExplainDepthResolver>();

        _preferences.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(false);
        GivenLevel(ExplainExperienceLevel.Beginner);
        _dispatches.GetRecentReactionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveTriggerDispatchRow>());

        _sut = new UiExplainOfferService(
            _dispatches, _preferences, new ExplainOffersSwitch(_settings, _governance), _depth,
            new SettableTimeProvider(Now), Substitute.For<ILogger<UiExplainOfferService>>());
    }

    private void GivenLevel(ExplainExperienceLevel? level) =>
        _depth.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(level == null ? null : new ExplainDepthProfile(level.Value, false));

    private void GivenSetting(string? value) =>
        _settings.GetSetting(SettingKeys.KlacksyExplainOffersEnabled)
            .Returns(value == null
                ? null
                : new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.KlacksyExplainOffersEnabled, Value = value });

    private static ProactiveTriggerDispatchRow Reacted(string outcome) => new()
    {
        TriggerKind = Kind,
        Reaction = ProactiveReaction.Dismissed,
        ContentParamsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["outcome"] = outcome })
    };

    private ProactiveTriggerDispatchRow RecordedRow() =>
        (ProactiveTriggerDispatchRow)_dispatches.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IProactiveTriggerDispatchRepository.RecordAsync))
            .GetArguments()[0]!;

    [Test]
    public async Task GetState_Beginner_SettingMissing_Offers()
    {
        GivenSetting(null);

        var state = await _sut.GetStateAsync(_userId, Topic);

        state.Enabled.ShouldBeTrue();
        state.ShouldOffer.ShouldBeTrue();
        await _preferences.Received(1).IsAllowedAsync(_userId, Kind, AgentTriggerSeverity.Low);
    }

    [TestCase("true")]
    [TestCase("not-a-bool")]
    [TestCase("")]
    public async Task GetState_SettingOnOrUnparseable_Offers(string value)
    {
        GivenSetting(value);

        (await _sut.GetStateAsync(_userId, Topic)).ShouldOffer.ShouldBeTrue();
    }

    [Test]
    public async Task GetState_SettingFalse_IsOff()
    {
        GivenSetting("false");

        var state = await _sut.GetStateAsync(_userId, Topic);

        state.ShouldBe(UiExplainOfferState.Off);
    }

    [Test]
    public async Task GetState_KillSwitchActive_IsOff()
    {
        _governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(true);

        (await _sut.GetStateAsync(_userId, Topic)).ShouldBe(UiExplainOfferState.Off);
    }

    [Test]
    public async Task GetState_Advanced_IsEnabledButNotOffered()
    {
        GivenLevel(ExplainExperienceLevel.Advanced);

        var state = await _sut.GetStateAsync(_userId, Topic);

        state.Enabled.ShouldBeTrue();
        state.ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task GetState_UnresolvedLevel_IsNotOffered()
    {
        GivenLevel(null);

        (await _sut.GetStateAsync(_userId, Topic)).ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task GetState_MutedKind_IsNotOffered()
    {
        _preferences.IsAllowedAsync(_userId, Kind, AgentTriggerSeverity.Low).Returns(false);

        (await _sut.GetStateAsync(_userId, Topic)).ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task GetState_UnknownTopic_IsOff_WithoutLookups()
    {
        var state = await _sut.GetStateAsync(_userId, "explain.topic.sealing");

        state.ShouldBe(UiExplainOfferState.Off);
        await _governance.DidNotReceive().IsKillSwitchActiveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetState_FailingLookup_IsOff()
    {
        _preferences.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<bool>(_ => throw new InvalidOperationException("db down"));

        (await _sut.GetStateAsync(_userId, Topic)).ShouldBe(UiExplainOfferState.Off);
    }

    [Test]
    public async Task GetState_Cancellation_IsNotSwallowed()
    {
        _governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => _sut.GetStateAsync(_userId, Topic));
    }

    [Test]
    public async Task Shown_WritesAnUnreactedLedgerOnlyRow_AndDoesNotMute()
    {
        _dispatches.CountUnreactedAsync(_userId, Kind, Arg.Any<CancellationToken>()).Returns(1);

        (await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Shown)).ShouldBeTrue();

        var row = RecordedRow();
        row.UserId.ShouldBe(_userId);
        row.TriggerKind.ShouldBe(Kind);
        row.ContentKey.ShouldBeNull();
        row.Reaction.ShouldBe(ProactiveReaction.None);
        row.ReactionAtUtc.ShouldBeNull();
        row.Severity.ShouldBe(AgentTriggerSeverity.Low);
        row.ContentParamsJson.ShouldBe("{\"outcome\":\"shown\"}");
        row.DedupKey.ShouldStartWith(Topic + ":shown:");
        await _preferences.DidNotReceive().MuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ThirdUnansweredShown_Mutes()
    {
        _dispatches.CountUnreactedAsync(_userId, Kind, Arg.Any<CancellationToken>())
            .Returns(UiExplainOfferDefaults.MaxShownBeforeMute);

        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Shown);

        await _preferences.Received(1).MuteAsync(_userId, Kind, true);
    }

    [Test]
    public async Task Accepted_WritesHelpfulRow_AndMutesForGood()
    {
        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Accepted);

        var row = RecordedRow();
        row.Reaction.ShouldBe(ProactiveReaction.Helpful);
        row.ReactionAtUtc.ShouldBe(Now);
        await _preferences.Received(1).MuteAsync(_userId, Kind, true);
    }

    [Test]
    public async Task Declined_WritesDismissedRow_AndMutesForGood()
    {
        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Declined);

        RecordedRow().Reaction.ShouldBe(ProactiveReaction.Dismissed);
        await _preferences.Received(1).MuteAsync(_userId, Kind, true);
    }

    [Test]
    public async Task FirstLater_WritesRowOnly()
    {
        _dispatches.GetRecentReactionsAsync(_userId, Kind, UiExplainOfferDefaults.MaxLaterBeforeMute, Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveTriggerDispatchRow> { Reacted(UiExplainOfferOutcomes.Later) });

        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Later);

        RecordedRow().Reaction.ShouldBe(ProactiveReaction.Dismissed);
        await _preferences.DidNotReceive().MuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task SecondLater_Mutes()
    {
        _dispatches.GetRecentReactionsAsync(_userId, Kind, UiExplainOfferDefaults.MaxLaterBeforeMute, Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveTriggerDispatchRow>
            {
                Reacted(UiExplainOfferOutcomes.Later),
                Reacted(UiExplainOfferOutcomes.Later)
            });

        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Later);

        await _preferences.Received(1).MuteAsync(_userId, Kind, true);
    }

    [Test]
    public async Task Later_IgnoresRowsWithOtherOrBrokenOutcomes()
    {
        var broken = new ProactiveTriggerDispatchRow { Reaction = ProactiveReaction.Dismissed, ContentParamsJson = "{not json" };
        _dispatches.GetRecentReactionsAsync(_userId, Kind, UiExplainOfferDefaults.MaxLaterBeforeMute, Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveTriggerDispatchRow> { Reacted(UiExplainOfferOutcomes.Later), broken });

        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Later);

        await _preferences.DidNotReceive().MuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [TestCase("false", false)]
    [TestCase(null, true)]
    public async Task Reaction_WhileOffersOff_IsAcceptedButWritesNothing(string? setting, bool killSwitch)
    {
        GivenSetting(setting);
        _governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(killSwitch);

        (await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Shown)).ShouldBeTrue();

        await _dispatches.DidNotReceive().RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
        await _preferences.DidNotReceive().MuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task Reaction_OnAMutedTopic_IsAcceptedButWritesNothing()
    {
        _preferences.IsAllowedAsync(_userId, Kind, AgentTriggerSeverity.Low).Returns(false);

        (await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();

        await _dispatches.DidNotReceive().RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
        await _preferences.DidNotReceive().MuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [TestCase(UiExplainOfferOutcomes.Accepted)]
    [TestCase(UiExplainOfferOutcomes.Later)]
    [TestCase(UiExplainOfferOutcomes.Declined)]
    public async Task AnswerToTheMutingOffer_IsStored_WithoutMutingAgain(string outcome)
    {
        _preferences.IsAllowedAsync(_userId, Kind, AgentTriggerSeverity.Low).Returns(false);
        _dispatches.FindNewestAsync(_userId, Kind, Arg.Any<CancellationToken>())
            .Returns(new ProactiveTriggerDispatchRow
            {
                TriggerKind = Kind,
                Reaction = ProactiveReaction.None,
                ContentParamsJson = "{\"outcome\":\"shown\"}"
            });

        (await _sut.RecordReactionAsync(_userId, Topic, outcome)).ShouldBeTrue();

        RecordedRow().ContentParamsJson.ShouldBe("{\"outcome\":\"" + outcome + "\"}");
        await _preferences.DidNotReceive().MuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task SecondAnswerAfterTheMute_WritesNothing()
    {
        _preferences.IsAllowedAsync(_userId, Kind, AgentTriggerSeverity.Low).Returns(false);
        _dispatches.FindNewestAsync(_userId, Kind, Arg.Any<CancellationToken>())
            .Returns(Reacted(UiExplainOfferOutcomes.Later));

        (await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();

        await _dispatches.DidNotReceive().RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ShownAfterTheMute_WritesNothing_EvenAfterAnUnansweredShown()
    {
        _preferences.IsAllowedAsync(_userId, Kind, AgentTriggerSeverity.Low).Returns(false);
        _dispatches.FindNewestAsync(_userId, Kind, Arg.Any<CancellationToken>())
            .Returns(new ProactiveTriggerDispatchRow { TriggerKind = Kind, Reaction = ProactiveReaction.None, ContentParamsJson = "{\"outcome\":\"shown\"}" });

        (await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Shown)).ShouldBeTrue();

        await _dispatches.DidNotReceive().RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnswerWhileOffersOff_WritesNothing_EvenAfterAnUnansweredShown()
    {
        GivenSetting("false");
        _preferences.IsAllowedAsync(_userId, Kind, AgentTriggerSeverity.Low).Returns(false);
        _dispatches.FindNewestAsync(_userId, Kind, Arg.Any<CancellationToken>())
            .Returns(new ProactiveTriggerDispatchRow { TriggerKind = Kind, Reaction = ProactiveReaction.None, ContentParamsJson = "{\"outcome\":\"shown\"}" });

        (await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();

        await _dispatches.DidNotReceive().RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
    }

    [TestCase("explain.topic.sealing", UiExplainOfferOutcomes.Accepted)]
    [TestCase(Topic, "ACCEPTED")]
    [TestCase(Topic, "helpful")]
    [TestCase(Topic, "")]
    public async Task UnknownTopicOrOutcome_WritesNothing(string topic, string outcome)
    {
        (await _sut.RecordReactionAsync(_userId, topic, outcome)).ShouldBeFalse();

        await _dispatches.DidNotReceive().RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
        await _preferences.DidNotReceive().MuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task EmptyUser_WritesNothing_AndIsOff()
    {
        (await _sut.RecordReactionAsync(" ", Topic, UiExplainOfferOutcomes.Accepted)).ShouldBeFalse();
        (await _sut.GetStateAsync(string.Empty, Topic)).ShouldBe(UiExplainOfferState.Off);

        await _dispatches.DidNotReceive().RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TwoReactions_GetDistinctDedupKeys()
    {
        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Shown);
        await _sut.RecordReactionAsync(_userId, Topic, UiExplainOfferOutcomes.Shown);

        var keys = _dispatches.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IProactiveTriggerDispatchRepository.RecordAsync))
            .Select(call => ((ProactiveTriggerDispatchRow)call.GetArguments()[0]!).DedupKey)
            .ToList();
        keys.Count.ShouldBe(2);
        keys.Distinct().Count().ShouldBe(2);
    }
}

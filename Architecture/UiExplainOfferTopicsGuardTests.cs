// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the UI explain offer topics (T5): every topic points at a happen that ships in skill-seeds.json, the trigger
/// kind prefix "explain.ui." never overlaps the T4 gate-offer prefix "explain.topic." (T4 reads every row below its own
/// prefix as a pending chip of the conversation and would settle a UI offer), the counter rows never feed the goal
/// reflection (TriggerHistoryGoalSignalSource neither aggregates nor warns about them), and the audience switch is
/// beginners only, the owner default of 2026-10-18.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UiExplainOfferTopicsGuardTests
{
    private const int RowsPerUser = 25;

    [Test]
    public void EveryTopic_PointsAtAShippedHappen_AndHasAUniqueKey()
    {
        var seeded = SeedSkillCatalog.Load().Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);

        UiExplainOfferTopics.All.ShouldNotBeEmpty();
        UiExplainOfferTopics.All.Select(topic => topic.Key).ShouldBeUnique();
        foreach (var topic in UiExplainOfferTopics.All)
        {
            seeded.ShouldContain(topic.HappenSkillName, $"topic {topic.Key}");
        }
    }

    [Test]
    public void ThePrefix_NeverOverlapsTheGateOfferPrefix()
    {
        UiExplainOfferTopics.TriggerKindPrefix.StartsWith(ExplainOfferTopics.TriggerKindPrefix, StringComparison.Ordinal).ShouldBeFalse();
        ExplainOfferTopics.TriggerKindPrefix.StartsWith(UiExplainOfferTopics.TriggerKindPrefix, StringComparison.Ordinal).ShouldBeFalse();

        foreach (var topic in UiExplainOfferTopics.All)
        {
            var kind = UiExplainOfferTopics.TriggerKindOf(topic);
            ExplainOfferTopics.IsOfferKind(kind).ShouldBeFalse();
            ExplainOfferTopics.FindByTriggerKind(kind).ShouldBeNull();
            UiExplainOfferTopics.IsOfferKind(kind).ShouldBeTrue();
            AgentTriggerKinds.All.ShouldNotContain(kind);
        }
    }

    [Test]
    public void TheAudience_IsBeginnersOnly()
    {
        UiExplainOfferDefaults.OfferedLevels.ShouldBe([ExplainExperienceLevel.Beginner]);
    }

    [Test]
    public async Task GoalReflection_IgnoresTheCounterRows_WithoutWarning()
    {
        var kind = UiExplainOfferTopics.TriggerKindOf(UiExplainOfferTopics.All[0]);
        var rows = Enumerable.Range(0, RowsPerUser)
            .Select(index => new ProactiveTriggerDispatchRow
            {
                Id = Guid.NewGuid(),
                UserId = "user-a",
                TriggerKind = kind,
                DedupKey = Guid.NewGuid().ToString(),
                CreateTime = DateTime.UtcNow.AddHours(-index)
            })
            .ToList();
        var repository = Substitute.For<IProactiveTriggerDispatchRepository>();
        repository.GetSinceAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var logger = Substitute.For<ILogger<TriggerHistoryGoalSignalSource>>();

        var signals = await new TriggerHistoryGoalSignalSource(repository, logger).CollectAsync();

        signals.ShouldBeEmpty();
        logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => (LogLevel)call.GetArguments()[0]!)
            .ShouldNotContain(LogLevel.Warning);
    }
}

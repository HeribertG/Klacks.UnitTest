// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Schedules.KeywordWishes;
using Klacks.Api.Application.DTOs.Schedules.Summary;
using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Application.Services.Schedules.AutoWizard;

/// <summary>
/// The gating and keep rules of the AutoWizard's soft-keyword retry: only a requested retry of a Hard run with open slots
/// is wanted, and the soft scenario is only kept when it fills strictly more slots.
/// </summary>
[TestFixture]
public sealed class AutoWizardSoftKeywordRetryPlannerTests
{
    private static readonly DateOnly From = new(2026, 11, 2);
    private static readonly DateOnly Until = new(2026, 11, 8);
    private static readonly Guid Known = Guid.NewGuid();
    private static readonly Guid Unknown = Guid.NewGuid();

    private static StartAutoWizardRequest Request(bool softRetry, KeywordMode mode = KeywordMode.Hard)
        => new(From, Until, [Guid.NewGuid()], null, null, null, null, KeywordMode: mode, SoftKeywordRetry: softRetry);

    private static ScenarioSummaryDto Summary(int demanded, int filled, KeywordWishesDto? wishes = null)
        => new(Guid.NewGuid(), From, Until, 1, filled, demanded, filled, [], [], KeywordWishes: wishes);

    [Test]
    public void IsWanted_RequestedHardRunWithOpenSlots_IsTrue()
        => AutoWizardSoftKeywordRetryPlanner.IsWanted(Request(softRetry: true), Summary(10, 8)).ShouldBeTrue();

    [Test]
    public void IsWanted_NotRequested_IsFalse()
        => AutoWizardSoftKeywordRetryPlanner.IsWanted(Request(softRetry: false), Summary(10, 8)).ShouldBeFalse();

    [Test]
    public void IsWanted_RunAlreadySoft_IsFalse()
        => AutoWizardSoftKeywordRetryPlanner.IsWanted(Request(softRetry: true, KeywordMode.Soft), Summary(10, 8)).ShouldBeFalse();

    [Test]
    public void IsWanted_NoOpenSlot_IsFalse()
        => AutoWizardSoftKeywordRetryPlanner.IsWanted(Request(softRetry: true), Summary(10, 10)).ShouldBeFalse();

    [Test]
    public void IsWanted_WithoutStrictSummary_IsFalse()
        => AutoWizardSoftKeywordRetryPlanner.IsWanted(Request(softRetry: true), null).ShouldBeFalse();

    [Test]
    public void IsBetter_MoreFilledSlots_IsTrue()
        => AutoWizardSoftKeywordRetryPlanner.IsBetter(Summary(10, 8), Summary(10, 9)).ShouldBeTrue();

    [Test]
    public void IsBetter_EqualFilledSlots_IsFalse()
        => AutoWizardSoftKeywordRetryPlanner.IsBetter(Summary(10, 8), Summary(10, 8)).ShouldBeFalse();

    [Test]
    public void IsBetter_FewerFilledSlots_IsFalse()
        => AutoWizardSoftKeywordRetryPlanner.IsBetter(Summary(10, 8), Summary(10, 7)).ShouldBeFalse();

    [Test]
    public void IsBetter_WithoutSoftSummary_IsFalse()
        => AutoWizardSoftKeywordRetryPlanner.IsBetter(Summary(10, 8), null).ShouldBeFalse();

    [Test]
    public void BuildAlternative_CarriesTheAdditionalSlotsAndNamesTheMissedWishes()
    {
        var day = new DateOnly(2026, 11, 3);
        var wishes = new KeywordWishesDto(1, 0, 0, 1,
        [
            new KeywordWishItemDto(Known, day, "Free", "BrokenFree"),
            new KeywordWishItemDto(Unknown, day, "NotFree", "Unfulfilled"),
        ]);
        var stage = new AutoWizardStageScenario("HolisticHarmonizer", Guid.NewGuid(), Guid.NewGuid(), "Soft plan");

        var alternative = AutoWizardSoftKeywordRetryPlanner.BuildAlternative(
            stage, Summary(10, 7), Summary(10, 9, wishes), new Dictionary<Guid, string> { [Known] = "Anna Muster" });

        alternative.ScenarioId.ShouldBe(stage.ScenarioId);
        alternative.ScenarioToken.ShouldBe(stage.Token);
        alternative.KeywordMode.ShouldBe(KeywordMode.Soft);
        alternative.AdditionalFilledSlots.ShouldBe(2);
        alternative.BrokenWishes.ShouldBe(
        [
            new AutoWizardBrokenWishDto(Known, "Anna Muster", day, "Free", "BrokenFree"),
            new AutoWizardBrokenWishDto(Unknown, Unknown.ToString(), day, "NotFree", "Unfulfilled"),
        ]);
    }

    [Test]
    public void BuildAlternative_DoesNotBlameTheSoftPlanForAWishTheStrictPlanMissesAsWell()
    {
        var day = new DateOnly(2026, 11, 4);
        var shared = new KeywordWishItemDto(Known, day, "NotFree", "Unfulfilled");
        var softOnly = new KeywordWishItemDto(Known, day, "Free", "BrokenFree");
        var stage = new AutoWizardStageScenario("HolisticHarmonizer", Guid.NewGuid(), Guid.NewGuid(), "Soft plan");

        var alternative = AutoWizardSoftKeywordRetryPlanner.BuildAlternative(
            stage,
            Summary(10, 7, new KeywordWishesDto(0, 0, 0, 1, [shared])),
            Summary(10, 9, new KeywordWishesDto(1, 0, 0, 1, [shared, softOnly])),
            new Dictionary<Guid, string> { [Known] = "Anna Muster" });

        alternative.BrokenWishes.ShouldBe([new AutoWizardBrokenWishDto(Known, "Anna Muster", day, "Free", "BrokenFree")]);
        alternative.Summary.KeywordWishes.ShouldNotBeNull().Items.Count.ShouldBe(2);
    }

    [Test]
    public void BuildSoftScenarioNote_NamesTheStrictScenarioAndTheCounts()
    {
        var note = AutoWizardSoftKeywordRetryPlanner.BuildSoftScenarioNote(3, new KeywordWishesDto(2, 1, 0, 4, []), "Plan KW45");

        note.ShouldContain("'Plan KW45'");
        note.ShouldContain("3 more slot(s)");
        note.ShouldContain("FREE 2");
        note.ShouldContain("unfulfilled 4");
    }
}

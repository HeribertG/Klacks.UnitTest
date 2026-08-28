// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the weekly learning digest. The two rules that matter: it stays silent when nothing was
/// learned, and it reports the FINISHED week - a digest over the running week would be empty every Monday
/// morning and would therefore never fire at all.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Triggers;

using System.Globalization;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class KlacksyLearnedDigestDetectorTests
{
    // A Wednesday, so the reported week is unambiguously the previous Monday to Sunday.
    private static readonly DateTime Today = new(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpectedWindowStart = new(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpectedWindowEnd = new(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc);

    private ISkillLearningClusterRepository _clusters = null!;
    private ICompanyClock _clock = null!;
    private KlacksyLearnedDigestDetector _detector = null!;

    [SetUp]
    public void SetUp()
    {
        _clusters = Substitute.For<ISkillLearningClusterRepository>();
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _detector = new KlacksyLearnedDigestDetector(_clusters, _clock);
    }

    private void GivenCounts(Dictionary<string, int> counts) =>
        _clusters.CountByStatusInWindowAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(counts);

    [Test]
    public void Kind_IsTheLearnedDigestKind()
    {
        _detector.Kind.ShouldBe(AgentTriggerKinds.KlacksyLearnedDigest);
    }

    [Test]
    public async Task NothingLearned_EmitsNoEvent()
    {
        GivenCounts([]);

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task SomethingLearned_EmitsExactlyOneEvent()
    {
        GivenCounts(new Dictionary<string, int>
        {
            [SkillLearningClusterStatuses.LearnedPhrase] = 2,
            [SkillLearningClusterStatuses.LearnedCapability] = 1,
            [SkillLearningClusterStatuses.Ready] = 3,
            [SkillLearningClusterStatuses.Unfulfillable] = 1
        });

        var events = await _detector.DetectAsync();

        events.Count.ShouldBe(1);
        var digest = events[0].ShouldBeOfType<KlacksyLearnedDigestTriggerEvent>();
        digest.Phrases.ShouldBe(2);
        digest.Capabilities.ShouldBe(1);
        digest.Unfulfillable.ShouldBe(4);
        digest.Total.ShouldBe(7);
    }

    [Test]
    public async Task TheReportedWindow_IsThePreviousIsoWeek()
    {
        GivenCounts(new Dictionary<string, int> { [SkillLearningClusterStatuses.LearnedPhrase] = 1 });

        await _detector.DetectAsync();

        await _clusters.Received(1).CountByStatusInWindowAsync(
            Arg.Any<IReadOnlyList<string>>(),
            ExpectedWindowStart,
            ExpectedWindowEnd,
            Arg.Any<CancellationToken>());
    }

    // Sunday is the last day of the ISO week, not the first - getting that wrong shifts every window by a day.
    [Test]
    public async Task OnASunday_TheWindowIsStillThePreviousIsoWeek()
    {
        _clock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc));
        GivenCounts(new Dictionary<string, int> { [SkillLearningClusterStatuses.LearnedPhrase] = 1 });

        await _detector.DetectAsync();

        await _clusters.Received(1).CountByStatusInWindowAsync(
            Arg.Any<IReadOnlyList<string>>(),
            new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheEvent_IsAdminOnlyMediumAndDeepLinksToSettings()
    {
        GivenCounts(new Dictionary<string, int> { [SkillLearningClusterStatuses.LearnedPhrase] = 1 });

        var digest = (await _detector.DetectAsync())[0];

        digest.AdminOnly.ShouldBeTrue();
        digest.PlannersOnly.ShouldBeFalse();
        digest.TargetUserId.ShouldBeNull();
        digest.Severity.ShouldBe(AgentTriggerSeverity.Medium);
        digest.RequiresGroupScope.ShouldBeFalse();
        digest.ActionRoute.ShouldBe(ProactiveActionRoutes.Settings);
        digest.ActionParams.ShouldNotBeNull();
        digest.ActionParams![ProactiveActionParamKeys.Target]
            .ShouldBe(ProactiveActionRoutes.SettingsTargetKlacksyLearning);
        digest.Summary.ShouldBe(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.KlacksyLearnedDigest);
    }

    [Test]
    public async Task TheDedupKey_IsTheIsoWeekOfTheReportedWindow()
    {
        GivenCounts(new Dictionary<string, int> { [SkillLearningClusterStatuses.LearnedPhrase] = 1 });

        var digest = (await _detector.DetectAsync())[0];

        digest.DedupKey.ShouldBe("2026-W34");
    }

    [Test]
    public async Task TheSummaryParams_CarryEveryCounterTheTextInterpolates()
    {
        GivenCounts(new Dictionary<string, int>
        {
            [SkillLearningClusterStatuses.LearnedPhrase] = 2,
            [SkillLearningClusterStatuses.Ready] = 1
        });

        var digest = (await _detector.DetectAsync())[0];

        digest.SummaryParams.ShouldNotBeNull();
        digest.SummaryParams!["phrases"].ShouldBe("2");
        digest.SummaryParams["capabilities"].ShouldBe("0");
        digest.SummaryParams["unfulfillable"].ShouldBe("1");
        digest.SummaryParams["total"].ShouldBe("3");
    }

    [Test]
    public async Task WithNothingLearned_NoFingerprintIsHeldOpen()
    {
        GivenCounts([]);

        (await _detector.GetActiveFingerprintsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task TheFingerprint_MatchesTheEventsLedgerSpelling()
    {
        GivenCounts(new Dictionary<string, int> { [SkillLearningClusterStatuses.LearnedPhrase] = 1 });

        var digest = (await _detector.DetectAsync())[0];
        var fingerprints = await _detector.GetActiveFingerprintsAsync();

        fingerprints.ShouldHaveSingleItem()
            .ShouldBe(AgentConditionLedgerPolicy.FingerprintFor(digest));
    }

    [Test]
    public void TheDedupKeyHelper_UsesIsoWeekNumbering()
    {
        // 2026-01-01 is a Thursday, so it belongs to ISO week 1 of 2026.
        KlacksyLearnedDigestTriggerEvent
            .DedupKeyFor(new DateOnly(2026, 1, 1))
            .ShouldBe(string.Create(CultureInfo.InvariantCulture, $"2026-W01"));
    }
}

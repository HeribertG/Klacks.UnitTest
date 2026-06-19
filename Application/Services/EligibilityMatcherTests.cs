// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the pure EligibilityMatcher: a mandatory requirement is satisfied only by an
/// in-window qualification at or above the required level; otherwise it is classified Missing /
/// Expired / InsufficientLevel. Non-mandatory requirements never produce a gap.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Application.Services;

[TestFixture]
public class EligibilityMatcherTests
{
    private static readonly Guid QualId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 6, 15);

    private static ShiftRequiredQualification Req(
        bool mandatory = true, QualificationLevel min = QualificationLevel.Proficient, Guid? qualId = null)
        => new() { QualificationId = qualId ?? QualId, IsMandatory = mandatory, MinLevel = min };

    private static ClientQualification Held(
        QualificationLevel level, DateOnly? from = null, DateOnly? until = null, Guid? qualId = null)
        => new() { QualificationId = qualId ?? QualId, Level = level, ValidFrom = from, ValidUntil = until };

    [Test]
    public void Eligible_InWindowAndSufficientLevel_NoGap()
    {
        var gaps = EligibilityMatcher.FindMandatoryGaps(
            [Req(min: QualificationLevel.Proficient)],
            [Held(QualificationLevel.Advanced, from: new(2026, 1, 1), until: new(2026, 12, 31))],
            Date);

        gaps.ShouldBeEmpty();
    }

    [Test]
    public void Eligible_OpenEndedWindow_NoGap()
    {
        var gaps = EligibilityMatcher.FindMandatoryGaps(
            [Req(min: QualificationLevel.Low)],
            [Held(QualificationLevel.Low)],
            Date);

        gaps.ShouldBeEmpty();
    }

    [Test]
    public void Missing_NoSuchQualification_GapMissing()
    {
        var gaps = EligibilityMatcher.FindMandatoryGaps([Req()], [], Date);

        gaps.Count.ShouldBe(1);
        gaps[0].Reason.ShouldBe(QualificationGapReason.Missing);
        gaps[0].QualificationId.ShouldBe(QualId);
    }

    [Test]
    public void Expired_SufficientLevelButOutOfWindow_GapExpired()
    {
        var gaps = EligibilityMatcher.FindMandatoryGaps(
            [Req(min: QualificationLevel.Proficient)],
            [Held(QualificationLevel.Expert, from: new(2025, 1, 1), until: new(2026, 1, 1))],
            Date);

        gaps.Count.ShouldBe(1);
        gaps[0].Reason.ShouldBe(QualificationGapReason.Expired);
    }

    [Test]
    public void InsufficientLevel_InWindowButTooLow_GapInsufficientLevel()
    {
        var gaps = EligibilityMatcher.FindMandatoryGaps(
            [Req(min: QualificationLevel.Advanced)],
            [Held(QualificationLevel.Basic, from: new(2026, 1, 1), until: new(2026, 12, 31))],
            Date);

        gaps.Count.ShouldBe(1);
        gaps[0].Reason.ShouldBe(QualificationGapReason.InsufficientLevel);
    }

    [Test]
    public void NonMandatoryRequirement_NeverProducesGap()
    {
        var gaps = EligibilityMatcher.FindMandatoryGaps(
            [Req(mandatory: false, min: QualificationLevel.Expert)],
            [],
            Date);

        gaps.ShouldBeEmpty();
    }

    [Test]
    public void NoRequirements_NoGap()
    {
        EligibilityMatcher.FindMandatoryGaps([], [Held(QualificationLevel.Expert)], Date).ShouldBeEmpty();
    }

    [Test]
    public void MultipleRequirements_OnlyUnmetSurface()
    {
        var other = Guid.NewGuid();
        var gaps = EligibilityMatcher.FindMandatoryGaps(
            [Req(min: QualificationLevel.Low, qualId: QualId), Req(min: QualificationLevel.Expert, qualId: other)],
            [Held(QualificationLevel.Proficient, qualId: QualId)],
            Date);

        gaps.Count.ShouldBe(1);
        gaps[0].QualificationId.ShouldBe(other);
        gaps[0].Reason.ShouldBe(QualificationGapReason.Missing);
    }
}

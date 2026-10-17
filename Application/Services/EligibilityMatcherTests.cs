// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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

    [Test]
    public void InsufficientLevel_CarriesTheHighestInWindowLevel()
    {
        // Arrange
        var requirement = Req(min: QualificationLevel.Expert);

        // Act
        var gaps = EligibilityMatcher.FindMandatoryGaps(
            [requirement],
            [Held(QualificationLevel.Basic), Held(QualificationLevel.Proficient), Held(QualificationLevel.Advanced, until: new(2026, 1, 1))],
            Date);

        // Assert
        gaps.Count.ShouldBe(1);
        gaps[0].Reason.ShouldBe(QualificationGapReason.InsufficientLevel);
        gaps[0].HeldLevel.ShouldBe(QualificationLevel.Proficient);
        gaps[0].LevelDelta.ShouldBe(2);
    }

    [TestCase(null, false, QualificationGapSeverity.Warning)]
    [TestCase(null, true, QualificationGapSeverity.Error)]
    [TestCase(true, false, QualificationGapSeverity.Error)]
    [TestCase(false, true, QualificationGapSeverity.Warning)]
    public void InsufficientLevel_SeverityFollowsTheEffectiveBinding(
        bool? levelBindingOverride, bool qualificationIsLevelBinding, QualificationGapSeverity expected)
    {
        // Arrange
        var requirement = Req(min: QualificationLevel.Proficient);
        requirement.LevelBindingOverride = levelBindingOverride;
        requirement.Qualification = new Klacks.Api.Domain.Models.Staffs.Qualification { IsLevelBinding = qualificationIsLevelBinding };

        // Act
        var gaps = EligibilityMatcher.FindGaps([requirement], [Held(QualificationLevel.Basic)], Date);

        // Assert
        gaps.Single().IsLevelBinding.ShouldBe(expected == QualificationGapSeverity.Error);
        gaps.Single().Severity.ShouldBe(expected);
    }

    [TestCase(null, false, false, QualificationGapSeverity.Warning)]
    [TestCase(true, false, false, QualificationGapSeverity.Error)]
    [TestCase(false, false, true, QualificationGapSeverity.Error)]
    [TestCase(false, true, false, QualificationGapSeverity.Warning)]
    public void Expired_AndTooLow_IsExpiredWithTheLevelGap_SeverityFollowsBindingOrExpirySetting(
        bool? levelBindingOverride, bool qualificationIsLevelBinding, bool expiredMandatoryBlocks, QualificationGapSeverity expected)
    {
        // Arrange
        var requirement = Req(min: QualificationLevel.Advanced);
        requirement.LevelBindingOverride = levelBindingOverride;
        requirement.Qualification = new Klacks.Api.Domain.Models.Staffs.Qualification { IsLevelBinding = qualificationIsLevelBinding };
        var expiredRows = new[]
        {
            Held(QualificationLevel.Low, until: new(2026, 1, 1)),
            Held(QualificationLevel.Basic, from: new(2025, 1, 1), until: new(2026, 3, 1)),
        };

        // Act
        var gap = EligibilityMatcher.FindGaps([requirement], expiredRows, Date, expiredMandatoryBlocks).Single();

        // Assert
        gap.Reason.ShouldBe(QualificationGapReason.Expired);
        gap.HeldLevel.ShouldBe(QualificationLevel.Basic);
        gap.LevelDelta.ShouldBe(2);
        gap.Severity.ShouldBe(expected);
    }

    [Test]
    public void Expired_AtTheRequiredLevel_BindingLevel_StaysAWarning()
    {
        // Arrange
        var requirement = Req(min: QualificationLevel.Proficient);
        requirement.LevelBindingOverride = true;

        // Act
        var gap = EligibilityMatcher.FindGaps([requirement], [Held(QualificationLevel.Expert, until: new(2026, 1, 1))], Date).Single();

        // Assert
        gap.Reason.ShouldBe(QualificationGapReason.Expired);
        gap.LevelDelta.ShouldBe(0);
        gap.Severity.ShouldBe(QualificationGapSeverity.Warning);
    }

    [Test]
    public void Missing_BlocksEvenWhenTheLevelIsOnlyAHint()
    {
        // Arrange
        var requirement = Req(min: QualificationLevel.Proficient);
        requirement.LevelBindingOverride = false;

        // Act
        var gaps = EligibilityMatcher.FindMandatoryGaps([requirement], [], Date);

        // Assert
        gaps.Single().Reason.ShouldBe(QualificationGapReason.Missing);
        gaps.Single().Severity.ShouldBe(QualificationGapSeverity.Error);
    }
}

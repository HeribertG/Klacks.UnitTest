// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class QualificationGapSeverityTests
{
    private static readonly Guid Qual = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [TestCase(QualificationGapReason.Missing, true, false, false, QualificationGapSeverity.Error)]
    [TestCase(QualificationGapReason.Missing, true, true, false, QualificationGapSeverity.Error)]
    [TestCase(QualificationGapReason.Missing, false, false, false, QualificationGapSeverity.Warning)]
    [TestCase(QualificationGapReason.InsufficientLevel, true, false, false, QualificationGapSeverity.Warning)]
    [TestCase(QualificationGapReason.InsufficientLevel, true, true, false, QualificationGapSeverity.Error)]
    [TestCase(QualificationGapReason.InsufficientLevel, false, true, false, QualificationGapSeverity.Warning)]
    [TestCase(QualificationGapReason.Expired, true, false, false, QualificationGapSeverity.Warning)]
    [TestCase(QualificationGapReason.Expired, true, true, false, QualificationGapSeverity.Error)]
    [TestCase(QualificationGapReason.Expired, false, true, false, QualificationGapSeverity.Warning)]
    [TestCase(QualificationGapReason.Expired, true, false, true, QualificationGapSeverity.Error)]
    [TestCase(QualificationGapReason.Expired, false, false, true, QualificationGapSeverity.Warning)]
    public void Severity_FollowsMandatoryBindingAndExpirySetting(
        QualificationGapReason reason, bool mandatory, bool binding, bool expiredBlocks, QualificationGapSeverity expected)
    {
        // Arrange
        var gap = new QualificationGap(Qual, reason, QualificationLevel.Proficient, mandatory, expiredBlocks, QualificationLevel.Basic, binding);

        // Act
        var severity = gap.Severity;

        // Assert
        severity.ShouldBe(expected);
    }

    [Test]
    public void LevelDelta_InsufficientLevel_IsTheNumberOfMissingLevels()
    {
        // Arrange
        var gap = new QualificationGap(Qual, QualificationGapReason.InsufficientLevel, QualificationLevel.Advanced, HeldLevel: QualificationLevel.Basic);

        // Act
        var delta = gap.LevelDelta;

        // Assert
        delta.ShouldBe(2);
    }

    [Test]
    public void LevelDelta_Missing_IsZero()
    {
        // Arrange
        var gap = new QualificationGap(Qual, QualificationGapReason.Missing, QualificationLevel.Advanced, HeldLevel: QualificationLevel.Basic);

        // Act
        var delta = gap.LevelDelta;

        // Assert
        delta.ShouldBe(0);
    }

    [TestCase(QualificationLevel.Basic, 2)]
    [TestCase(QualificationLevel.Expert, 0)]
    public void LevelDelta_Expired_CountsTheLevelsBelowTheMinimum(QualificationLevel held, int expected)
    {
        // Arrange
        var gap = new QualificationGap(Qual, QualificationGapReason.Expired, QualificationLevel.Advanced, HeldLevel: held);

        // Act
        var delta = gap.LevelDelta;

        // Assert
        delta.ShouldBe(expected);
    }

    [Test]
    public void Severity_ExpiredAtTheRequiredLevel_BindingLevel_IsAWarning()
    {
        // Arrange
        var gap = new QualificationGap(
            Qual, QualificationGapReason.Expired, QualificationLevel.Proficient, HeldLevel: QualificationLevel.Expert, IsLevelBinding: true);

        // Act
        var severity = gap.Severity;

        // Assert
        severity.ShouldBe(QualificationGapSeverity.Warning);
        gap.IsBindingLevelBlock.ShouldBeFalse();
    }

    [TestCase(QualificationGapReason.InsufficientLevel, false, true)]
    [TestCase(QualificationGapReason.Expired, false, true)]
    [TestCase(QualificationGapReason.Expired, true, false)]
    public void IsBindingLevelBlock_OnlyWhenTheBindingLevelIsWhatBlocks(
        QualificationGapReason reason, bool expiredBlocks, bool expected)
    {
        // Arrange
        var gap = new QualificationGap(
            Qual, reason, QualificationLevel.Proficient, true, expiredBlocks, QualificationLevel.Basic, IsLevelBinding: true);

        // Act
        var isLevelBlock = gap.IsBindingLevelBlock;

        // Assert
        gap.Severity.ShouldBe(QualificationGapSeverity.Error);
        isLevelBlock.ShouldBe(expected);
    }

    [Test]
    public void CommentParams_ExpiredAndTooLow_CarryHeldLevelAndDelta()
    {
        // Arrange
        var gap = new QualificationGap(Qual, QualificationGapReason.Expired, QualificationLevel.Proficient, HeldLevel: QualificationLevel.Low);

        // Act
        var parameters = QualificationGapCommentParams.Build(gap);

        // Assert
        parameters[QualificationValidationParamKeys.HeldLevel].ShouldBe(nameof(QualificationLevel.Low));
        parameters[QualificationValidationParamKeys.LevelDelta].ShouldBe("2");
    }

    [Test]
    public void CommentParams_ExpiredAtTheRequiredLevel_CarryNoLevel()
    {
        // Arrange
        var gap = new QualificationGap(Qual, QualificationGapReason.Expired, QualificationLevel.Proficient, HeldLevel: QualificationLevel.Expert);

        // Act
        var parameters = QualificationGapCommentParams.Build(gap);

        // Assert
        parameters.ContainsKey(QualificationValidationParamKeys.HeldLevel).ShouldBeFalse();
    }

    [Test]
    public void CommentParams_InsufficientLevel_CarryHeldLevelAndDelta()
    {
        // Arrange
        var gap = new QualificationGap(Qual, QualificationGapReason.InsufficientLevel, QualificationLevel.Proficient, HeldLevel: QualificationLevel.Basic);

        // Act
        var parameters = QualificationGapCommentParams.Build(gap);

        // Assert
        parameters[QualificationValidationParamKeys.QualificationId].ShouldBe(Qual.ToString());
        parameters[QualificationValidationParamKeys.MinLevel].ShouldBe(nameof(QualificationLevel.Proficient));
        parameters[QualificationValidationParamKeys.HeldLevel].ShouldBe(nameof(QualificationLevel.Basic));
        parameters[QualificationValidationParamKeys.LevelDelta].ShouldBe("1");
    }

    [Test]
    public void CommentParams_Missing_CarryOnlyQualificationAndMinLevel()
    {
        // Arrange
        var gap = new QualificationGap(Qual, QualificationGapReason.Missing, QualificationLevel.Proficient);

        // Act
        var parameters = QualificationGapCommentParams.Build(gap);

        // Assert
        parameters.Keys.ShouldBe(
            [QualificationValidationParamKeys.QualificationId, QualificationValidationParamKeys.MinLevel],
            ignoreOrder: true);
    }
}

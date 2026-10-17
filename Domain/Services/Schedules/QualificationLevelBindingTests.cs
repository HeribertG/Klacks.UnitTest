// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class QualificationLevelBindingTests
{
    [TestCase(null, false, false)]
    [TestCase(null, true, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, true)]
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    public void IsBinding_OverrideWinsAndNullInherits(bool? levelBindingOverride, bool qualificationDefault, bool expected)
    {
        // Act
        var result = QualificationLevelBinding.IsBinding(levelBindingOverride, qualificationDefault);

        // Assert
        result.ShouldBe(expected);
    }

    [TestCase(null, true, true)]
    [TestCase(false, true, false)]
    [TestCase(true, false, true)]
    public void IsBinding_Requirement_ReadsTheLoadedQualification(bool? levelBindingOverride, bool qualificationDefault, bool expected)
    {
        // Arrange
        var requirement = new ShiftRequiredQualification
        {
            MinLevel = QualificationLevel.Proficient,
            LevelBindingOverride = levelBindingOverride,
            Qualification = new Qualification { Name = new MultiLanguage { De = "Pflege" }, IsLevelBinding = qualificationDefault },
        };

        // Act
        var result = QualificationLevelBinding.IsBinding(requirement);

        // Assert
        result.ShouldBe(expected);
    }

    [Test]
    public void IsBinding_RequirementWithoutQualification_FallsBackToTheNonBindingDefault()
    {
        // Arrange
        var requirement = new ShiftRequiredQualification { MinLevel = QualificationLevel.Proficient };

        // Act
        var result = QualificationLevelBinding.IsBinding(requirement);

        // Assert
        result.ShouldBeFalse();
    }
}

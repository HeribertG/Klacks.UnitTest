// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner rule 2026-11-07 for the planning profile (Klacksy): a cap (daily hours, weekly hours, consecutive days)
/// is at least 1, a rest minimum (pause hours, rest days) may be 0.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Settings;

namespace Klacks.UnitTest.Domain.Services.Settings;

[TestFixture]
public class PlanningProfileCapBoundsTests
{
    private PlanningProfileDraftValidator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new PlanningProfileDraftValidator(new PlanningProfileParameterCatalog());
    }

    [TestCase(PlanningProfileParameterNames.MaxDailyHours)]
    [TestCase(PlanningProfileParameterNames.MaxWeeklyHours)]
    [TestCase(PlanningProfileParameterNames.MaxConsecutiveDays)]
    public void Validate_CapZero_IsInvalid(string parameterName)
    {
        _sut.Validate(parameterName, "0").IsValid.ShouldBeFalse();
    }

    [TestCase(PlanningProfileParameterNames.MaxDailyHours)]
    [TestCase(PlanningProfileParameterNames.MaxWeeklyHours)]
    [TestCase(PlanningProfileParameterNames.MaxConsecutiveDays)]
    public void Validate_CapOne_IsValid(string parameterName)
    {
        _sut.Validate(parameterName, "1").IsValid.ShouldBeTrue();
    }

    [TestCase(PlanningProfileParameterNames.MinPauseHours)]
    [TestCase(PlanningProfileParameterNames.MinRestDays)]
    public void Validate_RestMinimumZero_IsValid(string parameterName)
    {
        _sut.Validate(parameterName, "0").IsValid.ShouldBeTrue();
    }
}

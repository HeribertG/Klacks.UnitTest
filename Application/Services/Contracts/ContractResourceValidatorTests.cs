// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ContractResourceValidator: the rules shared by create_contract and
/// create_contract_from_template with their original wording, and the stricter handler-equivalent rejection
/// that turns a would-be POST exception into a clean skill error.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Services.Contracts;

namespace Klacks.UnitTest.Application.Services.Contracts;

[TestFixture]
public class ContractResourceValidatorTests
{
    private static ContractResource Valid() => new()
    {
        Name = "X",
        GuaranteedHours = 100m,
        MinimumHours = 100m,
        MaximumHours = 100m,
        FullTime = 180m,
        ValidFrom = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public void ValidResource_HasNoViolation()
    {
        ContractResourceValidator.FindViolation(Valid()).ShouldBeNull();
        ContractResourceValidator.FindHandlerRejection(Valid()).ShouldBeNull();
    }

    [Test]
    public void NegativeValues_AreRejectedByParameterName()
    {
        var negativeGuaranteed = Valid();
        negativeGuaranteed.GuaranteedHours = -1m;
        ContractResourceValidator.FindViolation(negativeGuaranteed).ShouldBe("Parameter 'guaranteedHours' must not be negative.");

        var negativePercent = Valid();
        negativePercent.Percent = -5m;
        ContractResourceValidator.FindViolation(negativePercent).ShouldBe("Parameter 'percent' must not be negative.");

        var negativeSaRate = Valid();
        negativeSaRate.WE1Rate = -0.1m;
        ContractResourceValidator.FindViolation(negativeSaRate).ShouldBe("Parameter 'saRate' must not be negative.");

        var negativeFullTime = Valid();
        negativeFullTime.FullTime = -1m;
        ContractResourceValidator.FindViolation(negativeFullTime).ShouldBe("Parameter 'fullTime' must not be negative.");
    }

    [Test]
    public void MinimumAboveMaximum_IsRejected_OnlyWhenMaximumIsPositive()
    {
        var violating = Valid();
        violating.GuaranteedHours = null;
        violating.MinimumHours = 50m;
        violating.MaximumHours = 40m;
        ContractResourceValidator.FindViolation(violating).ShouldBe("Parameter 'minimumHours' must not exceed 'maximumHours'.");

        var unconfiguredMaximum = Valid();
        unconfiguredMaximum.GuaranteedHours = null;
        unconfiguredMaximum.MinimumHours = 50m;
        unconfiguredMaximum.MaximumHours = 0m;
        ContractResourceValidator.FindViolation(unconfiguredMaximum).ShouldBeNull();
    }

    [Test]
    public void HandlerRejection_CatchesMinimumAboveZeroMaximum_ThatTheSkillRuleLetsPass()
    {
        var resource = Valid();
        resource.GuaranteedHours = null;
        resource.MinimumHours = 50m;
        resource.MaximumHours = 0m;

        ContractResourceValidator.FindHandlerRejection(resource).ShouldNotBeNull();
    }

    [Test]
    public void GuaranteedHoursOutsideTheBand_IsRejected()
    {
        var resource = Valid();
        resource.MinimumHours = 120m;
        resource.MaximumHours = 140m;

        ContractResourceValidator.FindViolation(resource)
            .ShouldBe("Parameter 'guaranteedHours' must be between 'minimumHours' and 'maximumHours'.");
    }

    [Test]
    public void ValidUntilNotAfterValidFrom_IsRejected()
    {
        var resource = Valid();
        resource.ValidUntil = resource.ValidFrom;

        ContractResourceValidator.FindViolation(resource).ShouldBe("Parameter 'validUntil' must be after 'validFrom'.");
    }
}

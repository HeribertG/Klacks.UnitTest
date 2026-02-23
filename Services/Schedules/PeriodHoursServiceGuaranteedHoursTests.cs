// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.PeriodHours;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class PeriodHoursServiceGuaranteedHoursTests
{
    private const decimal DefaultGuaranteedHours = 170m;

    [Test]
    public void GetEffectiveGuaranteedHours_NoContract_ReturnsDefault()
    {
        // Arrange
        Contract? contract = null;

        // Act
        var result = PeriodHoursService.GetEffectiveGuaranteedHours(contract, DefaultGuaranteedHours);

        // Assert
        result.Should().Be(DefaultGuaranteedHours);
    }

    [Test]
    public void GetEffectiveGuaranteedHours_ContractWithSchedulingRule_ReturnsContractHours()
    {
        // Arrange
        var schedulingRule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Default Rule",
            GuaranteedHours = 20m
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            SchedulingRuleId = schedulingRule.Id,
            SchedulingRule = schedulingRule,
            GuaranteedHours = 40m
        };

        // Act
        var result = PeriodHoursService.GetEffectiveGuaranteedHours(contract, DefaultGuaranteedHours);

        // Assert
        result.Should().Be(40m);
    }

    [Test]
    public void GetEffectiveGuaranteedHours_ContractWithSchedulingRuleButNullHours_ReturnsDefault()
    {
        // Arrange
        var schedulingRule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Default Rule",
            GuaranteedHours = 20m
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            SchedulingRuleId = schedulingRule.Id,
            SchedulingRule = schedulingRule,
            GuaranteedHours = null
        };

        // Act
        var result = PeriodHoursService.GetEffectiveGuaranteedHours(contract, DefaultGuaranteedHours);

        // Assert
        result.Should().Be(DefaultGuaranteedHours);
    }

    [Test]
    public void GetEffectiveGuaranteedHours_ContractWithoutSchedulingRule_ReturnsDefault()
    {
        // Arrange
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            SchedulingRuleId = null,
            SchedulingRule = null,
            GuaranteedHours = 30m
        };

        // Act
        var result = PeriodHoursService.GetEffectiveGuaranteedHours(contract, DefaultGuaranteedHours);

        // Assert
        result.Should().Be(DefaultGuaranteedHours);
    }

    [Test]
    public void GetEffectiveGuaranteedHours_ContractOverridesDefault()
    {
        // Arrange
        var schedulingRule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Default Rule",
            GuaranteedHours = 20m
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Override Contract",
            SchedulingRuleId = schedulingRule.Id,
            SchedulingRule = schedulingRule,
            GuaranteedHours = 35m
        };

        // Act
        var result = PeriodHoursService.GetEffectiveGuaranteedHours(contract, DefaultGuaranteedHours);

        // Assert
        result.Should().Be(35m);
        result.Should().NotBe(DefaultGuaranteedHours);
    }

    [Test]
    public void GetEffectiveGuaranteedHours_NoContract_ZeroDefault_ReturnsZero()
    {
        // Arrange
        Contract? contract = null;

        // Act
        var result = PeriodHoursService.GetEffectiveGuaranteedHours(contract, 0m);

        // Assert
        result.Should().Be(0m);
    }
}

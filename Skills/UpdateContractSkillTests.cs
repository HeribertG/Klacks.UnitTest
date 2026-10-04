// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateContractSkillTests
{
    private static readonly ICompanyClock CompanyClock =
        new FixedCompanyClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditContracts" }
    };

    private static ContractResource Contract(Guid id) => new()
    {
        Id = id,
        Name = "Vollzeit 160",
        GuaranteedHours = 160m,
        MinimumHours = 140m,
        MaximumHours = 180m,
        FullTime = 160m,
        ValidFrom = new DateTime(2026, 1, 1),
        ValidUntil = null
    };

    [Test]
    public async Task ValidFrom_TodayWord_ResolvesToCompanyLocalDay_NotUtcDay()
    {
        // Pacific/Auckland edge case: 23:30 UTC on 27.06 is already 11:30 NZST (+12, no June DST) on
        // 28.06 - "heute" must resolve to the company's local calendar day, not the UTC day.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var aucklandClock = new FixedCompanyClock(new DateTimeOffset(2026, 6, 27, 23, 30, 0, TimeSpan.Zero), auckland);
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator, aucklandClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["validFrom"] = "heute"
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.ValidFrom == new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidFrom_UnreadableValue_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["validFrom"] = "not-a-date"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateNameAndHours_DispatchesPutCommand_WithMergedValues()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["name"] = "Vollzeit 170",
            ["guaranteedHours"] = 170m,
            ["maximumHours"] = 190m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.Id == contractId &&
                c.Resource.Name == "Vollzeit 170" &&
                c.Resource.GuaranteedHours == 170m &&
                c.Resource.MaximumHours == 190m &&
                c.Resource.MinimumHours == 140m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValidFrom_PersistsKindUtc_SoNpgsqlAcceptsTheTimestamptzWrite()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["validFrom"] = "2026-04-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.ValidFrom.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MinimumAboveMaximum_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["minimumHours"] = 200m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NegativeHours_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["guaranteedHours"] = -5m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownContract_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = Guid.NewGuid().ToString(),
            ["name"] = "Renamed"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetPercent_DispatchesPutCommand_WithPercent()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["percent"] = 80m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.Percent == 80m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearGuaranteedHoursWithPercent_SetsContractToInheriting()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["clearGuaranteedHours"] = true,
            ["percent"] = 80m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.GuaranteedHours == null &&
                c.Resource.Percent == 80m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearPercent_RemovesPercent()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        var existing = Contract(contractId);
        existing.Percent = 60m;
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(existing);
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["clearPercent"] = true
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.Percent == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoFieldsSupplied_ReturnsSuccess_WithoutPut()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = new UpdateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    private static async Task<(SkillResult Result, IMediator Mediator)> RunAsync(
        ContractResource existing, Dictionary<string, object> parameters)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(existing);
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator, CompanyClock);
        parameters["contractId"] = existing.Id.ToString();
        var result = await skill.ExecuteAsync(Ctx(), parameters);
        return (result, mediator);
    }

    [Test]
    public async Task ExplicitZeroNightRate_OnStandardContract_IsStoredAsZero()
    {
        var existing = Contract(Guid.NewGuid());

        var (result, mediator) = await RunAsync(existing, new Dictionary<string, object> { ["nightRate"] = 0m });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.NightRate == 0m && c.Resource.HolidayRate == null),
            Arg.Any<CancellationToken>());
    }

    [TestCase("clearNightRate")]
    [TestCase("clearHolidayRate")]
    [TestCase("clearSaRate")]
    [TestCase("clearSoRate")]
    public async Task ClearRate_ResetsTheRateToStandard(string clearFlag)
    {
        var existing = Contract(Guid.NewGuid());
        existing.NightRate = 0.1m;
        existing.HolidayRate = 0.2m;
        existing.WE1Rate = 0.3m;
        existing.WE2Rate = 0.4m;

        var (result, mediator) = await RunAsync(existing, new Dictionary<string, object> { [clearFlag] = true });

        result.Success.ShouldBeTrue(result.Message);
        var expectedNight = clearFlag == "clearNightRate" ? (decimal?)null : 0.1m;
        var expectedHoliday = clearFlag == "clearHolidayRate" ? (decimal?)null : 0.2m;
        var expectedSa = clearFlag == "clearSaRate" ? (decimal?)null : 0.3m;
        var expectedSo = clearFlag == "clearSoRate" ? (decimal?)null : 0.4m;
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.NightRate == expectedNight &&
                c.Resource.HolidayRate == expectedHoliday &&
                c.Resource.WE1Rate == expectedSa &&
                c.Resource.WE2Rate == expectedSo),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NegativeNightRate_ReturnsError_NoMutation()
    {
        var (result, mediator) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object> { ["nightRate"] = -0.1m });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PerformsShiftWorkFalse_IsStoredExplicitly()
    {
        var (result, mediator) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object> { ["performsShiftWork"] = false });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.PerformsShiftWork == false),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearPerformsShiftWork_ResetsTheFlagToStandard()
    {
        var existing = Contract(Guid.NewGuid());
        existing.PerformsShiftWork = true;

        var (result, mediator) = await RunAsync(existing, new Dictionary<string, object> { ["clearPerformsShiftWork"] = true });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.PerformsShiftWork == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RateTogetherWithItsClearFlag_ReturnsError_NoMutation()
    {
        var (result, mediator) = await RunAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { ["nightRate"] = 0.2m, ["clearNightRate"] = true });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ShiftWorkTogetherWithItsClearFlag_ReturnsError_NoMutation()
    {
        var (result, mediator) = await RunAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { ["performsShiftWork"] = true, ["clearPerformsShiftWork"] = true });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }
}

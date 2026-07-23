// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Handlers.IndustryTemplates;
using Klacks.Api.Application.Queries.IndustryTemplates;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.IndustryTemplates;

[TestFixture]
public class PreviewQueryHandlerTests
{
    private ISchedulingRuleRepository _schedulingRuleRepository = null!;
    private IQualificationRepository _qualificationRepository = null!;
    private PreviewQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _schedulingRuleRepository = Substitute.For<ISchedulingRuleRepository>();
        _qualificationRepository = Substitute.For<IQualificationRepository>();
        _handler = new PreviewQueryHandler(
            _schedulingRuleRepository,
            _qualificationRepository,
            Substitute.For<ILogger<PreviewQueryHandler>>());
    }

    [Test]
    public async Task Handle_KnownIndustry_ReturnsMappedSchedulingRulesAndQualifications()
    {
        _schedulingRuleRepository.GetByIndustryAsync("healthcare").Returns(new List<SchedulingRule>
        {
            new() { Id = Guid.NewGuid(), Name = "Healthcare preset", Industry = "healthcare" },
        });
        _qualificationRepository.GetByIndustryAsync("healthcare", Arg.Any<CancellationToken>()).Returns(new List<Qualification>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = new MultiLanguage { De = "Anästhesiepflege" },
                Description = new MultiLanguage { De = "Fachpflege" },
                Industry = "healthcare",
            },
        });

        var result = await _handler.Handle(new PreviewQuery("healthcare"), CancellationToken.None);

        result.Industry.ShouldBe("healthcare");
        result.SchedulingRules.Single().Name.ShouldBe("Healthcare preset");
        result.SchedulingRules.Single().Description.ShouldBeNull();
        result.Qualifications.Single().Name.De.ShouldBe("Anästhesiepflege");
        result.Qualifications.Single().Description.ShouldNotBeNull();
        result.Qualifications.Single().Description!.De.ShouldBe("Fachpflege");
    }

    [Test]
    public async Task Handle_SchedulingRuleWithAllPlanningFieldsSet_MapsEveryConcreteValue()
    {
        _schedulingRuleRepository.GetByIndustryAsync("healthcare").Returns(new List<SchedulingRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Healthcare preset",
                Industry = "healthcare",
                DefaultWorkingHours = 8.5m,
                FullTimeHours = 42m,
                MaxDailyHours = 10m,
                MaxWeeklyHours = 45m,
                MaxConsecutiveDays = 6,
                MinRestDays = 1,
                MinPauseHours = 0.5m,
                OvertimeThreshold = 40m,
                OvertimeBasis = OvertimeBasis.Week,
                OvertimeRateMode = SurchargeRateMode.Multiplier,
                OvertimeTier1AfterHours = 40m,
                OvertimeTier1Rate = 1.25m,
                OvertimeTier2AfterHours = 50m,
                OvertimeTier2Rate = 1.5m,
                OvertimeTier3AfterHours = 60m,
                OvertimeTier3Rate = 2m,
                NightStart = "22:00",
                NightEnd = "06:00",
                NightRate = 1.1m,
                HolidayRate = 1.5m,
                WE1Rate = 1.2m,
                WE2Rate = 1.3m,
                WE3Rate = 1.4m,
                VacationDaysPerYear = 25,
                MaxWorkDays = 5,
                MaxOptimalGap = 2m,
                GuaranteedHours = 100m,
                MaximumHours = 180m,
                MinimumHours = 80m,
            },
        });
        _qualificationRepository.GetByIndustryAsync("healthcare", Arg.Any<CancellationToken>()).Returns(new List<Qualification>());

        var result = await _handler.Handle(new PreviewQuery("healthcare"), CancellationToken.None);

        var rule = result.SchedulingRules.Single();
        rule.DefaultWorkingHours.ShouldBe(8.5m);
        rule.FullTimeHours.ShouldBe(42m);
        rule.MaxDailyHours.ShouldBe(10m);
        rule.MaxWeeklyHours.ShouldBe(45m);
        rule.MaxConsecutiveDays.ShouldBe(6);
        rule.MinRestDays.ShouldBe(1);
        rule.MinPauseHours.ShouldBe(0.5m);
        rule.OvertimeThreshold.ShouldBe(40m);
        rule.OvertimeBasis.ShouldBe("Week");
        rule.OvertimeRateMode.ShouldBe("Multiplier");
        rule.OvertimeTier1AfterHours.ShouldBe(40m);
        rule.OvertimeTier1Rate.ShouldBe(1.25m);
        rule.OvertimeTier2AfterHours.ShouldBe(50m);
        rule.OvertimeTier2Rate.ShouldBe(1.5m);
        rule.OvertimeTier3AfterHours.ShouldBe(60m);
        rule.OvertimeTier3Rate.ShouldBe(2m);
        rule.NightStart.ShouldBe("22:00");
        rule.NightEnd.ShouldBe("06:00");
        rule.NightRate.ShouldBe(1.1m);
        rule.HolidayRate.ShouldBe(1.5m);
        rule.Weekend1Rate.ShouldBe(1.2m);
        rule.Weekend2Rate.ShouldBe(1.3m);
        rule.Weekend3Rate.ShouldBe(1.4m);
        rule.VacationDaysPerYear.ShouldBe(25);
        rule.MaxWorkDays.ShouldBe(5);
        rule.MaxOptimalGap.ShouldBe(2m);
        rule.GuaranteedHours.ShouldBe(100m);
        rule.MaximumHours.ShouldBe(180m);
        rule.MinimumHours.ShouldBe(80m);
    }

    [Test]
    public async Task Handle_SchedulingRuleWithoutPlanningFieldsSet_MapsEveryPlanningFieldToNull()
    {
        _schedulingRuleRepository.GetByIndustryAsync("healthcare").Returns(new List<SchedulingRule>
        {
            new() { Id = Guid.NewGuid(), Name = "Bare preset", Industry = "healthcare" },
        });
        _qualificationRepository.GetByIndustryAsync("healthcare", Arg.Any<CancellationToken>()).Returns(new List<Qualification>());

        var result = await _handler.Handle(new PreviewQuery("healthcare"), CancellationToken.None);

        var rule = result.SchedulingRules.Single();
        rule.DefaultWorkingHours.ShouldBeNull();
        rule.FullTimeHours.ShouldBeNull();
        rule.MaxDailyHours.ShouldBeNull();
        rule.MaxWeeklyHours.ShouldBeNull();
        rule.MaxConsecutiveDays.ShouldBeNull();
        rule.MinRestDays.ShouldBeNull();
        rule.MinPauseHours.ShouldBeNull();
        rule.OvertimeThreshold.ShouldBeNull();
        rule.OvertimeBasis.ShouldBeNull();
        rule.OvertimeRateMode.ShouldBeNull();
        rule.OvertimeTier1AfterHours.ShouldBeNull();
        rule.OvertimeTier1Rate.ShouldBeNull();
        rule.OvertimeTier2AfterHours.ShouldBeNull();
        rule.OvertimeTier2Rate.ShouldBeNull();
        rule.OvertimeTier3AfterHours.ShouldBeNull();
        rule.OvertimeTier3Rate.ShouldBeNull();
        rule.NightStart.ShouldBeNull();
        rule.NightEnd.ShouldBeNull();
        rule.NightRate.ShouldBeNull();
        rule.HolidayRate.ShouldBeNull();
        rule.Weekend1Rate.ShouldBeNull();
        rule.Weekend2Rate.ShouldBeNull();
        rule.Weekend3Rate.ShouldBeNull();
        rule.VacationDaysPerYear.ShouldBeNull();
        rule.MaxWorkDays.ShouldBeNull();
        rule.MaxOptimalGap.ShouldBeNull();
        rule.GuaranteedHours.ShouldBeNull();
        rule.MaximumHours.ShouldBeNull();
        rule.MinimumHours.ShouldBeNull();
    }

    [Test]
    public async Task Handle_SlugWithMixedCaseAndWhitespace_IsNormalizedBeforeLookup()
    {
        _schedulingRuleRepository.GetByIndustryAsync("security").Returns(new List<SchedulingRule>());
        _qualificationRepository.GetByIndustryAsync("security", Arg.Any<CancellationToken>()).Returns(new List<Qualification>());

        var result = await _handler.Handle(new PreviewQuery("  SECURITY  "), CancellationToken.None);

        result.Industry.ShouldBe("security");
        await _schedulingRuleRepository.Received(1).GetByIndustryAsync("security");
    }

    [Test]
    public async Task Handle_UnknownSlug_ThrowsInvalidRequestException()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(new PreviewQuery("not-an-industry"), CancellationToken.None));
    }

    [Test]
    public async Task Handle_CustomMarker_ThrowsInvalidRequestException()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(new PreviewQuery("custom"), CancellationToken.None));
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for <see cref="ApplyPlanningProfileCommandHandler"/>: an industry base copies every
/// template scheduling rule as a NEW customer-owned row (fresh id, empty Industry and import keys, the
/// templates themselves untouched) with the field overrides applied to each copy, a scratch base creates
/// exactly one blank rule from the overrides, both set ACTIVE_INDUSTRIES to the custom marker and clear
/// the draft, and an incomplete draft (missing base industry, or scratch with no override) is rejected
/// before anything is persisted. Uses the real persistent draft store (backed by an EF InMemory
/// database) and validator with a substituted scheduling-rule repository, settings repository and unit
/// of work.
/// </summary>

using Klacks.Api.Application.Commands.PlanningProfile;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Application.Handlers.PlanningProfile;
using Klacks.Api.Domain.Services.Settings;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.PlanningProfile;

[TestFixture]
public class ApplyPlanningProfileCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string Key = "conv-1";

    private IPendingPlanningProfileDraftStore _store = null!;
    private PlanningProfileParameterCatalog _catalog = null!;
    private PlanningProfileDraftValidator _validator = null!;
    private ISchedulingRuleRepository _schedulingRules = null!;
    private ISettingsRepository _settings = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ApplyPlanningProfileCommandHandler _sut = null!;
    private List<SchedulingRule> _added = null!;

    [SetUp]
    public void Setup()
    {
        _store = PendingStoreTestFactory.CreatePlanningProfileDraftStore();
        _catalog = new PlanningProfileParameterCatalog();
        _validator = new PlanningProfileDraftValidator(_catalog);
        _schedulingRules = Substitute.For<ISchedulingRuleRepository>();
        _settings = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _added = new List<SchedulingRule>();
        _schedulingRules.When(r => r.Add(Arg.Any<SchedulingRule>())).Do(ci => _added.Add(ci.Arg<SchedulingRule>()));
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<List<string>>>>())
            .Returns(ci => ci.ArgAt<Func<Task<List<string>>>>(0)());

        _sut = new ApplyPlanningProfileCommandHandler(_store, _validator, _catalog, _schedulingRules, _settings, _unitOfWork);
    }

    private static ApplyPlanningProfileCommand Cmd() => new(UserId, Key);

    [Test]
    public void Handle_NoDraft_Throws()
    {
        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));
    }

    [Test]
    public void Handle_MissingBaseIndustry_Throws_NothingPersisted()
    {
        _store.Set(UserId, Key, new PlanningProfileDraft());

        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));

        _added.ShouldBeEmpty();
        _schedulingRules.DidNotReceive().Add(Arg.Any<SchedulingRule>());
        _store.Get(UserId, Key).ShouldNotBeNull();
    }

    [Test]
    public void Handle_Scratch_NoOverride_Throws()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = PlanningProfileBaseChoices.Scratch;
        _store.Set(UserId, Key, draft);

        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));
        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_Industry_CopiesTemplatesAsNewCustomerOwnedRows_TemplatesUntouched()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = IndustrySlugs.Homecare;
        _store.Set(UserId, Key, draft);

        var template = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Homecare day",
            Industry = IndustrySlugs.Homecare,
            ImportSourceKey = "homecare/day",
            ImportContentHash = "hash",
            MaxWeeklyHours = 40m
        };
        _schedulingRules.GetByIndustryAsync(IndustrySlugs.Homecare).Returns(new List<SchedulingRule> { template });

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.CreatedRuleCount.ShouldBe(1);
        _added.Count.ShouldBe(1);

        var copy = _added[0];
        copy.Id.ShouldNotBe(template.Id);
        copy.Name.ShouldBe("Homecare day");
        copy.Industry.ShouldBe(string.Empty);
        copy.ImportSourceKey.ShouldBe(string.Empty);
        copy.ImportContentHash.ShouldBe(string.Empty);
        copy.MaxWeeklyHours.ShouldBe(40m);

        template.Industry.ShouldBe(IndustrySlugs.Homecare);
        template.ImportSourceKey.ShouldBe("homecare/day");
    }

    [Test]
    public async Task Handle_Industry_AppliesOverridesToEveryCopy_AndSetsActiveIndustriesCustom()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = IndustrySlugs.Homecare;
        draft.Parameters[PlanningProfileParameterNames.MaxWeeklyHours] = "42";
        draft.Parameters[PlanningProfileParameterNames.MinRestDays] = "2";
        _store.Set(UserId, Key, draft);

        _schedulingRules.GetByIndustryAsync(IndustrySlugs.Homecare).Returns(new List<SchedulingRule>
        {
            new() { Id = Guid.NewGuid(), Name = "Homecare day", Industry = IndustrySlugs.Homecare, MaxWeeklyHours = 40m },
            new() { Id = Guid.NewGuid(), Name = "Homecare night", Industry = IndustrySlugs.Homecare, MaxWeeklyHours = 38m }
        });

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result!.CreatedRuleCount.ShouldBe(2);
        _added.ShouldAllBe(r => r.MaxWeeklyHours == 42m);
        _added.ShouldAllBe(r => r.MinRestDays == 2);
        await _settings.Received(1).UpsertSettingAsync(SettingKeys.ActiveIndustries, IndustrySlugs.Custom);
        _store.Get(UserId, Key).ShouldBeNull();
    }

    [Test]
    public async Task Handle_Scratch_CreatesSingleRule_WithOverrides_AndCustomName()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = PlanningProfileBaseChoices.Scratch;
        draft.Parameters[PlanningProfileParameterNames.ProfileName] = "My rule";
        draft.Parameters[PlanningProfileParameterNames.DefaultWorkingHours] = "8";
        _store.Set(UserId, Key, draft);

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result!.CreatedRuleCount.ShouldBe(1);
        _added.Count.ShouldBe(1);
        _added[0].Name.ShouldBe("My rule");
        _added[0].Industry.ShouldBe(string.Empty);
        _added[0].DefaultWorkingHours.ShouldBe(8m);
        await _schedulingRules.DidNotReceive().GetByIndustryAsync(Arg.Any<string>());
        await _settings.Received(1).UpsertSettingAsync(SettingKeys.ActiveIndustries, IndustrySlugs.Custom);
    }

    [Test]
    public async Task Handle_Scratch_AppliesEveryOverrideField_IncludingEnumAndTime()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = PlanningProfileBaseChoices.Scratch;
        draft.Parameters[PlanningProfileParameterNames.DefaultWorkingHours] = "8";
        draft.Parameters[PlanningProfileParameterNames.FullTimeHours] = "40";
        draft.Parameters[PlanningProfileParameterNames.MaxDailyHours] = "10";
        draft.Parameters[PlanningProfileParameterNames.MaxWeeklyHours] = "45";
        draft.Parameters[PlanningProfileParameterNames.MaxConsecutiveDays] = "6";
        draft.Parameters[PlanningProfileParameterNames.MinRestDays] = "2";
        draft.Parameters[PlanningProfileParameterNames.MinPauseHours] = "11";
        draft.Parameters[PlanningProfileParameterNames.OvertimeThreshold] = "42";
        draft.Parameters[PlanningProfileParameterNames.OvertimeBasis] = "Week";
        draft.Parameters[PlanningProfileParameterNames.NightStart] = "22:00";
        draft.Parameters[PlanningProfileParameterNames.NightEnd] = "06:00";
        draft.Parameters[PlanningProfileParameterNames.NightRate] = "1.25";
        draft.Parameters[PlanningProfileParameterNames.HolidayRate] = "2";
        draft.Parameters[PlanningProfileParameterNames.VacationDaysPerYear] = "25";
        _store.Set(UserId, Key, draft);

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result!.CreatedRuleCount.ShouldBe(1);
        var rule = _added[0];
        rule.DefaultWorkingHours.ShouldBe(8m);
        rule.FullTimeHours.ShouldBe(40m);
        rule.MaxDailyHours.ShouldBe(10m);
        rule.MaxWeeklyHours.ShouldBe(45m);
        rule.MaxConsecutiveDays.ShouldBe(6);
        rule.MinRestDays.ShouldBe(2);
        rule.MinPauseHours.ShouldBe(11m);
        rule.OvertimeThreshold.ShouldBe(42m);
        rule.OvertimeBasis.ShouldBe(OvertimeBasis.Week);
        rule.NightStart.ShouldBe("22:00");
        rule.NightEnd.ShouldBe("06:00");
        rule.NightRate.ShouldBe(1.25m);
        rule.HolidayRate.ShouldBe(2m);
        rule.VacationDaysPerYear.ShouldBe(25);
        result.AppliedOverrides.Count.ShouldBe(14);
    }

    [Test]
    public async Task Handle_Industry_NoTemplates_CreatesZeroRules_SetsCustom_DoesNotThrow()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = IndustrySlugs.Security;
        _store.Set(UserId, Key, draft);

        _schedulingRules.GetByIndustryAsync(IndustrySlugs.Security).Returns(new List<SchedulingRule>());

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result!.CreatedRuleCount.ShouldBe(0);
        _added.ShouldBeEmpty();
        result.ActiveIndustries.ShouldBe(IndustrySlugs.Custom);
        await _settings.Received(1).UpsertSettingAsync(SettingKeys.ActiveIndustries, IndustrySlugs.Custom);
        _store.Get(UserId, Key).ShouldBeNull();
    }
}

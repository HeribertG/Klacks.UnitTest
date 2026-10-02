// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Behavioural proof behind PreviewApplySkillCalls: every catalogued skill is run with apply=false, with
/// apply absent and with apply as a JsonElement false (the real tool-call shape), and must succeed on its
/// preview path while writing nothing. Skills that delegate to a command handler are wired to the real
/// handler through the mediator substitute, so the handler's Apply=false path is proven as well; every
/// request the mediator saw must be a query or a command carrying Apply=false. Repository substitutes may
/// only receive read calls (Get/List/Count/Exists/Search); the unit of work, the self-API client, the job
/// queue, the plan applier and the snapshot store must receive no call at all. apply_grouping_plan records
/// its in-memory preview (the only permitted side effect) and never forgets it. A control test shows the
/// same checks flag an apply=true run, and a coverage test pins that every catalogued name has a proof here
/// whose skill class really carries that name.
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Api.Application.Commands.Grouping;
using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Grouping;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Handlers.Orders;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Attributes;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Geo;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PreviewApplySkillCallsNoWriteTests
{
    private const string FillGroupByCriteria = "fill_group_by_criteria";
    private const string PartitionClientsByAddress = "partition_clients_by_address";
    private const string PartitionClientsByQualification = "partition_clients_by_qualification";
    private const string BulkAddShiftsToGroup = "bulk_add_shifts_to_group";
    private const string BulkAddAbsenceForGroup = "bulk_add_absence_for_group";
    private const string AddSelectedClientsToGroup = "add_selected_clients_to_group";
    private const string GroupUngroupedByCityName = "group_ungrouped_by_city_name";
    private const string AssignOrdersToGroups = "assign_orders_to_groups";
    private const string AssignShiftsToCityGroups = "assign_shifts_to_city_groups";
    private const string SealOpenOrders = "seal_open_orders";
    private const string ScheduleRecurringTask = "schedule_recurring_task";

    private const string ApplyAbsent = "absent";
    private const string ApplyFalse = "false";
    private const string ApplyJsonFalse = "json-false";

    private const string NothingChangedMarker = "Nothing was changed yet";
    private const string NothingScheduledMarker = "Nothing was scheduled yet";
    private const string QueryTypeMarker = "Query";
    private const string ApplyPropertyName = "Apply";
    private const string UserName = "tester";
    private const string CityName = "Bern";
    private const string StateCode = "BE";
    private const string CountryCode = "CH";
    private const string AbsenceTypeName = "Schulung";
    private const string AbsenceDate = "2026-08-01";
    private const string ShiftSearchTerm = "Nacht";
    private const string ShiftName = "Nachtwache A";
    private const string OrderName = "ERP order";
    private const string OrderAbbreviation = "FS";
    private const string CustomerName = "Muster AG";
    private const string TaskName = "weekly check";
    private const string TaskCron = "0 8 * * 1";
    private const string ExistingTaskCron = "0 6 * * 1";
    private const string ExistingTaskMessage = "old text";
    private const string TaskPauseReason = "irreversible skill without the opt-in";
    private const string ReminderText = "check coverage";
    private const string ReminderActionType = "reminder";
    private const string ApplyValidFrom = "2026-10-01";
    private const string PlanReportFingerprint = "r";
    private const char FingerprintChar = 'b';
    private const int FingerprintLength = 64;
    private const int ExistingTaskRunCount = 4;
    private const int LeafLeft = 1;
    private const int LeafRight = 2;

    private static readonly string[] ReadMethodPrefixes = ["Get", "List", "Count", "Exists", "Search", "get_"];

    private static readonly DateTime Today = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();
    private static readonly string PlanFingerprint = new(FingerprintChar, FingerprintLength);

    private static readonly IReadOnlyDictionary<string, Type> CoveredSkills =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            [GroupingSkillNames.Apply] = typeof(ApplyGroupingPlanSkill),
            [FillGroupByCriteria] = typeof(FillGroupByCriteriaSkill),
            [PartitionClientsByAddress] = typeof(PartitionClientsByAddressSkill),
            [PartitionClientsByQualification] = typeof(PartitionClientsByQualificationSkill),
            [BulkAddShiftsToGroup] = typeof(BulkAddShiftsToGroupSkill),
            [BulkAddAbsenceForGroup] = typeof(BulkAddAbsenceForGroupSkill),
            [AddSelectedClientsToGroup] = typeof(AddSelectedClientsToGroupSkill),
            [GroupUngroupedByCityName] = typeof(GroupUngroupedByCityNameSkill),
            [AssignOrdersToGroups] = typeof(AssignOrdersToGroupsSkill),
            [AssignShiftsToCityGroups] = typeof(AssignShiftsToCityGroupsSkill),
            [SealOpenOrders] = typeof(SealOpenOrdersSkill),
            [ScheduleRecurringTask] = typeof(ScheduleRecurringTaskSkill),
        };

    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IShiftRepository _shiftRepository = null!;
    private IAddressRepository _addressRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.List().Returns(new List<Group> { new() { Id = GroupId, Name = CityName } });
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((GroupItem?)null);
        _groupItemRepository.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
        _clientRepository = Substitute.For<IClientRepository>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _addressRepository = Substitute.For<IAddressRepository>();
        _addressRepository.GetCityCentroidsAsync(Arg.Any<CancellationToken>()).Returns(new List<CityCentroid>());
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<PartitionApplyOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<PartitionApplyOutcome>>>()());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<QualificationApplyOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<QualificationApplyOutcome>>>()());
        _mediator = Substitute.For<IMediator>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
    }

    [Test]
    public void EveryCataloguedSkillHasAPreviewProof()
    {
        var covered = new HashSet<string>(CoveredSkills.Keys, StringComparer.OrdinalIgnoreCase);

        PreviewApplySkillCalls.Skills.Where(name => !covered.Contains(name)).ShouldBeEmpty();
        covered.Where(name => !PreviewApplySkillCalls.Skills.Contains(name)).ShouldBeEmpty();
    }

    [Test]
    public void EveryCoveredNameIsTheNameOfTheTestedSkillClass()
    {
        foreach (var (name, skillType) in CoveredSkills)
        {
            var attribute = skillType.GetCustomAttribute<SkillImplementationAttribute>();
            attribute.ShouldNotBeNull(skillType.Name);
            attribute.SkillName.ShouldBe(name, skillType.Name);
        }
    }

    [Test]
    public async Task ApplyGroupingPlan_PreviewWritesNothing([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var analyzer = Substitute.For<IGroupingFeasibilityAnalyzer>();
        analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => GroupingPlan(call.Arg<GroupingAnalysisRequest>()));
        var applier = Substitute.For<IGroupingPlanApplier>();
        var snapshotStore = Substitute.For<IGroupingFeasibilityDailySnapshotStore>();
        var previews = Substitute.For<IGroupingPlanPreviewRegistry>();
        var skill = new ApplyGroupingPlanSkill(
            analyzer, applier, _groupRepository, TestGroupScopeGuard.Unrestricted(), _companyClock, snapshotStore,
            previews, NullLogger<ApplyGroupingPlanSkill>.Instance);
        var parameters = WithApply(applyShape, new Dictionary<string, object>
        {
            [ApplyGroupingPlanSkill.FingerprintParameter] = PlanFingerprint[..GroupingFeasibilityDefaults.FingerprintDisplayLength],
        });

        var result = await RunPreviewAsync(GroupingSkillNames.Apply, skill, Ctx(), parameters);

        result.Message.ShouldContain(NothingChangedMarker);
        previews.Received(1).RecordPreview(Arg.Any<Guid>(), Arg.Is(PlanFingerprint), Arg.Any<Guid?>());
        previews.DidNotReceiveWithAnyArgs().Forget(default, default!);
        ShouldNotHaveWritten(null, [_groupRepository], applier, snapshotStore);
    }

    [Test]
    public async Task FillGroupByCriteria_PreviewWritesNothing_InSkillAndHandler([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var contractRepository = Substitute.For<IContractRepository>();
        var searchRepository = Substitute.For<IClientSearchRepository>();
        searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<DateOnly?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem> { new() { Id = ClientId, FirstName = UserName, LastName = CustomerName } },
                TotalCount = 1
            });
        var handler = new FillGroupByCriteriaCommandHandler(
            searchRepository, _groupItemRepository, _unitOfWork, _companyClock, TestGroupWriteVisibility.UnrestrictedGroups());
        _mediator.Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<FillGroupByCriteriaCommand>(), ci.Arg<CancellationToken>()));
        var skill = new FillGroupByCriteriaSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), contractRepository, _mediator, _companyClock);

        var result = await RunPreviewAsync(FillGroupByCriteria, skill, Ctx(), FillParameters(applyShape));

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<FillGroupByCriteriaResult>();
        data.Applied.ShouldBeFalse();
        data.TotalMatchCount.ShouldBe(1);
        await _mediator.Received(1).Send(Arg.Is<FillGroupByCriteriaCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(_mediator, [_groupRepository, contractRepository, searchRepository, _groupItemRepository], _unitOfWork);
    }

    [Test]
    public async Task PartitionClientsByAddress_PreviewWritesNothing_InSkillAndHandler([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { EmployeeLivingInCity() });
        var regionProvider = Substitute.For<ICountryRegionProvider>();
        regionProvider.GetRegionByStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var countryResolver = Substitute.For<ICountryResolver>();
        var stateRepository = Substitute.For<IStateRepository>();
        stateRepository.List().Returns(new List<State>());
        var settingsReader = Substitute.For<ISettingsReader>();
        var visibilityPreservation = Substitute.For<IGroupVisibilityPreservationService>();
        var handler = new PartitionClientsByAddressCommandHandler(
            _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock,
            regionProvider, countryResolver, stateRepository, settingsReader, visibilityPreservation,
            TestGroupWriteVisibility.UnrestrictedGroups());
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<PartitionClientsByAddressCommand>(), ci.Arg<CancellationToken>()));
        var skill = new PartitionClientsByAddressSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);
        var parameters = WithApply(applyShape, new Dictionary<string, object>
        {
            ["level"] = "state",
            ["entityType"] = nameof(EntityTypeEnum.Employee),
        });

        var result = await RunPreviewAsync(PartitionClientsByAddress, skill, Ctx(), parameters);

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<PartitionClientsByAddressResult>();
        data.Applied.ShouldBeFalse();
        data.Groups.ShouldNotBeEmpty();
        await _mediator.Received(1).Send(Arg.Is<PartitionClientsByAddressCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(
            _mediator,
            [_clientRepository, _groupRepository, _groupItemRepository, regionProvider, countryResolver, stateRepository,
                settingsReader, visibilityPreservation],
            _unitOfWork);
    }

    [Test]
    public async Task PartitionClientsByQualification_PreviewWritesNothing_InSkillAndHandler([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var qualification = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = CityName } };
        var holder = EmployeeLivingInCity();
        holder.Qualifications.Add(new ClientQualification { Id = Guid.NewGuid(), ClientId = holder.Id, QualificationId = qualification.Id });
        _clientRepository.GetByTypeWithQualificationsAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { holder });
        var qualificationRepository = Substitute.For<IQualificationRepository>();
        qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Qualification> { qualification });
        var languageResolver = Substitute.For<IInstallationLanguageResolver>();
        languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns("de");
        var visibilityPreservation = Substitute.For<IGroupVisibilityPreservationService>();
        var groupVisibilityRepository = Substitute.For<IGroupVisibilityRepository>();
        var handler = new PartitionClientsByQualificationCommandHandler(
            _clientRepository, qualificationRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock,
            languageResolver, groupVisibilityRepository, visibilityPreservation, TestGroupWriteVisibility.UnrestrictedGroups());
        _mediator.Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<PartitionClientsByQualificationCommand>(), ci.Arg<CancellationToken>()));
        var skill = new PartitionClientsByQualificationSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);
        var parameters = WithApply(applyShape, new Dictionary<string, object> { ["minMembers"] = 1 });

        var result = await RunPreviewAsync(PartitionClientsByQualification, skill, Ctx(), parameters);

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<PartitionClientsByQualificationResult>();
        data.Applied.ShouldBeFalse();
        data.Groups.ShouldNotBeEmpty();
        await _mediator.Received(1).Send(Arg.Is<PartitionClientsByQualificationCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(
            _mediator,
            [_clientRepository, _groupRepository, _groupItemRepository, qualificationRepository, visibilityPreservation, groupVisibilityRepository],
            _unitOfWork);
    }

    [Test]
    public async Task BulkAddShiftsToGroup_PreviewWritesNothing([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var selfApi = Substitute.For<IKlacksSelfApiClient>();
        var routes = Substitute.For<ISelfApiRouteResolver>();
        _mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TruncatedShiftResource
            {
                Shifts = new List<ShiftResource> { new() { Id = ShiftId, Name = ShiftName } }
            });
        var skill = new BulkAddShiftsToGroupSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _groupItemRepository, selfApi, routes, _mediator);
        var parameters = WithApply(applyShape, new Dictionary<string, object>
        {
            ["groupName"] = CityName,
            ["searchTerm"] = ShiftSearchTerm,
        });

        var result = await RunPreviewAsync(BulkAddShiftsToGroup, skill, Ctx(), parameters);

        result.Message.ShouldContain(NothingChangedMarker);
        result.Message.ShouldContain(ShiftName);
        ShouldNotHaveWritten(_mediator, [_groupRepository, _groupItemRepository], selfApi, routes);
    }

    [Test]
    public async Task BulkAddAbsenceForGroup_PreviewWritesNothing([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var absenceRepository = Substitute.For<IAbsenceRepository>();
        absenceRepository.List().Returns(new List<Absence>
        {
            new() { Id = AbsenceId, Name = new MultiLanguage { De = AbsenceTypeName } }
        });
        var memberService = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        memberService.GetAllClientIdsFromGroupAndSubgroups(GroupId).Returns(new List<Guid> { ClientId });
        var breakRepository = Substitute.For<IBreakRepository>();
        breakRepository.GetClientIdsWithBreakOnDate(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        var skill = new BulkAddAbsenceForGroupSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), absenceRepository, memberService, breakRepository, _mediator);
        var parameters = WithApply(applyShape, new Dictionary<string, object>
        {
            ["groupName"] = CityName,
            ["absenceType"] = AbsenceTypeName,
            ["date"] = AbsenceDate,
        });

        var result = await RunPreviewAsync(BulkAddAbsenceForGroup, skill, Ctx(), parameters);

        result.Message.ShouldContain(NothingChangedMarker);
        ShouldNotHaveWritten(null, [_groupRepository, absenceRepository, memberService, breakRepository], _mediator);
    }

    [Test]
    public async Task AddSelectedClientsToGroup_PreviewWritesNothing_InSkillAndHandler([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { EmployeeLivingInCity() });
        var handler = new AddSelectedClientsToGroupCommandHandler(
            _clientRepository, _groupItemRepository, _unitOfWork, _companyClock, TestGroupWriteVisibility.UnrestrictedGroups(), TestGroupWriteVisibility.AllClientsVisible());
        _mediator.Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<AddSelectedClientsToGroupCommand>(), ci.Arg<CancellationToken>()));
        var skill = new AddSelectedClientsToGroupSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);
        var parameters = WithApply(applyShape, new Dictionary<string, object> { ["groupName"] = CityName });

        var result = await RunPreviewAsync(AddSelectedClientsToGroup, skill, Ctx([ClientId]), parameters);

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<AddSelectedClientsToGroupResult>();
        data.Applied.ShouldBeFalse();
        data.EligibleCount.ShouldBe(1);
        await _mediator.Received(1).Send(Arg.Is<AddSelectedClientsToGroupCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(_mediator, [_groupRepository, _clientRepository, _groupItemRepository], _unitOfWork);
    }

    [Test]
    public async Task GroupUngroupedByCityName_PreviewWritesNothing_InSkillAndHandler([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { EmployeeLivingInCity() });
        var handler = new GroupUngroupedByCityNameCommandHandler(
            _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock, TestGroupWriteVisibility.UnrestrictedGroups());
        _mediator.Send(Arg.Any<GroupUngroupedByCityNameCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<GroupUngroupedByCityNameCommand>(), ci.Arg<CancellationToken>()));
        var skill = new GroupUngroupedByCityNameSkill(_mediator, TestGroupScopeGuard.Unrestricted(), _companyClock);

        var result = await RunPreviewAsync(GroupUngroupedByCityName, skill, Ctx(), WithApply(applyShape, new Dictionary<string, object>()));

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<GroupUngroupedByCityNameResult>();
        data.Applied.ShouldBeFalse();
        data.MatchCount.ShouldBe(1);
        await _mediator.Received(1).Send(Arg.Is<GroupUngroupedByCityNameCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(_mediator, [_clientRepository, _groupRepository, _groupItemRepository], _unitOfWork);
    }

    [Test]
    public async Task AssignOrdersToGroups_PreviewWritesNothing_InSkillAndHandler([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var order = UngroupedOrderOfCustomerInCity();
        OpenOrders(order);
        RouteAssignOrdersToHandler();
        var skill = new AssignOrdersToGroupsSkill(TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

        var result = await RunPreviewAsync(AssignOrdersToGroups, skill, Ctx(), WithApply(applyShape, new Dictionary<string, object>()));

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<AssignOrdersToGroupsResult>();
        data.Applied.ShouldBeFalse();
        data.AssignedCount.ShouldBe(1);
        order.GroupItems.ShouldBeEmpty();
        await _mediator.Received(1).Send(Arg.Is<AssignOrdersToGroupsCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(_mediator, [_shiftRepository, _groupRepository, _groupItemRepository], _unitOfWork);
    }

    [Test]
    public async Task AssignShiftsToCityGroups_PreviewWritesNothing_InSkillAndHandler([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var cityGroup = new Group { Id = GroupId, Name = CityName, Lft = LeafLeft, Rgt = LeafRight };
        _groupRepository.List().Returns(new List<Group> { cityGroup });
        var regionLink = new GroupItem { Id = Guid.NewGuid(), GroupId = Guid.NewGuid() };
        var shift = UngroupedOrderOfCustomerInCity();
        shift.GroupItems = new List<GroupItem> { regionLink };
        _shiftRepository.GetShiftsForCityGroupPlacementAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { shift });
        RouteAssignShiftsToHandler();
        var skill = new AssignShiftsToCityGroupsSkill(TestGroupScopeGuard.Unrestricted(), _mediator);

        var result = await RunPreviewAsync(AssignShiftsToCityGroups, skill, Ctx(), WithApply(applyShape, new Dictionary<string, object>()));

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<AssignShiftsToCityGroupsResult>();
        data.Applied.ShouldBeFalse();
        data.AssignedCount.ShouldBe(1);
        data.VerifiedCount.ShouldBe(0);
        shift.GroupItems.ShouldHaveSingleItem().ShouldBeSameAs(regionLink);
        await _mediator.Received(1).Send(Arg.Is<AssignShiftsToCityGroupsCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(_mediator, [_shiftRepository, _groupRepository, _addressRepository, _groupItemRepository], _unitOfWork);
    }

    [Test]
    public async Task SealOpenOrders_PreviewWithAutoAssignWritesNothing_InSkillAndBothHandlers([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var sealable = SealableOrder();
        var ungrouped = UngroupedOrderOfCustomerInCity();
        OpenOrders(sealable, ungrouped);
        RouteAssignOrdersToHandler();
        var sealHandler = new SealOpenOrdersCommandHandler(
            _shiftRepository, new OrderSealingService(_shiftRepository, _unitOfWork), _mediator);
        _mediator.Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => sealHandler.Handle(ci.Arg<SealOpenOrdersCommand>(), ci.Arg<CancellationToken>()));
        var jobQueue = Substitute.For<ISealOpenOrdersJobQueue>();
        var skill = new SealOpenOrdersSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock, jobQueue);
        var parameters = WithApply(applyShape, new Dictionary<string, object> { ["autoAssignGroups"] = true });

        var result = await RunPreviewAsync(SealOpenOrders, skill, Ctx(), parameters);

        result.Message.ShouldContain(NothingChangedMarker);
        var data = result.Data.ShouldBeOfType<SealOpenOrdersResult>();
        data.Applied.ShouldBeFalse();
        data.SealableCount.ShouldBe(1);
        data.SealedCount.ShouldBe(0);
        data.AutoAssignedCount.ShouldBe(1);
        sealable.Status.ShouldBe(ShiftStatus.OriginalOrder);
        ungrouped.Status.ShouldBe(ShiftStatus.OriginalOrder);
        ungrouped.GroupItems.ShouldBeEmpty();
        await _mediator.Received(1).Send(Arg.Is<SealOpenOrdersCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(Arg.Is<AssignOrdersToGroupsCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        ShouldNotHaveWritten(_mediator, [_shiftRepository, _groupRepository, _groupItemRepository], _unitOfWork, jobQueue);
    }

    [Test]
    public async Task ScheduleRecurringTask_PreviewOfAPausedExistingTaskWritesNothing([Values(ApplyAbsent, ApplyFalse, ApplyJsonFalse)] string applyShape)
    {
        var repository = Substitute.For<IScheduledTaskRepository>();
        var skillRegistry = Substitute.For<ISkillRegistry>();
        var riskClassifier = Substitute.For<ISkillRiskClassifier>();
        var skill = new ScheduleRecurringTaskSkill(
            repository, skillRegistry, riskClassifier,
            new EffectiveTimeZoneResolver(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc)));
        var context = Ctx();
        var existing = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            Name = TaskName,
            CronExpression = ExistingTaskCron,
            TimeZoneId = TimeZoneInfo.Utc.Id,
            ActionType = ScheduledTaskActionTypes.Reminder,
            MessageText = ExistingTaskMessage,
            OwnerUserId = context.UserId,
            OwnerUserName = UserName,
            IsEnabled = true,
            RunCount = ExistingTaskRunCount
        };
        existing.Pause(TaskPauseReason);
        repository.GetByOwnerAndNameAsync(context.UserId, TaskName, Arg.Any<CancellationToken>()).Returns(existing);
        var parameters = WithApply(applyShape, new Dictionary<string, object>
        {
            ["name"] = TaskName,
            ["cronExpression"] = TaskCron,
            ["actionType"] = ReminderActionType,
            ["messageText"] = ReminderText,
        });

        var result = await RunPreviewAsync(ScheduleRecurringTask, skill, context, parameters);

        result.Message.ShouldContain(NothingScheduledMarker);
        existing.IsPaused.ShouldBeTrue();
        existing.PausedReason.ShouldBe(TaskPauseReason);
        existing.CronExpression.ShouldBe(ExistingTaskCron);
        existing.MessageText.ShouldBe(ExistingTaskMessage);
        existing.RunCount.ShouldBe(ExistingTaskRunCount);
        ShouldNotHaveWritten(null, [repository]);
    }

    [Test]
    public async Task Control_AnApplyRunIsFlaggedAsWrite()
    {
        var searchRepository = Substitute.For<IClientSearchRepository>();
        searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<DateOnly?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem> { new() { Id = ClientId, FirstName = UserName, LastName = CustomerName } },
                TotalCount = 1
            });
        var handler = new FillGroupByCriteriaCommandHandler(
            searchRepository, _groupItemRepository, _unitOfWork, _companyClock, TestGroupWriteVisibility.UnrestrictedGroups());
        _mediator.Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<FillGroupByCriteriaCommand>(), ci.Arg<CancellationToken>()));
        var skill = new FillGroupByCriteriaSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), Substitute.For<IContractRepository>(), _mediator, _companyClock);
        var parameters = FillParameters(ApplyAbsent);
        parameters[PreviewApplySkillCalls.ApplyParameter] = true;
        parameters["validFrom"] = ApplyValidFrom;

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        PreviewApplySkillCalls.IsPreviewCall(FillGroupByCriteria, parameters).ShouldBeFalse();
        WriteCalls([_groupItemRepository]).ShouldNotBeEmpty();
        AnyCalls([_unitOfWork]).ShouldNotBeEmpty();
        NonPreviewRequests(_mediator).ShouldNotBeEmpty();
    }

    private static async Task<SkillResult> RunPreviewAsync(
        string skillName, BaseSkillImplementation skill, SkillExecutionContext context, Dictionary<string, object> parameters)
    {
        PreviewApplySkillCalls.IsPreviewCall(skillName, parameters).ShouldBeTrue();

        var result = await skill.ExecuteAsync(context, parameters);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldNotBeNull();
        return result;
    }

    private static Dictionary<string, object> WithApply(string applyShape, Dictionary<string, object> parameters)
    {
        if (applyShape == ApplyFalse)
        {
            parameters[PreviewApplySkillCalls.ApplyParameter] = false;
        }
        else if (applyShape == ApplyJsonFalse)
        {
            parameters[PreviewApplySkillCalls.ApplyParameter] = JsonSerializer.SerializeToElement(false);
        }

        return parameters;
    }

    private static Dictionary<string, object> FillParameters(string applyShape) =>
        WithApply(applyShape, new Dictionary<string, object>
        {
            ["groupName"] = CityName,
            ["city"] = CityName,
        });

    private static SkillExecutionContext Ctx(IReadOnlyList<Guid>? selection = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = UserName,
        UserPermissions = new List<string> { Roles.Admin },
        SelectedEntityIds = selection
    };

    private static void ShouldNotHaveWritten(IMediator? mediator, object[] readOnlySubstitutes, params object[] untouchedSubstitutes)
    {
        var violations = WriteCalls(readOnlySubstitutes).Concat(AnyCalls(untouchedSubstitutes)).ToList();
        if (mediator != null)
        {
            violations.AddRange(NonPreviewRequests(mediator));
        }

        violations.ShouldBeEmpty();
    }

    private static List<string> WriteCalls(object[] readOnlySubstitutes) =>
        readOnlySubstitutes
            .SelectMany(substitute => substitute.ReceivedCalls())
            .Select(call => call.GetMethodInfo())
            .Where(method => !ReadMethodPrefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToList();

    private static List<string> AnyCalls(object[] untouchedSubstitutes) =>
        untouchedSubstitutes
            .SelectMany(substitute => substitute.ReceivedCalls())
            .Select(call => call.GetMethodInfo())
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToList();

    private static List<string> NonPreviewRequests(IMediator mediator) =>
        mediator.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .Where(request => request is null || !IsQueryOrPreviewCommand(request))
            .Select(request => request?.GetType().Name ?? nameof(IMediator.Send))
            .ToList();

    private static bool IsQueryOrPreviewCommand(object request)
    {
        var type = request.GetType();
        if (type.Name.Contains(QueryTypeMarker, StringComparison.Ordinal))
        {
            return true;
        }

        return type.GetProperty(ApplyPropertyName)?.GetValue(request) is false;
    }

    private void RouteAssignOrdersToHandler()
    {
        var handler = new AssignOrdersToGroupsCommandHandler(
            _shiftRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock);
        _mediator.Send(Arg.Any<AssignOrdersToGroupsCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<AssignOrdersToGroupsCommand>(), ci.Arg<CancellationToken>()));
    }

    private void RouteAssignShiftsToHandler()
    {
        var handler = new AssignShiftsToCityGroupsCommandHandler(
            _shiftRepository, _groupRepository, _addressRepository, _groupItemRepository, _unitOfWork, _companyClock);
        _mediator.Send(Arg.Any<AssignShiftsToCityGroupsCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.Arg<AssignShiftsToCityGroupsCommand>(), ci.Arg<CancellationToken>()));
    }

    private void OpenOrders(params Shift[] orders) =>
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(orders.ToList());

    private static Client EmployeeLivingInCity() => new()
    {
        Id = ClientId,
        FirstName = UserName,
        Name = CustomerName,
        Type = EntityTypeEnum.Employee,
        Addresses = new List<Address>
        {
            new()
            {
                ClientId = ClientId, Type = AddressTypeEnum.Employee, State = StateCode, City = CityName,
                Country = CountryCode, ValidFrom = Today
            }
        }
    };

    private static Shift SealableOrder() => new()
    {
        Id = Guid.NewGuid(),
        Status = ShiftStatus.OriginalOrder,
        Name = OrderName,
        Abbreviation = OrderAbbreviation,
        FromDate = DateOnly.FromDateTime(Today),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1,
        GroupItems = new List<GroupItem> { new() { Id = Guid.NewGuid(), GroupId = GroupId } }
    };

    private static Shift UngroupedOrderOfCustomerInCity()
    {
        var customerId = Guid.NewGuid();
        var customer = new Client
        {
            Id = customerId,
            Name = CustomerName,
            Company = CustomerName,
            Type = EntityTypeEnum.Customer,
            Addresses = new List<Address>
            {
                new()
                {
                    ClientId = customerId, Type = AddressTypeEnum.Employee, City = CityName, State = StateCode,
                    ValidFrom = Today
                }
            }
        };

        var order = SealableOrder();
        order.GroupItems = new List<GroupItem>();
        order.ClientId = customerId;
        order.Client = customer;
        return order;
    }

    private static GroupingFeasibilityReport GroupingPlan(GroupingAnalysisRequest request) =>
        new(
            request,
            [],
            [new GroupingProposal(GroupingProposalKind.AddClient, GroupId, null, ClientId, null, GroupingFindingCode.ShiftUncoveredInGroup)],
            PlanFingerprint,
            PlanReportFingerprint,
            new Dictionary<Guid, string> { [GroupId] = CityName },
            new Dictionary<Guid, IReadOnlyList<Guid>> { [GroupId] = [GroupId] },
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            new Dictionary<Guid, string> { [ClientId] = CustomerName },
            new Dictionary<Guid, string> { [ShiftId] = ShiftName },
            1,
            1);
}

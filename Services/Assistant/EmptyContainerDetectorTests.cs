// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmptyContainerDetector -- covers the empty-database case, the positive
/// no-template-at-all case, the negative case with at least one template (which proves
/// empty_container is disjoint from unstaffed_shift rather than a subset of it), the
/// non-container (Task) exclusion, scenario-copy exclusion, soft-delete exclusion on both
/// Shift and ContainerTemplate, dedup-key stability, the active-period severity rule, the
/// MaxFindingsPerTick emission cap, and the rotation that decides which candidates fill that
/// cap -- never-opened candidates first, then the open ledger rows least recently observed.
/// Uses a real EF Core InMemory DataBaseContext with the real ShiftRepository and
/// ContainerTemplateRepository (as ContainerAvailableTasksServiceTests.cs does) because the
/// detector composes IQueryable via GetQuery() and awaits ToListAsync(), which a plain
/// NSubstitute-mocked IQueryable cannot support.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class EmptyContainerDetectorTests
{
    private static readonly DateOnly BulkFromDate = new(2025, 1, 1);

    private static readonly DateTime FirstTickInstant = new(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc);

    private const int RotationBacklogOverflow = 10;

    private DataBaseContext _context = null!;
    private ShiftRepository _shiftRepository = null!;
    private ContainerTemplateRepository _containerTemplateRepository = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private AgentConditionRepository _agentConditionRepository = null!;
    private EmptyContainerDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        var shiftLogger = Substitute.For<ILogger<Shift>>();
        var containerTemplateLogger = Substitute.For<ILogger<ContainerTemplate>>();
        var detectorLogger = Substitute.For<ILogger<EmptyContainerDetector>>();

        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var shiftValidator = Substitute.For<IShiftValidator>();
        var queryPipeline = Substitute.For<IShiftQueryPipelineService>();
        var groupManagementService = Substitute.For<IShiftGroupManagementService>();
        var scheduleMapper = new ScheduleMapper();

        _shiftRepository = new ShiftRepository(
            _context,
            shiftLogger,
            queryPipeline,
            groupManagementService,
            collectionUpdateService,
            shiftValidator,
            scheduleMapper,
            new FixedCompanyClock(DateTimeOffset.UtcNow));

        var containerTemplateServiceLogger = Substitute.For<ILogger<ContainerTemplateService>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var containerTemplateService = new ContainerTemplateService(unitOfWork, containerTemplateServiceLogger);

        _containerTemplateRepository = new ContainerTemplateRepository(
            _context,
            containerTemplateLogger,
            collectionUpdateService,
            containerTemplateService);

        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _agentConditionRepository = new AgentConditionRepository(_context);

        _sut = new EmptyContainerDetector(
            _shiftRepository, _containerTemplateRepository, _groupScopeReader, _agentConditionRepository,
            new FixedCompanyClock(DateTimeOffset.UtcNow), detectorLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    private static Shift MakeContainer(
        DateOnly fromDate,
        DateOnly? untilDate = null,
        Guid? analyseToken = null,
        Guid? scenarioSourceShiftId = null,
        bool isDeleted = false,
        ShiftStatus status = ShiftStatus.OriginalShift,
        ShiftType shiftType = ShiftType.IsContainer) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Container",
        Abbreviation = "CNT",
        ShiftType = shiftType,
        Status = status,
        FromDate = fromDate,
        UntilDate = untilDate,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        AnalyseToken = analyseToken,
        ScenarioSourceShiftId = scenarioSourceShiftId,
        IsDeleted = isDeleted
    };

    private static ContainerTemplate MakeTemplate(Guid containerId, bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = containerId,
        FromTime = new TimeOnly(8, 0),
        UntilTime = new TimeOnly(16, 0),
        Weekday = 1,
        IsDeleted = isDeleted
    };

    /// <summary>
    /// Gives the containers an open ledger row for empty_container observed at
    /// <paramref name="lastSeenAtUtc"/>, or moves an existing row's observation time forward -- the two
    /// halves of what AgentConditionLedgerService.UpsertDetectedAsync does after a real tick. The
    /// observation time is explicit because it is the rotation's sort key: rows seen longer ago come back
    /// first.
    /// </summary>
    private async Task ObserveInLedgerAsync(DateTime lastSeenAtUtc, params Guid[] containerIds)
    {
        foreach (var containerId in containerIds)
        {
            var existing = await _context.AgentConditions
                .FirstOrDefaultAsync(condition => condition.EntityId == containerId);

            if (existing != null)
            {
                existing.LastSeenAtUtc = lastSeenAtUtc;
                continue;
            }

            await _context.AgentConditions.AddAsync(new AgentCondition
            {
                TriggerKind = AgentTriggerKinds.EmptyContainer,
                Fingerprint = $"empty_container:{containerId}:{Guid.NewGuid()}",
                EntityId = containerId,
                Severity = AgentTriggerSeverity.Medium,
                Status = AgentConditionStatus.Reported,
                DetectedAtUtc = lastSeenAtUtc,
                LastSeenAtUtc = lastSeenAtUtc
            });
        }

        await _context.SaveChangesAsync();
    }

    private async Task<List<Guid>> SeedBulkBacklogAsync(int size)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backlog = Enumerable.Range(0, size)
            .Select(_ => MakeContainer(BulkFromDate, today.AddDays(365)))
            .ToList();
        await _context.Shift.AddRangeAsync(backlog);
        await _context.SaveChangesAsync();

        return backlog.Select(container => container.Id).ToList();
    }

    private async Task<List<Guid>> ReportedShiftIdsAsync() =>
        (await _sut.DetectAsync()).Cast<EmptyContainerTriggerEvent>().Select(e => e.ShiftId).ToList();

    [Test]
    public async Task DetectAsync_EmptyDatabase_ReturnsEmpty()
    {
        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ContainerWithoutAnyTemplate_EmitsEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var emptyContainerEvent = events.Single() as EmptyContainerTriggerEvent;
        Assert.That(emptyContainerEvent, Is.Not.Null);
        Assert.That(emptyContainerEvent!.ShiftId, Is.EqualTo(container.Id));
        Assert.That(emptyContainerEvent.Kind, Is.EqualTo(AgentTriggerKinds.EmptyContainer));
    }

    [Test]
    public async Task DetectAsync_ContainerWithAtLeastOneTemplate_ReturnsEmpty_DisjointFromUnstaffedShift()
    {
        // empty_container reports MISSING slot DEFINITIONS on the container itself. A container
        // that already has at least one ContainerTemplate is, at most, an unstaffed_shift concern
        // (missing employees on an existing slot) -- the two trigger kinds are disjoint, never a
        // subset of one another, so this container must NOT raise empty_container.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.ContainerTemplate.AddAsync(MakeTemplate(container.Id));
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_TaskShiftInsteadOfContainer_ReturnsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var task = MakeContainer(today.AddDays(-1), today.AddDays(30), shiftType: ShiftType.IsTask);
        await _context.Shift.AddAsync(task);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_NonOriginalShiftStatus_ReturnsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sealedOrder = MakeContainer(today.AddDays(-1), today.AddDays(30), status: ShiftStatus.SealedOrder);
        await _context.Shift.AddAsync(sealedOrder);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioClone_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scenarioContainer = MakeContainer(today.AddDays(-1), today.AddDays(30), analyseToken: Guid.NewGuid());
        await _context.Shift.AddAsync(scenarioContainer);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioSourceCopy_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scenarioSourceCopy = MakeContainer(today.AddDays(-1), today.AddDays(30), scenarioSourceShiftId: Guid.NewGuid());
        await _context.Shift.AddAsync(scenarioSourceCopy);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_SoftDeletedShift_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var deletedContainer = MakeContainer(today.AddDays(-1), today.AddDays(30), isDeleted: true);
        await _context.Shift.AddAsync(deletedContainer);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_OnlySoftDeletedTemplate_StillCountsAsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.ContainerTemplate.AddAsync(MakeTemplate(container.Id, isDeleted: true));
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var emptyContainerEvent = events.Single() as EmptyContainerTriggerEvent;
        Assert.That(emptyContainerEvent!.ShiftId, Is.EqualTo(container.Id));
    }

    [Test]
    public async Task DetectAsync_DedupKey_IsStableAcrossRepeatedScans()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var firstScan = await _sut.DetectAsync();
        var secondScan = await _sut.DetectAsync();

        Assert.That(firstScan.Single().DedupKey, Is.EqualTo(secondScan.Single().DedupKey));
        Assert.That(firstScan.Single().DedupKey, Is.EqualTo(container.Id.ToString()));
    }

    [Test]
    public async Task DetectAsync_PeriodCurrentlyActive_IsHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(1));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_PeriodNotYetActive_IsLowerThanHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(10), today.AddDays(40));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_PeriodAlreadyEnded_IsLowerThanHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-40), today.AddDays(-10));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_MoreEmptyContainersThanCap_StopsAtMaxFindingsPerTick()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var totalContainers = EmptyContainerDetector.MaxFindingsPerTick + RotationBacklogOverflow;
        var containers = Enumerable.Range(0, totalContainers)
            .Select(_ => MakeContainer(today.AddDays(-1), today.AddDays(30)))
            .ToList();
        await _context.Shift.AddRangeAsync(containers);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(EmptyContainerDetector.MaxFindingsPerTick));
    }

    [Test]
    public async Task DetectAsync_MoreEmptyContainersThanCap_SelectsOldestFromDateFirst()
    {
        // Regression test for the starvation bug: without an explicit OrderBy before Take, the cap
        // picks from whatever order the query happens to return (effectively physical storage order),
        // so the same subset is chosen on every tick regardless of which containers are actually
        // oldest -- once dispatched, the other containers are permanently starved because dedup has
        // no TTL. Inserting in an order that contradicts FromDate order proves the selection tracks
        // FromDate, not insertion/storage order.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var totalContainers = EmptyContainerDetector.MaxFindingsPerTick + 5;
        var containers = Enumerable.Range(0, totalContainers)
            .Select(i => MakeContainer(today.AddDays(-(totalContainers - i)), today.AddDays(365)))
            .ToList();
        var insertionOrder = containers.OrderByDescending(c => c.FromDate).ToList();
        await _context.Shift.AddRangeAsync(insertionOrder);
        await _context.SaveChangesAsync();

        var expectedIds = containers
            .OrderBy(c => c.FromDate)
            .Take(EmptyContainerDetector.MaxFindingsPerTick)
            .Select(c => c.Id)
            .ToHashSet();

        // Mark the leftover newest containers as already open, so the rotation sorts them behind the
        // never-opened ones and this test is isolated to the FromDate order inside a single group --
        // the rotation between the groups has its own tests below.
        var leftoverIds = containers.Select(c => c.Id).Except(expectedIds).ToArray();
        await ObserveInLedgerAsync(FirstTickInstant, leftoverIds);

        var events = (await _sut.DetectAsync()).Cast<EmptyContainerTriggerEvent>().ToList();
        var actualIds = events.Select(e => e.ShiftId).ToHashSet();

        Assert.That(actualIds, Is.EqualTo(expectedIds));
    }

    [Test]
    public async Task DetectAsync_BacklogSharesOneFromDateAndLedgerIsEmpty_FirstTickFillsTheCap()
    {
        // Every candidate shares one FromDate -- the shape the reference installation actually has, and
        // the one a plain oldest-first cap degenerates on. With nothing open in the ledger yet, every
        // candidate is in the never-opened group, so the cap alone decides how many are reported.
        var backlogSize = EmptyContainerDetector.MaxFindingsPerTick + RotationBacklogOverflow;
        var backlogIds = await SeedBulkBacklogAsync(backlogSize);

        var reported = await ReportedShiftIdsAsync();

        Assert.That(reported, Has.Count.EqualTo(EmptyContainerDetector.MaxFindingsPerTick));
        Assert.That(reported, Is.SubsetOf(backlogIds));
        Assert.That(reported.Distinct().Count(), Is.EqualTo(reported.Count));
    }

    [Test]
    public async Task DetectAsync_AfterTheFirstTickWasObserved_SecondTickLeadsWithTheNeverReportedRest()
    {
        // What the fixed first slice could never do: once the first tick's findings carry an open ledger
        // row, the rotation puts the candidates that were never opened at the front, and fills the rest of
        // the cap with the longest-unobserved of the already reported ones -- so a recipient who was
        // throttled on tick 1 is offered them again instead of losing them forever.
        var backlogSize = EmptyContainerDetector.MaxFindingsPerTick + RotationBacklogOverflow;
        var backlogIds = await SeedBulkBacklogAsync(backlogSize);

        var firstTick = await ReportedShiftIdsAsync();
        await ObserveInLedgerAsync(FirstTickInstant, firstTick.ToArray());
        var starved = backlogIds.Except(firstTick).ToList();

        var secondTick = await ReportedShiftIdsAsync();

        Assert.That(starved, Has.Count.EqualTo(RotationBacklogOverflow));
        Assert.That(secondTick, Has.Count.EqualTo(EmptyContainerDetector.MaxFindingsPerTick));
        Assert.That(secondTick.Take(RotationBacklogOverflow), Is.EquivalentTo(starved),
            "the candidates no tick has ever opened a ledger row for must lead the second tick");
        Assert.That(secondTick.Skip(RotationBacklogOverflow), Is.SubsetOf(firstTick));
    }

    [Test]
    public async Task DetectAsync_EveryCandidateOpenWithStaggeredLastSeen_ReportsTheLongestUnobservedOnes()
    {
        // No never-opened candidates left at all: the whole selection is then the LastSeenAtUtc order,
        // oldest observation first, which is what makes the backlog cycle instead of stalling.
        var backlogSize = EmptyContainerDetector.MaxFindingsPerTick + RotationBacklogOverflow;
        var backlogIds = await SeedBulkBacklogAsync(backlogSize);

        for (var index = 0; index < backlogIds.Count; index++)
        {
            await ObserveInLedgerAsync(FirstTickInstant.AddMinutes(index), backlogIds[index]);
        }

        var reported = await ReportedShiftIdsAsync();

        Assert.That(reported, Is.EqualTo(backlogIds.Take(EmptyContainerDetector.MaxFindingsPerTick).ToList()));
    }

    [Test]
    public async Task DetectAsync_RepeatedTicksWithLedgerUpdates_OffersEveryCandidateWithinOneFullCycle()
    {
        // The convergence guarantee the fixed cap never had: with LastSeenAtUtc advanced on everything a
        // tick reports -- what AgentConditionLedgerService.UpsertDetectedAsync does after every real tick
        // -- the whole backlog is offered within ceil(candidates / cap) ticks, even though every candidate
        // shares one FromDate and the business order can therefore not separate them.
        var backlogSize = EmptyContainerDetector.MaxFindingsPerTick * 2 - RotationBacklogOverflow;
        var backlogIds = await SeedBulkBacklogAsync(backlogSize);
        var ticksPerCycle = (backlogSize + EmptyContainerDetector.MaxFindingsPerTick - 1)
            / EmptyContainerDetector.MaxFindingsPerTick;

        var seenIds = new HashSet<Guid>();
        for (var tick = 0; tick < ticksPerCycle; tick++)
        {
            var reported = await ReportedShiftIdsAsync();
            seenIds.UnionWith(reported);
            await ObserveInLedgerAsync(FirstTickInstant.AddHours(tick), reported.ToArray());
        }

        Assert.That(seenIds, Is.EquivalentTo(backlogIds),
            "every candidate must be offered within one full rotation cycle, even sharing one FromDate");
    }

    [Test]
    public async Task DetectAsync_FewerCandidatesThanTheCap_ReportsAllOfThemInBusinessOrder()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var containers = Enumerable.Range(0, 5)
            .Select(offset => MakeContainer(BulkFromDate.AddDays(offset), today.AddDays(365)))
            .ToList();
        await _context.Shift.AddRangeAsync(containers.AsEnumerable().Reverse());
        await _context.SaveChangesAsync();

        var reported = await ReportedShiftIdsAsync();

        Assert.That(reported, Is.EqualTo(containers.Select(container => container.Id).ToList()));
    }

    [Test]
    public async Task DetectAsync_ContainerInOneGroup_CarriesThatGroup()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();
        var groupId = Guid.NewGuid();
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (container.Id, new[] { groupId }));

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.GroupIds, Is.EqualTo(new[] { groupId }));
        Assert.That(emptyContainerEvent.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ContainerInTwoGroups_CarriesBoth_NotOnlyTheFirst()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (container.Id, new[] { firstGroupId, secondGroupId }));

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.GroupIds, Is.EquivalentTo(new[] { firstGroupId, secondGroupId }));
    }

    [Test]
    public async Task DetectAsync_ContainerWithoutAnyGroup_CarriesNoGroup_AndStaysGroupScopeRequired()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.GroupIds, Is.Empty);
        Assert.That(emptyContainerEvent.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ManyContainers_ResolvesGroupsInOneBatchedLookup_NeverOnePerContainer()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var containers = Enumerable.Range(0, 10)
            .Select(_ => MakeContainer(today.AddDays(-1), today.AddDays(30)))
            .ToList();
        await _context.Shift.AddRangeAsync(containers);
        await _context.SaveChangesAsync();

        await _sut.DetectAsync();

        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). A container starting 2026-06-28 is already
        // active under the (correct) company day but not yet active under the (wrong) UTC day -
        // crossing IsPeriodActive's High/Medium severity boundary.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new EmptyContainerDetector(
            _shiftRepository, _containerTemplateRepository, _groupScopeReader, _agentConditionRepository,
            clock, Substitute.For<ILogger<EmptyContainerDetector>>());
        var container = MakeContainer(new DateOnly(2026, 6, 28), new DateOnly(2026, 6, 30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.High),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }
}

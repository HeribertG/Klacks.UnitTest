// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmptyContainerDetector -- covers the empty-database case, the positive
/// no-template-at-all case, the negative case with at least one template (which proves
/// empty_container is disjoint from unstaffed_shift rather than a subset of it), the
/// non-container (Task) exclusion, scenario-copy exclusion, soft-delete exclusion on both
/// Shift and ContainerTemplate, dedup-key stability, the active-period severity rule, and
/// the MaxFindingsPerTick emission cap.
/// Uses a real EF Core InMemory DataBaseContext with the real ShiftRepository and
/// ContainerTemplateRepository (as ContainerAvailableTasksServiceTests.cs does) because the
/// detector composes IQueryable via GetQuery() and awaits ToListAsync(), which a plain
/// NSubstitute-mocked IQueryable cannot support.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class EmptyContainerDetectorTests
{
    private DataBaseContext _context = null!;
    private ShiftRepository _shiftRepository = null!;
    private ContainerTemplateRepository _containerTemplateRepository = null!;
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
            scheduleMapper);

        var containerTemplateServiceLogger = Substitute.For<ILogger<ContainerTemplateService>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var containerTemplateService = new ContainerTemplateService(unitOfWork, containerTemplateServiceLogger);

        _containerTemplateRepository = new ContainerTemplateRepository(
            _context,
            containerTemplateLogger,
            collectionUpdateService,
            containerTemplateService);

        _sut = new EmptyContainerDetector(_shiftRepository, _containerTemplateRepository, detectorLogger);
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
        var containers = Enumerable.Range(0, EmptyContainerDetector.MaxFindingsPerTick + 5)
            .Select(_ => MakeContainer(today.AddDays(-1), today.AddDays(30)))
            .ToList();
        await _context.Shift.AddRangeAsync(containers);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(EmptyContainerDetector.MaxFindingsPerTick));
    }
}

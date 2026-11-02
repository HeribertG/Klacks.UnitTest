// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ContainerWorkExpansionService must not turn a template or override item into a Work outside the world of the container
/// work: a shift of a variant (key) is refused for every container work, a clone of a scenario only for a container work of
/// another world, and a real shift or a clone of the container work's own scenario expands as before. The real
/// WorkWriteGuardService decides, so the test fails when the expansion stops asking it.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerWorkExpansionServiceWorldTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);
    private const int MondayStorageWeekday = 1;

    private IContainerTemplateRepository _templateRepository = null!;
    private IContainerShiftOverrideRepository _overrideRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IShiftVisibilityReader _shiftVisibilityReader = null!;
    private ContainerWorkExpansionService _sut = null!;

    private readonly Guid _containerId = Guid.NewGuid();
    private readonly Guid _taskShiftId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _templateRepository = Substitute.For<IContainerTemplateRepository>();
        _overrideRepository = Substitute.For<IContainerShiftOverrideRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _shiftVisibilityReader = Substitute.For<IShiftVisibilityReader>();

        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(_containerId).Returns(new Shift { Id = _containerId, ShiftType = ShiftType.IsContainer });

        IWorkWriteGuard writeGuard = new WorkWriteGuardService(
            shiftRepository, _workRepository, Substitute.For<IPreCommitConflictChecker>(), _shiftVisibilityReader);

        _sut = new ContainerWorkExpansionService(
            _templateRepository,
            _overrideRepository,
            _workRepository,
            Substitute.For<IBreakRepository>(),
            Substitute.For<IWorkChangeRepository>(),
            shiftRepository,
            writeGuard,
            Substitute.For<ILogger<ContainerWorkExpansionService>>());
    }

    [Test]
    public async Task Template_ItemOnKeyShift_IsRefusedLikeAMissingShift_AndNothingIsWritten()
    {
        GivenTemplateWithItemOnTaskShift();
        GivenWorlds(new ShiftWorld(_taskShiftId, Guid.NewGuid(), IsVariant: true));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => _sut.ExpandAsync(NewContainerWork(null), Monday));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _taskShiftId));
        await _workRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Override_ItemOnKeyShift_IsRefusedLikeAMissingShift_AndNothingIsWritten()
    {
        GivenOverrideWithItemOnTaskShift();
        GivenWorlds(new ShiftWorld(_taskShiftId, Guid.NewGuid(), IsVariant: true));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => _sut.ExpandAsync(NewContainerWork(null), Monday));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _taskShiftId));
        await _workRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Template_ItemOnKeyShift_IsRefusedForAScenarioContainerWorkToo()
    {
        var scenarioToken = Guid.NewGuid();
        GivenTemplateWithItemOnTaskShift();
        GivenWorlds(new ShiftWorld(_taskShiftId, Guid.NewGuid(), IsVariant: true));

        await Should.ThrowAsync<KeyNotFoundException>(() => _sut.ExpandAsync(NewContainerWork(scenarioToken), Monday));

        await _workRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Template_ItemOnCloneOfAnotherScenario_IsRefusedForARealContainerWork()
    {
        GivenTemplateWithItemOnTaskShift();
        GivenWorlds(new ShiftWorld(_taskShiftId, Guid.NewGuid(), IsVariant: false));

        await Should.ThrowAsync<KeyNotFoundException>(() => _sut.ExpandAsync(NewContainerWork(null), Monday));

        await _workRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Template_ItemOnCloneOfTheContainerWorksOwnScenario_StillExpands()
    {
        var scenarioToken = Guid.NewGuid();
        GivenTemplateWithItemOnTaskShift();
        GivenWorlds(new ShiftWorld(_taskShiftId, scenarioToken, IsVariant: false));

        await _sut.ExpandAsync(NewContainerWork(scenarioToken), Monday);

        await _workRepository.Received(1).Add(Arg.Is<Work>(w => w.ShiftId == _taskShiftId && w.AnalyseToken == scenarioToken));
    }

    [Test]
    public async Task Template_ItemOnRealShift_ExpandsForARealAndAScenarioContainerWork()
    {
        var scenarioToken = Guid.NewGuid();
        GivenTemplateWithItemOnTaskShift();

        await _sut.ExpandAsync(NewContainerWork(null), Monday);
        await _sut.ExpandAsync(NewContainerWork(scenarioToken), Monday);

        await _workRepository.Received(2).Add(Arg.Is<Work>(w => w.ShiftId == _taskShiftId));
    }

    [Test]
    public async Task Override_ItemOnCloneOfTheContainerWorksOwnScenario_StillExpands()
    {
        var scenarioToken = Guid.NewGuid();
        GivenOverrideWithItemOnTaskShift();
        GivenWorlds(new ShiftWorld(_taskShiftId, scenarioToken, IsVariant: false));

        await _sut.ExpandAsync(NewContainerWork(scenarioToken), Monday);

        await _workRepository.Received(1).Add(Arg.Is<Work>(w => w.ShiftId == _taskShiftId && w.AnalyseToken == scenarioToken));
    }

    private Work NewContainerWork(Guid? analyseToken) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = _containerId,
        ClientId = Guid.NewGuid(),
        CurrentDate = Monday,
        AnalyseToken = analyseToken,
    };

    private void GivenTemplateWithItemOnTaskShift()
        => _templateRepository.GetTemplatesForContainer(_containerId).Returns(
        [
            new ContainerTemplate
            {
                ContainerId = _containerId,
                Weekday = MondayStorageWeekday,
                ContainerTemplateItems = [new ContainerTemplateItem { ShiftId = _taskShiftId }],
            },
        ]);

    private void GivenOverrideWithItemOnTaskShift()
        => _overrideRepository.GetByContainerAndDateWithItems(_containerId, Monday, Arg.Any<CancellationToken>()).Returns(
            new ContainerShiftOverride
            {
                ContainerId = _containerId,
                Date = Monday,
                ContainerShiftOverrideItems = [new ContainerShiftOverrideItem { ShiftId = _taskShiftId }],
            });

    private void GivenWorlds(params ShiftWorld[] worlds)
        => _shiftVisibilityReader
            .GetTokenedShiftWorldsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(worlds);
}

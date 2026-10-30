// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The optional restrictToShiftIds of ContainerAvailableTasksService only ever narrows the candidates: without a group list
/// it becomes the candidate filter, with a group list the two are intersected (never united), an empty intersection or an
/// empty restriction stays empty (no rows) and null leaves the group list untouched.
/// </summary>

using Klacks.Api.Domain.Services.ContainerTemplates;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.ContainerTemplates;

[TestFixture]
public class ContainerAvailableTasksServiceRestrictionTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ShiftA = Guid.NewGuid();
    private static readonly Guid ShiftB = Guid.NewGuid();
    private static readonly Guid ShiftC = Guid.NewGuid();
    private static readonly Guid ShiftOutsideGroup = Guid.NewGuid();

    private IShiftReader _shiftReader = null!;
    private IGroupItemRepository _groupItems = null!;
    private AvailableTaskCandidateCriteria? _criteria;
    private ContainerAvailableTasksService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _criteria = null;
        _shiftReader = Substitute.For<IShiftReader>();
        _shiftReader.GetAvailableTaskCandidatesAsync(Arg.Any<AvailableTaskCandidateCriteria>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _criteria = ci.Arg<AvailableTaskCandidateCriteria>();
                return (IReadOnlyList<Shift>)[];
            });

        _groupItems = Substitute.For<IGroupItemRepository>();
        _groupItems.GetGroupIdsByShiftId(ContainerId, Arg.Any<CancellationToken>()).Returns([GroupId]);
        _groupItems.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>()).Returns([ShiftA, ShiftB, ShiftC]);
        var templates = Substitute.For<IContainerTemplateRepository>();
        templates.GetUsedShiftIds(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());

        _service = new ContainerAvailableTasksService(
            _shiftReader, _groupItems, templates, Substitute.For<ILogger<ContainerAvailableTasksService>>());
    }

    [Test]
    public async Task WithoutARestriction_TheGroupShiftsAreTheOnlyFilter()
    {
        await _service.GetAvailableTasksAsync(ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0));

        _criteria!.OnlyShiftIds.ShouldBe([ShiftA, ShiftB, ShiftC], ignoreOrder: true);
    }

    [Test]
    public async Task ARestriction_IsIntersectedWithTheGroupShifts_AndNeverWidensThem()
    {
        await _service.GetAvailableTasksAsync(
            ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0),
            restrictToShiftIds: [ShiftB, ShiftOutsideGroup]);

        _criteria!.OnlyShiftIds.ShouldBe([ShiftB]);
    }

    [Test]
    public async Task ARestrictionDisjointFromTheGroup_YieldsAnEmptyFilter_NotNoFilter()
    {
        await _service.GetAvailableTasksAsync(
            ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0),
            restrictToShiftIds: [ShiftOutsideGroup]);

        _criteria!.OnlyShiftIds.ShouldNotBeNull();
        _criteria.OnlyShiftIds.ShouldBeEmpty();
    }

    [Test]
    public async Task AnEmptyRestriction_YieldsAnEmptyFilter()
    {
        await _service.GetAvailableTasksAsync(
            ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0),
            restrictToShiftIds: []);

        _criteria!.OnlyShiftIds.ShouldNotBeNull();
        _criteria.OnlyShiftIds.ShouldBeEmpty();
    }

    [Test]
    public async Task WithoutAGroupFilter_TheRestrictionBecomesTheFilter()
    {
        _groupItems.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>()).Returns([ShiftA]);

        await _service.GetAvailableTasksAsync(
            ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0),
            restrictToShiftIds: [ShiftB, ShiftOutsideGroup]);

        _criteria!.OnlyShiftIds.ShouldBe([ShiftB, ShiftOutsideGroup], ignoreOrder: true);
    }
}

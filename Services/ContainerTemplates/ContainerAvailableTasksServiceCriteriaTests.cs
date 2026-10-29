// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The criteria ContainerAvailableTasksService hands to IShiftReader.GetAvailableTaskCandidatesAsync: the container's
/// validity period becomes ValidFrom/ValidUntil (read through GetValidityPeriodAsync for the container id), and a container
/// that cannot be read leaves both null, so no period filter applies.
/// </summary>

using Klacks.Api.Domain.Services.ContainerTemplates;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.ContainerTemplates;

[TestFixture]
public class ContainerAvailableTasksServiceCriteriaTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private IShiftReader _shiftReader = null!;
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

        var groupItems = Substitute.For<IGroupItemRepository>();
        groupItems.GetGroupIdsByShiftId(ContainerId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        var templates = Substitute.For<IContainerTemplateRepository>();
        templates.GetUsedShiftIds(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());

        _service = new ContainerAvailableTasksService(
            _shiftReader, groupItems, templates, Substitute.For<ILogger<ContainerAvailableTasksService>>());
    }

    [Test]
    public async Task TheContainersValidityPeriod_BecomesTheCandidatesPeriod()
    {
        var from = new DateOnly(2026, 11, 1);
        var until = new DateOnly(2026, 11, 30);
        _shiftReader.GetValidityPeriodAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ShiftValidityPeriod(from, until));

        await _service.GetAvailableTasksAsync(ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0));

        _criteria.ShouldNotBeNull();
        _criteria.ValidFrom.ShouldBe(from);
        _criteria.ValidUntil.ShouldBe(until);
        await _shiftReader.Received(1).GetValidityPeriodAsync(ContainerId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnOpenEndedContainer_HasNoUpperBound()
    {
        var from = new DateOnly(2026, 11, 1);
        _shiftReader.GetValidityPeriodAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ShiftValidityPeriod(from, null));

        await _service.GetAvailableTasksAsync(ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0));

        _criteria!.ValidFrom.ShouldBe(from);
        _criteria.ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task AContainerThatCannotBeRead_AppliesNoPeriodFilter()
    {
        _shiftReader.GetValidityPeriodAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns((ShiftValidityPeriod?)null);

        await _service.GetAvailableTasksAsync(ContainerId, 3, new TimeOnly(8, 0), new TimeOnly(18, 0));

        _criteria!.ValidFrom.ShouldBeNull();
        _criteria.ValidUntil.ShouldBeNull();
    }
}

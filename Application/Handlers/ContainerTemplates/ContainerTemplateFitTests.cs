// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner rule (2026-11-03) for the container template: an item that reaches outside the template window, by itself or by
/// its travel, briefing or debriefing time, refuses the save (Post and Put) before anything is written. Template 08:00-16:00.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ContainerTemplates;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ContainerTemplates;

[TestFixture]
public class ContainerTemplateFitTests
{
    private readonly Guid _containerId = Guid.NewGuid();
    private IShiftRepository _shiftRepository = null!;
    private IContainerTemplateRepository _templateRepository = null!;
    private IContainerLockRepository _lockRepository = null!;
    private List<ContainerTemplate> _added = null!;

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(_containerId).Returns(new Shift
        {
            Id = _containerId, Status = ShiftStatus.OriginalShift, ShiftType = ShiftType.IsContainer,
        });
        _templateRepository = Substitute.For<IContainerTemplateRepository>();
        _added = [];
        _templateRepository.Add(Arg.Do<ContainerTemplate>(t => _added.Add(t))).Returns(Task.CompletedTask);
        _templateRepository.GetTemplatesForContainerWithTracking(_containerId).Returns(new List<ContainerTemplate>());
        _lockRepository = Substitute.For<IContainerLockRepository>();
        _lockRepository.IsHeldBy(Arg.Any<string>(), _containerId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private ContainerTemplateResource Template(params ContainerTemplateItemResource[] items) => new()
    {
        ContainerId = _containerId, FromTime = T(8), UntilTime = T(16), Weekday = 1,
        ContainerTemplateItems = items.ToList(),
    };

    private static ContainerTemplateItemResource Item(TimeOnly start, TimeOnly end, TimeOnly? travelBefore = null) => new()
    {
        ShiftId = Guid.NewGuid(), Weekday = 1, StartItem = start, EndItem = end,
        TravelTimeBefore = travelBefore ?? TimeOnly.MinValue,
    };

    private static IEnumerable<TestCaseData> Overhangs()
    {
        yield return new TestCaseData(Item(T(15), T(17))).SetName("ItemEndsAfterUntil");
        yield return new TestCaseData(Item(T(7), T(9))).SetName("ItemStartsBeforeFrom");
        yield return new TestCaseData(Item(T(8, 15), T(10), T(0, 45))).SetName("ItemFitsButTravelBeforeReachesOutside");
    }

    [TestCaseSource(nameof(Overhangs))]
    public async Task PostHandler_RefusesOverhangingItem(ContainerTemplateItemResource item)
    {
        var handler = new PostContainerTemplatesCommandHandler(
            _templateRepository, Substitute.For<IUnitOfWork>(), new ScheduleMapper(), _lockRepository,
            Substitute.For<IUserService>(), Substitute.For<IContainerVisibilityGuard>(),
            Substitute.For<ILogger<PostContainerTemplatesCommandHandler>>());

        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => handler.Handle(new PostContainerTemplatesCommand(_containerId, [Template(item)]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }

    [TestCaseSource(nameof(Overhangs))]
    public async Task PutHandler_RefusesOverhangingItem(ContainerTemplateItemResource item)
    {
        var handler = new PutContainerTemplatesCommandHandler(
            _templateRepository, Substitute.For<IUnitOfWork>(), new ScheduleMapper(), _lockRepository,
            Substitute.For<IUserService>(), Substitute.For<IContainerVisibilityGuard>(),
            Substitute.For<ILogger<PutContainerTemplatesCommandHandler>>());

        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => handler.Handle(new PutContainerTemplatesCommand(_containerId, [Template(item)]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task PostHandler_StoresAnItemThatFitsWithItsTravelTime()
    {
        var handler = new PostContainerTemplatesCommandHandler(
            _templateRepository, Substitute.For<IUnitOfWork>(), new ScheduleMapper(), _lockRepository,
            Substitute.For<IUserService>(), Substitute.For<IContainerVisibilityGuard>(),
            Substitute.For<ILogger<PostContainerTemplatesCommandHandler>>());

        await handler.Handle(
            new PostContainerTemplatesCommand(_containerId, [Template(Item(T(8, 45), T(10), T(0, 45)))]), CancellationToken.None);

        _added.Count.ShouldBe(1);
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container save with the real children write guard: the stored seal state reaches the children manager, a child or a
/// work change whose stored owner or person is hidden from the caller makes the save look like a missing container, and a
/// stored child the caller may not write, also one left out of the request, refuses the save before anything is written.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class UpdateContainerWorkChildrenSealTests
{
    private static readonly DateOnly Day = new(2027, 5, 10);
    private static readonly DateTime SealedAt = new(2027, 5, 11, 8, 0, 0, DateTimeKind.Utc);

    private IContainerWorkChildrenReadRepository _childrenReadRepository = null!;
    private IContainerWorkChildrenManager _childrenManager = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private Work _container = null!;
    private Work _storedWork = null!;
    private List<Work> _storedWorks = null!;
    private List<WorkChange> _storedChanges = null!;
    private List<Work> _writtenWorks = null!;

    [SetUp]
    public void SetUp()
    {
        _childrenReadRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        _childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _writtenWorks = [];
        _container = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0),
        };
        _storedWork = new Work
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, ShiftId = _container.ShiftId, CurrentDate = Day,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(12, 0), ParentWorkId = _container.Id,
            LockLevel = WorkLockLevel.Confirmed, SealedAt = SealedAt, SealedBy = "supervisor",
        };
        _storedWorks = [_storedWork];
        _storedChanges = [];

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        _childrenReadRepository.GetParentWorkNoTracking(_container.Id, Arg.Any<CancellationToken>()).Returns(_container);
        _childrenReadRepository.GetChildWorksWithShiftClient(_container.Id, Arg.Any<CancellationToken>()).Returns(_ => _storedWorks);
        _childrenReadRepository.GetChildBreaksWithAbsence(_container.Id, Arg.Any<CancellationToken>()).Returns(new List<Break>());
        _childrenReadRepository.GetWorkChangesForWorks(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _storedChanges);
        _childrenReadRepository.GetUsedEntryIds(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new UsedEntryIds(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>()));
        _childrenManager.UpdateChildrenAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeOnly?>(), Arg.Any<TimeOnly?>(),
                Arg.Do<List<Work>>(works => _writtenWorks = works), Arg.Any<List<Break>>(), Arg.Any<List<WorkChange>>(),
                Arg.Any<CancellationToken>())
            .Returns((Work?)null);
        GivenRole(null);
    }

    [Test]
    public async Task StoredSealState_ReachesTheManager_EvenThoughTheRequestCarriesNone()
    {
        await Save(Resource(Requested(_storedWork)));

        var written = _writtenWorks.ShouldHaveSingleItem();
        written.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
        written.SealedAt.ShouldBe(SealedAt);
        written.SealedBy.ShouldBe("supervisor");
    }

    [Test]
    public async Task StoredChildOfAHiddenClient_IsAnsweredLikeAMissingContainer()
    {
        var hidden = Guid.NewGuid();
        _storedWork.ClientId = hidden;
        _clientVisibilityGuard.AreAllVisibleAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hidden)), Arg.Any<CancellationToken>())
            .Returns(false);
        var request = Requested(_storedWork);
        request.ClientId = _container.ClientId;

        await Should.ThrowAsync<KeyNotFoundException>(() => Save(Resource(request)));

        await WasNotWritten();
    }

    [Test]
    public async Task StoredWorkChangeWithAHiddenPerson_IsAnsweredLikeAMissingContainer()
    {
        var hidden = Guid.NewGuid();
        _storedChanges.Add(new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = _storedWork.Id, Type = WorkChangeType.ReplacementWithin, ReplaceClientId = hidden,
        });
        _clientVisibilityGuard.AreAllVisibleAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hidden)), Arg.Any<CancellationToken>())
            .Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => Save(Resource(Requested(_storedWork))));

        await WasNotWritten();
    }

    [Test]
    public async Task ClosedStoredChild_RefusesTheSave_BeforeAnythingIsWritten()
    {
        _storedWork.LockLevel = WorkLockLevel.Closed;

        await Should.ThrowAsync<InvalidRequestException>(() => Save(Resource(Requested(_storedWork))));

        await WasNotWritten();
    }

    [Test]
    public async Task ClosedStoredChildLeftOutOfTheRequest_RefusesTheSave()
    {
        _storedWork.LockLevel = WorkLockLevel.Closed;

        await Should.ThrowAsync<InvalidRequestException>(() => Save(Resource()));

        await WasNotWritten();
    }

    [Test]
    public async Task NewChildWithAnIdOfItsOwn_IsSaved_AsTheContainerDialogDoesIt()
    {
        var resource = Resource(Requested(_storedWork));
        resource.SubWorks.Add(new WorkResource
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, ShiftId = _container.ShiftId, CurrentDate = Day,
        });

        await Save(resource);

        _writtenWorks.Count.ShouldBe(2);
    }

    [Test]
    public async Task NewChildWithTheIdOfTheContainer_IsAnsweredLikeAMissingContainer()
    {
        _childrenReadRepository.GetUsedEntryIds(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new UsedEntryIds(new HashSet<Guid> { _container.Id }, new HashSet<Guid>(), new HashSet<Guid>()));
        var resource = Resource(Requested(_storedWork));
        resource.SubWorks.Add(new WorkResource
        {
            Id = _container.Id, ClientId = _container.ClientId, ShiftId = _container.ShiftId, CurrentDate = Day,
        });

        await Should.ThrowAsync<KeyNotFoundException>(() => Save(resource));

        await WasNotWritten();
    }

    private Task Save(UpdateContainerWorkChildrenResource resource)
        => NewHandler().Handle(new UpdateContainerWorkChildrenCommand(_container.Id, resource), CancellationToken.None);

    private async Task WasNotWritten()
        => await _childrenManager.DidNotReceiveWithAnyArgs().UpdateChildrenAsync(
            default, default, default, default, default, default!, default!, default!, default);

    private static UpdateContainerWorkChildrenResource Resource(params WorkResource[] works)
    {
        var resource = new UpdateContainerWorkChildrenResource();
        resource.SubWorks.AddRange(works);
        return resource;
    }

    private static WorkResource Requested(Work stored) => new()
    {
        Id = stored.Id, ClientId = stored.ClientId, ShiftId = stored.ShiftId, CurrentDate = stored.CurrentDate,
        StartTime = stored.StartTime, EndTime = stored.EndTime,
    };

    private void GivenRole(string? role)
    {
        var claims = role == null ? [] : new[] { new Claim(ClaimTypes.Role, role) };
        _httpContextAccessor.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
        });
    }

    private UpdateContainerWorkChildrenCommandHandler NewHandler()
    {
        var lockRepository = Substitute.For<IContainerLockRepository>();
        lockRepository.IsHeldBy(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var childrenGuard = new ContainerChildrenWriteGuard(
            _childrenReadRepository, new ParentWorkLockGuard(new WorkLockLevelService()),
            Substitute.For<IDayLockService>(), _httpContextAccessor, Substitute.For<IShiftRepository>());
        return new UpdateContainerWorkChildrenCommandHandler(
            _childrenReadRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            Substitute.For<IUnitOfWork>(), Substitute.For<IWorkNotificationFacade>(), lockRepository,
            Substitute.For<IUserService>(), _childrenManager, Substitute.For<IOvertimeCascadeService>(),
            ShiftVisibilityGuards.AllowAll(), Substitute.For<IWorkWriteGuard>(), Substitute.For<IReplacementWriteGuard>(), childrenGuard,
            Substitute.For<ILogger<UpdateContainerWorkChildrenCommandHandler>>());
    }
}

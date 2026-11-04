// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner 2026-11-04: a container save that leaves out a sealed sub-break deletes it, so it passes the delete rule of a
/// break: only an admin may remove a sealed break; a planner or supervisor is refused before anything is written.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerSealedBreakRemovalTests
{
    private static readonly DateOnly Day = new(2027, 5, 10);

    private Work _container = null!;
    private Break _sealedBreak = null!;

    [SetUp]
    public void SetUp()
    {
        _container = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0),
        };
        _sealedBreak = new Break
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, CurrentDate = Day, ParentWorkId = _container.Id,
            StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(12, 30), LockLevel = WorkLockLevel.Confirmed,
        };
    }

    private Task Prepare(string? role, IReadOnlyList<Break> breaks)
    {
        var readRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        readRepository.GetUsedEntryIds(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new UsedEntryIds(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>()));
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.GetWithClientByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift>());
        var claims = role == null ? [] : new[] { new Claim(ClaimTypes.Role, role) };
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) });
        var guard = new ContainerChildrenWriteGuard(
            readRepository, new ParentWorkLockGuard(new WorkLockLevelService()), Substitute.For<IDayLockService>(), http,
            shiftRepository);
        return guard.PrepareAsync(
            _container, new StoredContainerChildren([], [_sealedBreak], []), [], breaks, [], CancellationToken.None);
    }

    [TestCase(null)]
    [TestCase(Roles.Authorised)]
    public async Task NonAdmin_RemovingAConfirmedSubBreak_IsRefused(string? role)
    {
        await Should.ThrowAsync<InvalidRequestException>(() => Prepare(role, []));
    }

    [Test]
    public async Task Admin_MayRemoveAConfirmedSubBreak()
    {
        await Prepare(Roles.Admin, []);
    }

    [Test]
    public async Task Planner_KeepingTheConfirmedSubBreak_IsNotRefused()
    {
        var kept = new Break { Id = _sealedBreak.Id, CurrentDate = Day, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(12, 30) };

        await Prepare(null, [kept]);
    }
}

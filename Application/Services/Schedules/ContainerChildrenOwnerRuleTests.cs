// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner rule of the services in a container save (Owner 2026-11-03): a service belongs to the customer of its shift; a
/// service without customer belongs to the person of the container, whatever client the request names (empty included).
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerChildrenOwnerRuleTests
{
    private static readonly DateOnly Day = new(2027, 5, 10);

    private readonly Guid _customer = Guid.NewGuid();
    private readonly Shift _customerShift = new() { Id = Guid.NewGuid(), ClientId = null };
    private readonly Shift _ownShift = new() { Id = Guid.NewGuid(), ClientId = null };
    private Work _container = null!;
    private ContainerChildrenWriteGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _customerShift.ClientId = _customer;
        _container = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0),
        };
        var readRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        readRepository.GetUsedEntryIds(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new UsedEntryIds(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>()));
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.GetWithClientByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { _customerShift, _ownShift });
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth")) });
        _guard = new ContainerChildrenWriteGuard(
            readRepository, new ParentWorkLockGuard(new WorkLockLevelService()), Substitute.For<IDayLockService>(), http,
            shiftRepository);
    }

    private Work SubWork(Shift shift, Guid clientId) => new()
    {
        Id = Guid.NewGuid(), ShiftId = shift.Id, ClientId = clientId, CurrentDate = Day,
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
    };

    private Task Prepare(params Work[] works) => _guard.PrepareAsync(
        _container, new StoredContainerChildren([], [], []), works, [], [], CancellationToken.None);

    [Test]
    public async Task ServiceWithoutCustomer_SentWithAnEmptyClient_BelongsToTheContainerPerson()
    {
        var work = SubWork(_ownShift, Guid.Empty);

        await Prepare(work);

        work.ClientId.ShouldBe(_container.ClientId);
    }

    [Test]
    public async Task ServiceWithoutCustomer_SentWithAnotherPerson_BelongsToTheContainerPerson()
    {
        var work = SubWork(_ownShift, Guid.NewGuid());

        await Prepare(work);

        work.ClientId.ShouldBe(_container.ClientId);
    }

    [Test]
    public async Task ServiceWithCustomer_BelongsToTheCustomer_WhateverTheRequestSays()
    {
        var work = SubWork(_customerShift, _container.ClientId);

        await Prepare(work);

        work.ClientId.ShouldBe(_customer);
    }

    [Test]
    public void OwnerRule_FallsBackToTheContainerPerson_ForAMissingOrEmptyCustomer()
    {
        var person = Guid.NewGuid();

        ContainerChildOwner.Of(null, person).ShouldBe(person);
        ContainerChildOwner.Of(Guid.Empty, person).ShouldBe(person);
        ContainerChildOwner.Of(_customer, person).ShouldBe(_customer);
    }
}

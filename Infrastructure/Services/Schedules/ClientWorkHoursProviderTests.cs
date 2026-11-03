// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The daily hours of a person against an in-memory DataBaseContext: a container counts its own time once, a child of the
/// container's own person adds nothing, and a scenario row stays out of the real plan.
/// </summary>

using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ClientWorkHoursProviderTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly Guid Person = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private ClientWorkHoursProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _provider = new ClientWorkHoursProvider(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task AContainerChildOfTheSamePerson_AddsNoHoursOfItsOwn()
    {
        var container = NewWork(Person, null, 8m, null);
        _context.Work.AddRange(
            container,
            NewWork(Person, container.Id, 3m, null),
            NewWork(Guid.NewGuid(), container.Id, 3m, null),
            NewWork(Person, null, 5m, Guid.NewGuid()));
        await _context.SaveChangesAsync();

        var hours = await _provider.GetWorkHoursByDayAsync(Person, Day, Day, null);

        hours[Day].ShouldBe(container.WorkTime);
    }

    private static Work NewWork(Guid clientId, Guid? parentWorkId, decimal workTime, Guid? analyseToken) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ParentWorkId = parentWorkId,
        CurrentDate = Day,
        WorkTime = workTime,
        AnalyseToken = analyseToken,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
    };
}

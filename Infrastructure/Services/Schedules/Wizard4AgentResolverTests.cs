// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// A group may mix staff and customers. The resolver hands its result straight to the bitmap builder,
/// so a customer that slips through becomes a plannable row and the optimiser starts assigning shifts
/// to somebody who does not work here.
/// </summary>
[TestFixture]
public sealed class Wizard4AgentResolverTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly Until = new(2026, 10, 31);
    private static readonly Guid GroupId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private Wizard4AgentResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new Wizard4AgentResolver(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ResolveAsync_ReturnsEmployeesAndExternalStaff()
    {
        var employee = await AddMemberAsync(EntityTypeEnum.Employee);
        var external = await AddMemberAsync(EntityTypeEnum.ExternEmp);

        var result = await _sut.ResolveAsync(GroupId, From, Until, CancellationToken.None);

        result.ShouldBe([employee, external], ignoreOrder: true);
    }

    [Test]
    public async Task ResolveAsync_LeavesCustomersOut()
    {
        var employee = await AddMemberAsync(EntityTypeEnum.Employee);
        await AddMemberAsync(EntityTypeEnum.Customer);

        var result = await _sut.ResolveAsync(GroupId, From, Until, CancellationToken.None);

        result.ShouldBe([employee]);
    }

    [Test]
    public async Task ResolveAsync_MembershipWithoutAClientRow_IsLeftOut()
    {
        // Without a client row the type is unknown, and an unknown type must not be treated as staff.
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = GroupId,
            ClientId = Guid.NewGuid(),
        });
        await _context.SaveChangesAsync();

        var result = await _sut.ResolveAsync(GroupId, From, Until, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveAsync_MembershipEndedBeforeThePeriod_IsLeftOut()
    {
        await AddMemberAsync(
            EntityTypeEnum.Employee,
            validUntil: From.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var result = await _sut.ResolveAsync(GroupId, From, Until, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveAsync_OtherGroup_IsLeftOut()
    {
        await AddMemberAsync(EntityTypeEnum.Employee, groupId: Guid.NewGuid());

        var result = await _sut.ResolveAsync(GroupId, From, Until, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    private async Task<Guid> AddMemberAsync(
        EntityTypeEnum type, Guid? groupId = null, DateTime? validUntil = null)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client { Id = clientId, Type = type });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId ?? GroupId,
            ClientId = clientId,
            ValidUntil = validUntil,
        });
        await _context.SaveChangesAsync();
        return clientId;
    }
}

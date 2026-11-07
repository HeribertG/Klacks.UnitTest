// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The manual recalculation is open to planners (owner decision 2026-11-07), so PeriodHoursService has to keep a
/// group-restricted caller inside their group visibility: RecalculateAllClientsAsync writes rows only for the clients the
/// caller may see, and GetRestrictedCallerClientIdsAsync (the client scope of the thorough run and of the break
/// reprocessing) returns the same population united over the pay periods of the window, or null for an unrestricted
/// caller. Uses the real ClientGroupFilterService with a stubbed visibility scope.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Accounts;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class PeriodHoursCallerVisibilityTests
{
    private const string PlannerId = "planner-id";
    private const decimal WorkTime = 8m;

    private static readonly Guid VisibleGroup = Guid.NewGuid();
    private static readonly Guid HiddenGroup = Guid.NewGuid();
    private static readonly Guid VisibleClient = Guid.NewGuid();
    private static readonly Guid VisibleNovemberJoiner = Guid.NewGuid();
    private static readonly Guid VisibleFormerMember = Guid.NewGuid();
    private static readonly Guid HiddenClient = Guid.NewGuid();
    private static readonly DateOnly NovemberStart = new(2026, 11, 1);
    private static readonly DateOnly NovemberEnd = new(2026, 11, 30);

    private DataBaseContext _context = null!;
    private IGroupVisibilityService _groupVisibility = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _context.Settings.Add(new SettingsModel
        {
            Type = SettingKeys.PaymentInterval,
            Value = ((int)PaymentInterval.Monthly).ToString()
        });
        _context.Client.AddRange(
            Member(VisibleClient, VisibleGroup, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null),
            Member(VisibleNovemberJoiner, VisibleGroup, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), null),
            Member(VisibleFormerMember, VisibleGroup,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)),
            Member(HiddenClient, HiddenGroup, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null));
        _context.Work.AddRange(NovemberWork(VisibleClient), NovemberWork(HiddenClient));
        await _context.SaveChangesAsync();

        _groupVisibility = Substitute.For<IGroupVisibilityService>();
        _groupVisibility.GetVisibilityScopeAsync()
            .Returns(GroupVisibilityScope.Restricted([VisibleGroup], [VisibleGroup]));
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task RecalculateAllClients_ForARestrictedCaller_WritesOnlyTheClientsTheCallerMaySee()
    {
        await NewService().RecalculateAllClientsAsync(NovemberStart, NovemberEnd);

        var clientIds = await _context.ClientPeriodHours.AsNoTracking().Select(p => p.ClientId).ToListAsync();
        clientIds.ShouldBe([VisibleClient, VisibleNovemberJoiner], ignoreOrder: true);
    }

    [Test]
    public async Task RecalculateAllClients_ForAnUnrestrictedCaller_WritesEveryMember()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());

        await NewService().RecalculateAllClientsAsync(NovemberStart, NovemberEnd);

        var clientIds = await _context.ClientPeriodHours.AsNoTracking().Select(p => p.ClientId).ToListAsync();
        clientIds.ShouldBe([VisibleClient, VisibleNovemberJoiner, HiddenClient], ignoreOrder: true);
    }

    [Test]
    public async Task RestrictedCallerClientIds_AreTheVisibleMembersOfEveryPayPeriodOfTheWindow()
    {
        var clientIds = await NewService().GetRestrictedCallerClientIdsAsync(new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 10));

        clientIds.ShouldNotBeNull();
        clientIds.ShouldBe([VisibleClient, VisibleNovemberJoiner], ignoreOrder: true);
    }

    [Test]
    public async Task RestrictedCallerClientIds_OfACallerWhoSeesNobody_AreEmptyNotNull()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([], []));

        var clientIds = await NewService().GetRestrictedCallerClientIdsAsync(NovemberStart, NovemberEnd);

        clientIds.ShouldNotBeNull();
        clientIds.ShouldBeEmpty();
    }

    [Test]
    public async Task RestrictedCallerClientIds_OfAnUnrestrictedCaller_AreNull()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());

        var clientIds = await NewService().GetRestrictedCallerClientIdsAsync(NovemberStart, NovemberEnd);

        clientIds.ShouldBeNull();
    }

    private PeriodHoursService NewService()
    {
        var user = Substitute.For<IUserService>();
        user.GetIdString().Returns(PlannerId);
        var groupFilter = new ClientGroupFilterService(
            Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>(),
            _groupVisibility,
            user,
            Substitute.For<ILogger<ClientGroupFilterService>>());
        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

        return new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            groupFilter,
            contractData,
            Substitute.For<IWeekConfiguration>(),
            new FrozenPeriodPolicy(_context));
    }

    private static Client Member(Guid clientId, Guid groupId, DateTime validFrom, DateTime? validUntil) => new()
    {
        Id = clientId,
        Name = "Member",
        FirstName = clientId.ToString("N")[..8],
        Membership = new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = validFrom,
            ValidUntil = validUntil
        },
        GroupItems =
        [
            new GroupItem
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                GroupId = groupId
            }
        ]
    };

    private static Work NovemberWork(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = new DateOnly(2026, 11, 3),
        WorkTime = WorkTime,
        StartTime = new TimeOnly(5, 0),
        EndTime = new TimeOnly(13, 0)
    };
}

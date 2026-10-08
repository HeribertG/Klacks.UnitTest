// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Wizard 2 reads the same scope as Wizard 1: a shift preference set on an order reaches the cut pieces that are
/// actually staffed (K16).
/// </summary>
[TestFixture]
public class HarmonizerContextBuilderScopeTests
{
    private static readonly DateOnly WeekStart = new(2026, 1, 5);
    private static readonly DateOnly WeekEnd = new(2026, 1, 11);

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractProvider = null!;
    private IPlanningRuleSetLoader _ruleSetLoader = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _ruleSetLoader = Substitute.For<IPlanningRuleSetLoader>();
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], []));
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task BlacklistOnTheOrder_ReachesEveryCutPiece()
    {
        var agent = Guid.NewGuid();
        StubContract(agent);
        var (order, root, child) = SeedCutOrder(new TimeOnly(7, 0), new TimeOnly(15, 0), new TimeOnly(15, 0), new TimeOnly(23, 0));
        _context.ClientShiftPreference.Add(new ClientShiftPreference
        {
            Id = Guid.NewGuid(), ClientId = agent, ShiftId = order, PreferenceType = ShiftPreferenceType.Blacklist,
        });
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(
            new HarmonizerContextRequest(WeekStart, WeekEnd, [agent], AnalyseToken: null), CancellationToken.None);

        input.Agents.Single().BlacklistedShiftIds!.ShouldBe([order, root, child], ignoreOrder: true);
    }

    [Test]
    public async Task PreferredOrder_AddsTheSymbolsOfItsCutPieces()
    {
        var agent = Guid.NewGuid();
        StubContract(agent);
        var (order, _, _) = SeedCutOrder(new TimeOnly(7, 0), new TimeOnly(15, 0), new TimeOnly(15, 0), new TimeOnly(23, 0));
        _context.ClientShiftPreference.Add(new ClientShiftPreference
        {
            Id = Guid.NewGuid(), ClientId = agent, ShiftId = order, PreferenceType = ShiftPreferenceType.Preferred,
        });
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(
            new HarmonizerContextRequest(WeekStart, WeekEnd, [agent], AnalyseToken: null), CancellationToken.None);

        input.Agents.Single().PreferredShiftSymbols.ShouldBe([CellSymbol.Early, CellSymbol.Late], ignoreOrder: true);
    }

    private (Guid Order, Guid Root, Guid Child) SeedCutOrder(TimeOnly rootStart, TimeOnly rootEnd, TimeOnly childStart, TimeOnly childEnd)
    {
        var order = Guid.NewGuid();
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = order, Name = "Order", Status = ShiftStatus.SealedOrder, StartShift = rootStart, EndShift = childEnd });
        _context.Shift.Add(new Shift { Id = root, Name = "Root", Status = ShiftStatus.SplitShift, OriginalId = order, RootId = root, StartShift = rootStart, EndShift = rootEnd });
        _context.Shift.Add(new Shift { Id = child, Name = "Child", Status = ShiftStatus.SplitShift, OriginalId = order, ParentId = root, RootId = root, StartShift = childStart, EndShift = childEnd });
        return (order, root, child);
    }
    private void StubContract(Guid agent)
    {
        var data = new EffectiveContractData
        {
            GuaranteedHours = 160m,
            PaymentInterval = (int)PaymentInterval.Monthly,
            MaxConsecutiveDays = 7,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>())
            .Returns(new Dictionary<Guid, EffectiveContractData> { [agent] = data });

        var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            range[date] = new Dictionary<Guid, EffectiveContractData> { [agent] = data };
        }
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(range);

        _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = "Keyword" });
        _context.SaveChanges();
    }

    private HarmonizerContextBuilder BuildSut()
    {
        var softening = Substitute.For<IWorkSofteningRepository>();
        softening.LoadAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkSoftening>>([]));

        var eligibility = Substitute.For<IEligibilityMatrixBuilder>();
        eligibility.BuildAsync(default!, default!, default!, default).ReturnsForAnyArgs(new EligibilityMatrix
        {
            Ineligible = new HashSet<(string, Guid, DateOnly)>(),
            Gaps = new Dictionary<(string, Guid, DateOnly), IReadOnlyList<QualificationGap>>(),
            QualificationInfo = new Dictionary<Guid, QualificationInfo>(),
            ShiftNames = new Dictionary<Guid, string>(),
        });

        var availability = Substitute.For<IAvailabilityIneligibilityService>();
        availability.GetAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlySet<(string, Guid, DateOnly)>>(new HashSet<(string, Guid, DateOnly)>()));

        var windows = Substitute.For<IWizardRestrictedWindowBuilder>();
        windows.BuildAsync(default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<CoreRestrictedTimeWindow>>([]));

        var keywords = Substitute.For<IScheduleCommandKeywordProvider>();
        keywords.GetAsync(default).ReturnsForAnyArgs(ScheduleCommandKeywordTestFactory.Default);

        return new HarmonizerContextBuilder(_context, _contractProvider, softening, eligibility, availability, windows, keywords, _ruleSetLoader);
    }
}
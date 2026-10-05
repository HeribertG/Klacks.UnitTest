// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the live scenario summary: demand per shift and day, filled slots from the scenario works, and the cause named
/// for each open slot. The scenario mirrors the Bern case: an early and a late shift, one agent on a contract without
/// shift work and without weekends, one early work placed.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ScenarioSummaryBuilderTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly Until = new(2026, 10, 7);

    private DataBaseContext _context = null!;
    private readonly Guid _token = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private Shift _early = null!;
    private Shift _late = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new DataBaseContext(
            new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, null!);

        _early = NewShift("Frueh", new TimeOnly(7, 0), new TimeOnly(15, 0));
        _late = NewShift("Spaet", new TimeOnly(15, 0), new TimeOnly(23, 0));
        _context.Shift.AddRange(_early, _late);
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(), ClientId = _agentId, ShiftId = _early.Id, CurrentDate = From,
            AnalyseToken = _token, WorkTime = 8m,
        });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private Shift NewShift(string name, TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(), Name = name, Abbreviation = name[..2], AnalyseToken = _token,
        Status = ShiftStatus.OriginalShift, ShiftType = ShiftType.IsTask,
        FromDate = new DateOnly(2026, 1, 1), StartShift = start, EndShift = end,
        IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
        IsSaturday = true, IsSunday = true, Quantity = 1, SumEmployees = 1, WorkTime = 8m,
    };

    private ScenarioSummaryBuilder BuildSut()
    {
        var scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        scenarioRepository.GetByTokenAsync(_token, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = _token, FromDate = From, UntilDate = Until, GroupId = null });

        var contractProvider = Substitute.For<IClientContractDataProvider>();
        var contracts = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = From; date <= Until; date = date.AddDays(1))
        {
            contracts[date] = new Dictionary<Guid, EffectiveContractData>
            {
                [_agentId] = new() { HasActiveContract = true, PerformsShiftWork = false },
            };
        }

        contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), From, Until, null)
            .Returns(contracts);

        return new ScenarioSummaryBuilder(
            _context, scenarioRepository, Substitute.For<IAnalyseScenarioService>(), contractProvider);
    }

    [Test]
    public async Task UnknownToken_ReturnsNull()
    {
        var sut = new ScenarioSummaryBuilder(
            _context, Substitute.For<IAnalyseScenarioRepository>(), Substitute.For<IAnalyseScenarioService>(),
            Substitute.For<IClientContractDataProvider>());

        (await sut.BuildAsync(Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task CountsDemandAndFilledSlotsPerShift()
    {
        var summary = await BuildSut().BuildAsync(_token, CancellationToken.None);

        summary.ShouldNotBeNull();
        summary!.DemandedSlots.ShouldBe(14);
        summary.FilledSlots.ShouldBe(1);
        summary.OpenSlots.ShouldBe(13);
        summary.WorkCount.ShouldBe(1);
        summary.AgentCount.ShouldBe(1);
        summary.Shifts.Single(s => s.ShiftId == _early.Id).FilledSlots.ShouldBe(1);
        summary.Shifts.Single(s => s.ShiftId == _late.Id).FilledSlots.ShouldBe(0);
    }

    [Test]
    public async Task NamesTheCauseOfEveryOpenSlot()
    {
        var summary = await BuildSut().BuildAsync(_token, CancellationToken.None);

        var reasons = summary!.OpenSlotReasons.ToDictionary(r => r.ReasonCode, r => r.SlotCount);
        reasons[ScenarioSummaryReasonCodes.NoAgentPerformsShiftWork].ShouldBe(5, "late shift on the five weekdays");
        reasons[ScenarioSummaryReasonCodes.NoAgentWorksOnWeekday].ShouldBe(4, "both shifts on Saturday and Sunday");
        reasons[ScenarioSummaryReasonCodes.CapacityOrRules].ShouldBe(4, "early shift on the remaining weekdays");
        summary.OpenSlotReasons.Sum(r => r.SlotCount).ShouldBe(summary.OpenSlots);
    }
}

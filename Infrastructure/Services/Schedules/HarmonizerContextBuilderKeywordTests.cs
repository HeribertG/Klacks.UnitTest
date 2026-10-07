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
/// K14 (2026-10-07): several schedule commands on one (agent, day) restrict cumulatively in Wizard 2, exactly as
/// Wizard 1 reads them. The builder used to keep only the last command per day (last wins).
/// </summary>
[TestFixture]
public class HarmonizerContextBuilderKeywordTests
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
    public async Task OnlyEarlyAndNoNight_RequiresEarly()
    {
        var availability = await AvailabilityWithCommandsAsync("EARLY", "-NIGHT");

        availability.RequiredSymbol.ShouldBe(CellSymbol.Early);
        availability.HasFreeCommand.ShouldBeFalse();
    }

    [Test]
    public async Task NoEarlyAndNoNight_RequiresLate()
    {
        var availability = await AvailabilityWithCommandsAsync("-EARLY", "-NIGHT");

        availability.RequiredSymbol.ShouldBe(CellSymbol.Late);
        availability.HasFreeCommand.ShouldBeFalse();
    }

    [Test]
    public async Task OnlyEarlyAndOnlyLate_ClosesTheDay()
    {
        var availability = await AvailabilityWithCommandsAsync("EARLY", "LATE");

        availability.HasFreeCommand.ShouldBeTrue("no shift kind satisfies both directives, so the day is closed");
        availability.IsAvailable.ShouldBeFalse();
    }

    [Test]
    public async Task NotFreeAndNoNight_KeepsNoNight()
    {
        var availability = await AvailabilityWithCommandsAsync("-FREE", "-NIGHT");

        availability.ForbiddenSymbol.ShouldBe(CellSymbol.Night);
        availability.RequiredSymbol.ShouldBeNull();
        availability.HasFreeCommand.ShouldBeFalse();
    }

    private async Task<DayAvailability> AvailabilityWithCommandsAsync(params string[] keywords)
    {
        var agent = Guid.NewGuid();
        StubContract(agent);
        foreach (var keyword in keywords)
        {
            _context.ScheduleCommands.Add(new ScheduleCommand
            {
                Id = Guid.NewGuid(),
                ClientId = agent,
                CurrentDate = WeekStart,
                CommandKeyword = keyword,
            });
        }
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(
            new HarmonizerContextRequest(WeekStart, WeekEnd, [agent], AnalyseToken: null), CancellationToken.None);

        return input.Availability![(agent.ToString(), WeekStart)];
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

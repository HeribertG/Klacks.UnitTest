// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The range API resolves a whole period in one pass instead of repeating the contract, revision and
/// settings queries for every day. That is only safe if it answers exactly like the per-day overload, so
/// every test here compares the two field by field across the whole range - with a contract change in
/// the middle of the period, a contract that starts and one that ends mid-period, a client without any
/// contract, a rate revision taking effect mid-period, and two overlapping contract rows where the
/// loser of the FromDate race is the one on the MonthlyTargetHours interval. The per-day reference is
/// always asked through a FRESH provider so no per-instance state can paper over a divergence.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientContractDataProviderRangeTests
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly Until = new(2026, 3, 31);

    private DbContextOptions<DataBaseContext> _options = null!;
    private DataBaseContext _context = null!;
    private ClientContractDataProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(_options, Substitute.For<IHttpContextAccessor>());
        _sut = new ClientContractDataProvider(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Range_ContractSwitchMidPeriod_MatchesThePerDayResult()
    {
        var clientId = Guid.NewGuid();
        var first = await AddContractAsync(nightRate: 0.10m);
        var second = await AddContractAsync(nightRate: 0.40m);
        await AddClientContractAsync(clientId, first, From.AddDays(-30), From.AddDays(14));
        await AddClientContractAsync(clientId, second, From.AddDays(15), null);

        await AssertParityAsync([clientId]);
    }

    [Test]
    public async Task Range_ContractStartsMidPeriod_MatchesThePerDayResult()
    {
        var clientId = Guid.NewGuid();
        var contract = await AddContractAsync(nightRate: 0.25m);
        await AddClientContractAsync(clientId, contract, From.AddDays(10), null);

        await AssertParityAsync([clientId]);
    }

    [Test]
    public async Task Range_ContractEndsMidPeriod_MatchesThePerDayResult()
    {
        var clientId = Guid.NewGuid();
        var contract = await AddContractAsync(nightRate: 0.25m);
        await AddClientContractAsync(clientId, contract, From.AddDays(-10), From.AddDays(12));

        await AssertParityAsync([clientId]);
    }

    [Test]
    public async Task Range_ClientWithoutAnyContract_MatchesThePerDayResult()
    {
        await AssertParityAsync([Guid.NewGuid()]);
    }

    [Test]
    public async Task Range_RateRevisionTakingEffectMidPeriod_MatchesThePerDayResult()
    {
        var clientId = Guid.NewGuid();
        var rule = new SchedulingRule { Id = Guid.NewGuid(), Name = "Rule", NightRate = 0.10m };
        _context.SchedulingRules.Add(rule);
        _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
        {
            Id = Guid.NewGuid(),
            SchedulingRuleId = rule.Id,
            ValidFrom = From.AddDays(16),
            NightRate = 0.55m,
        });

        var contract = await AddContractAsync(nightRate: null, schedulingRuleId: rule.Id);
        await AddClientContractAsync(clientId, contract, From.AddDays(-30), null);

        await AssertParityAsync([clientId]);
    }

    [Test]
    public async Task Range_MonthlyTargetHoursContractAcrossTwoMonths_MatchesThePerDayResult()
    {
        var clientId = Guid.NewGuid();
        var contract = await AddContractAsync(nightRate: 0.10m, paymentInterval: PaymentInterval.MonthlyTargetHours);
        await AddClientContractAsync(clientId, contract, From.AddDays(-30), null);

        _context.MonthlyTargetHours.Add(new MonthlyTargetHours { Id = Guid.NewGuid(), Year = 2026, Month = 3, Hours = 168 });
        _context.MonthlyTargetHours.Add(new MonthlyTargetHours { Id = Guid.NewGuid(), Year = 2026, Month = 4, Hours = 176 });
        await _context.SaveChangesAsync();

        await AssertParityAsync([clientId], From, Until.AddDays(15));
    }

    [Test]
    public async Task Range_SeveralClientsWithDifferentContracts_MatchThePerDayResult()
    {
        var withContract = Guid.NewGuid();
        var withoutContract = Guid.NewGuid();
        var contract = await AddContractAsync(nightRate: 0.30m);
        await AddClientContractAsync(withContract, contract, From.AddDays(5), null);

        await AssertParityAsync([withContract, withoutContract]);
    }

    [Test]
    public async Task Range_OverlappingContractRows_PicksTheSameWinnerAsThePerDayPath()
    {
        // Two rows are active on the same days; the later FromDate wins. The LOSING row is the one on the
        // MonthlyTargetHours interval, so a gate that looks at every active row instead of the winner
        // would apply the monthly override where the per-day path does not.
        var clientId = Guid.NewGuid();
        var loser = await AddContractAsync(nightRate: 0.10m, paymentInterval: PaymentInterval.MonthlyTargetHours);
        var winner = await AddContractAsync(nightRate: 0.45m);
        await AddClientContractAsync(clientId, loser, From.AddDays(-30), null);
        await AddClientContractAsync(clientId, winner, From.AddDays(-5), null);

        _context.MonthlyTargetHours.Add(new MonthlyTargetHours { Id = Guid.NewGuid(), Year = 2026, Month = 3, Hours = 168 });
        await _context.SaveChangesAsync();

        await AssertParityAsync([clientId]);
    }

    [Test]
    public async Task Range_FromAfterUntil_ReturnsNothing()
    {
        var result = await _sut.GetEffectiveContractDataForClientsRangeAsync([Guid.NewGuid()], Until, From);

        result.ShouldBeEmpty();
    }

    private async Task AssertParityAsync(List<Guid> clientIds, DateOnly? from = null, DateOnly? until = null)
    {
        var rangeFrom = from ?? From;
        var rangeUntil = until ?? Until;

        var range = await _sut.GetEffectiveContractDataForClientsRangeAsync(clientIds, rangeFrom, rangeUntil);

        for (var date = rangeFrom; date <= rangeUntil; date = date.AddDays(1))
        {
            using var referenceContext = new DataBaseContext(_options, Substitute.For<IHttpContextAccessor>());
            var reference = await new ClientContractDataProvider(referenceContext)
                .GetEffectiveContractDataForClientsAsync(clientIds, date);

            range.ShouldContainKey(date);

            foreach (var clientId in clientIds)
            {
                var actual = range[date][clientId];
                var expected = reference[clientId];

                actual.HasActiveContract.ShouldBe(expected.HasActiveContract, $"HasActiveContract on {date:yyyy-MM-dd}");
                actual.ContractId.ShouldBe(expected.ContractId, $"ContractId on {date:yyyy-MM-dd}");
                actual.NightRate.ShouldBe(expected.NightRate, $"NightRate on {date:yyyy-MM-dd}");
                actual.HolidayRate.ShouldBe(expected.HolidayRate, $"HolidayRate on {date:yyyy-MM-dd}");
                actual.WE1Rate.ShouldBe(expected.WE1Rate, $"WE1Rate on {date:yyyy-MM-dd}");
                actual.WE2Rate.ShouldBe(expected.WE2Rate, $"WE2Rate on {date:yyyy-MM-dd}");
                actual.GuaranteedHours.ShouldBe(expected.GuaranteedHours, $"GuaranteedHours on {date:yyyy-MM-dd}");
                actual.FullTime.ShouldBe(expected.FullTime, $"FullTime on {date:yyyy-MM-dd}");
                actual.MaximumHours.ShouldBe(expected.MaximumHours, $"MaximumHours on {date:yyyy-MM-dd}");
                actual.MinimumHours.ShouldBe(expected.MinimumHours, $"MinimumHours on {date:yyyy-MM-dd}");
                actual.PaymentInterval.ShouldBe(expected.PaymentInterval, $"PaymentInterval on {date:yyyy-MM-dd}");
                actual.PerformsShiftWork.ShouldBe(expected.PerformsShiftWork, $"PerformsShiftWork on {date:yyyy-MM-dd}");
                actual.WorkOnMonday.ShouldBe(expected.WorkOnMonday, $"WorkOnMonday on {date:yyyy-MM-dd}");
                actual.WorkOnSaturday.ShouldBe(expected.WorkOnSaturday, $"WorkOnSaturday on {date:yyyy-MM-dd}");
                actual.WorkOnSunday.ShouldBe(expected.WorkOnSunday, $"WorkOnSunday on {date:yyyy-MM-dd}");
                actual.MaxDailyHours.ShouldBe(expected.MaxDailyHours, $"MaxDailyHours on {date:yyyy-MM-dd}");
                actual.MaxWeeklyHours.ShouldBe(expected.MaxWeeklyHours, $"MaxWeeklyHours on {date:yyyy-MM-dd}");
                actual.MaxConsecutiveDays.ShouldBe(expected.MaxConsecutiveDays, $"MaxConsecutiveDays on {date:yyyy-MM-dd}");
            }
        }
    }

    private async Task<Guid> AddContractAsync(
        decimal? nightRate,
        Guid? schedulingRuleId = null,
        PaymentInterval paymentInterval = PaymentInterval.Monthly)
    {
        var ruleId = schedulingRuleId;
        if (ruleId is null && nightRate.HasValue)
        {
            var rule = new SchedulingRule { Id = Guid.NewGuid(), Name = "Rule", NightRate = nightRate.Value };
            _context.SchedulingRules.Add(rule);
            ruleId = rule.Id;
        }

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PaymentInterval = paymentInterval,
            ValidFrom = new DateTime(2020, 1, 1),
            SchedulingRuleId = ruleId,
        };
        _context.Contract.Add(contract);
        await _context.SaveChangesAsync();
        return contract.Id;
    }

    private async Task AddClientContractAsync(Guid clientId, Guid contractId, DateOnly fromDate, DateOnly? untilDate)
    {
        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contractId,
            FromDate = fromDate,
            UntilDate = untilDate,
            IsActive = true,
        });
        await _context.SaveChangesAsync();
    }
}

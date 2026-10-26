// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the view-interval filter of ClientContractDataProvider after the Weg-1 fix (owner decision 2026-10-25): a
/// Monthly view resolves a MonthlyTargetHours contract and vice versa, other intervals still filter, and a client
/// with a Monthly and a MonthlyTargetHours assignment from the same day resolves deterministically (latest created,
/// then id) on the per-day, batch and range paths alike.
/// </summary>

using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

[TestFixture]
public class ClientContractDataProviderViewIntervalTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);
    private static readonly DateOnly Start = new(2020, 1, 1);
    private const int CreateTimeGapMilliseconds = 20;

    private DataBaseContext _context = null!;
    private ClientContractDataProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance, new SettingsChangeVersion());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(PaymentInterval.MonthlyTargetHours, PaymentInterval.Monthly)]
    [TestCase(PaymentInterval.Monthly, PaymentInterval.MonthlyTargetHours)]
    public async Task CalendarMonthContract_IsResolvedByTheOtherCalendarMonthView(PaymentInterval contractInterval, PaymentInterval view)
    {
        var clientId = Guid.NewGuid();
        var contract = await SeedAsync(clientId, contractInterval, 160m, Start);

        var single = await _sut.GetEffectiveContractDataAsync(clientId, Day, (int)view);
        var batch = await _sut.GetEffectiveContractDataForClientsAsync([clientId], Day, (int)view);
        var range = await _sut.GetEffectiveContractDataForClientsRangeAsync([clientId], Day, Day, (int)view);

        single.ContractId.ShouldBe(contract.Id);
        batch[clientId].ContractId.ShouldBe(contract.Id);
        range[Day][clientId].ContractId.ShouldBe(contract.Id);
    }

    [Test]
    public async Task WeeklyContract_IsStillFilteredOutOfAMonthlyView()
    {
        var clientId = Guid.NewGuid();
        await SeedAsync(clientId, PaymentInterval.Weekly, 40m, Start);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, Day, (int)PaymentInterval.Monthly);

        result.HasActiveContract.ShouldBeFalse();
    }

    [Test]
    public async Task MonthlyAndMonthlyTargetHoursFromTheSameDay_TheLaterCreatedWins_OnEveryPath()
    {
        var clientId = Guid.NewGuid();
        var older = await SeedAsync(clientId, PaymentInterval.Monthly, 160m, Start);
        await Task.Delay(TimeSpan.FromMilliseconds(CreateTimeGapMilliseconds));
        var newer = await SeedAsync(clientId, PaymentInterval.MonthlyTargetHours, 150m, Start);

        var single = await _sut.GetEffectiveContractDataAsync(clientId, Day, (int)PaymentInterval.Monthly);
        var batch = await _sut.GetEffectiveContractDataForClientsAsync([clientId], Day, (int)PaymentInterval.Monthly);
        var range = await _sut.GetEffectiveContractDataForClientsRangeAsync([clientId], Day, Day, (int)PaymentInterval.Monthly);

        single.ContractId.ShouldBe(newer.Id);
        batch[clientId].ContractId.ShouldBe(newer.Id);
        range[Day][clientId].ContractId.ShouldBe(newer.Id);
        older.Id.ShouldNotBe(newer.Id);
    }

    [Test]
    public async Task LaterFromDate_StillBeatsALaterCreateTime()
    {
        var clientId = Guid.NewGuid();
        var laterStart = await SeedAsync(clientId, PaymentInterval.MonthlyTargetHours, 150m, Start.AddYears(1));
        await Task.Delay(TimeSpan.FromMilliseconds(CreateTimeGapMilliseconds));
        var earlierStart = await SeedAsync(clientId, PaymentInterval.Monthly, 160m, Start);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, Day, (int)PaymentInterval.Monthly);

        result.ContractId.ShouldBe(laterStart.Id);
        earlierStart.Id.ShouldNotBe(laterStart.Id);
    }

    // The context stamps CreateTime on insert, so a later insert is the later-created assignment.
    private async Task<Contract> SeedAsync(
        Guid clientId, PaymentInterval interval, decimal guaranteedHours, DateOnly fromDate)
    {
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = $"{interval} {guaranteedHours}",
            PaymentInterval = interval,
            GuaranteedHours = guaranteedHours,
            FullTime = 180m,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _context.Contract.Add(contract);
        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contract.Id,
            FromDate = fromDate,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        return contract;
    }
}

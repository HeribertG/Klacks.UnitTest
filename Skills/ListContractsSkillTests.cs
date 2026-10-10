// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the canton filter of list_contracts, which resolves the canton to calendar selections through
/// ContractRegionCalendarResolver: the canton is upper-cased and looked up in the installation's default
/// country, matching contracts are kept, and an unknown canton leaves the list unfiltered.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListContractsSkillTests
{
    private static readonly ICompanyClock CompanyClock =
        new FixedCompanyClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string>()
    };

    private static Contract MakeContract(string name, Guid? calendarId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CalendarSelectionId = calendarId,
        ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static (ListContractsSkill Skill, ICalendarSelectionRepository Calendars) Create(
        IEnumerable<Contract> contracts, IEnumerable<Guid> idsForState)
    {
        var contractRepository = Substitute.For<IContractRepository>();
        contractRepository.List().Returns(contracts.ToList());

        var calendars = Substitute.For<ICalendarSelectionRepository>();
        calendars.GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(idsForState.ToList());

        var countryResolver = Substitute.For<ICountryResolver>();
        countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(new Countries { Abbreviation = "CH" });

        return (new ListContractsSkill(contractRepository, calendars, countryResolver, CompanyClock), calendars);
    }

    private static List<string> Names(SkillResult result)
    {
        var contracts = result.Data!.GetType().GetProperty("Contracts")!.GetValue(result.Data)!;
        return ((System.Collections.IEnumerable)contracts)
            .Cast<object>()
            .Select(item => (string)item.GetType().GetProperty("Name")!.GetValue(item)!)
            .ToList();
    }

    [Test]
    public async Task Canton_KeepsOnlyContractsWithAMatchingCalendar_AndLooksItUpUpperCasedInTheDefaultCountry()
    {
        var zurich = Guid.NewGuid();
        var (skill, calendars) = Create(
            [MakeContract("Zurich", zurich), MakeContract("Bern", Guid.NewGuid()), MakeContract("No calendar", null)],
            [zurich]);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["canton"] = " zh " });

        result.Success.ShouldBeTrue(result.Message);
        Names(result).ShouldBe(new[] { "Zurich" });
        await calendars.Received(1).GetIdsByStateAsync("CH", "ZH", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Canton_WithoutAnyMatchingCalendar_LeavesTheListUnfiltered()
    {
        var (skill, _) = Create([MakeContract("A", Guid.NewGuid()), MakeContract("B", null)], []);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["canton"] = "XX" });

        Names(result).ShouldBe(new[] { "A", "B" });
    }

    [Test]
    public async Task WithoutCanton_NoCalendarLookupHappens()
    {
        var (skill, calendars) = Create([MakeContract("A", null)], []);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        await calendars.DidNotReceive().GetIdsByStateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text;
using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.DTOs.Seed;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Data.Seed.Demo;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Persistence.Seed.Demo;
using Klacks.Api.Infrastructure.Services.Imports;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class DemoOrderXmlGeneratorTests
{
    private const string Language = "de";

    private static readonly DateTime FixedNowUtc = new(2026, 9, 17, 14, 32, 0, DateTimeKind.Utc);

    private static readonly int ExpectedOrderCount =
        (DemoOrderDefinitionFactory.SimpleShiftCount
         + DemoOrderDefinitionFactory.MorningShiftCount
         + DemoOrderDefinitionFactory.DayShiftCount
         + DemoOrderDefinitionFactory.NightShiftWeekdayCount
         + DemoOrderDefinitionFactory.NightShiftWeekendCount
         + DemoOrderDefinitionFactory.TwentyFourHourShiftCount
         + DemoOrderDefinitionFactory.NightCutShiftCount)
        + (DemoOrderDefinitionFactory.TimeRangeShiftsPerRootGroup * DemoOrderDefinitionFactory.RootGroups.Count);

    private static readonly IReadOnlyList<DemoOrderCustomer> Customers =
    [
        new() { IdNumber = 11, Company = "Digital Enterprises AG", Street = "Storchenstrasse 73", Zip = "8103", City = "Unterengstringen", State = "ZH", Country = "CH" },
        new() { IdNumber = 12, Company = "Tech Systems GmbH", Street = "Augustinergasse 59", Zip = "4056", City = "Basel", State = "BS", Country = "CH" },
        new() { IdNumber = 13, Company = "Digital Technologies AG", Street = "Schuetzenmattstrasse 88", Zip = "3000", City = "Bern", State = "BE", Country = "CH" }
    ];

    [Test]
    public void Generate_ParsedByTheRealParser_YieldsEveryOrderWithoutValidationError()
    {
        var result = ParseGenerated();

        result.Errors.ShouldBeEmpty(DescribeErrors(result));
        result.SchemaVersion.ShouldBe(ErpImportXmlElements.CurrentSchemaVersion);
        result.Orders.Count.ShouldBe(ExpectedOrderCount);
        result.Orders.ShouldAllBe(o => o.SourceSystemId == DemoOrderSeedConstants.SourceSystemId);
    }

    [Test]
    public void Generate_DayShiftOrders_KeepTheSeededWorkTimeInsteadOfTheClockDistance()
    {
        var result = ParseGenerated();

        var dayShifts = result.Orders
            .Where(o => o.StartTime == new TimeOnly(8, 0) && o.EndTime == new TimeOnly(17, 0) && !o.IsTimeRange)
            .ToList();

        dayShifts.Count.ShouldBe(DemoOrderDefinitionFactory.DayShiftCount);
        dayShifts.ShouldAllBe(o => o.DurationMinutes == 480);
        dayShifts.ShouldAllBe(o => ImportedOrderShiftMapper.ResolveWorkTimeHours(o) == 8m);
        dayShifts.ShouldAllBe(o => o.IsMonday && o.IsTuesday && o.IsWednesday && o.IsThursday && o.IsFriday && !o.IsSaturday && !o.IsSunday);
    }

    [Test]
    public void Generate_TwentyFourHourOrders_ResolveToTwentyFourHoursDespiteEqualStartAndEnd()
    {
        var result = ParseGenerated();

        var fullDay = result.Orders
            .Where(o => o.StartTime == new TimeOnly(7, 0) && o.EndTime == new TimeOnly(7, 0))
            .ToList();

        fullDay.Count.ShouldBe(DemoOrderDefinitionFactory.TwentyFourHourShiftCount);
        fullDay.ShouldAllBe(o => ImportedOrderShiftMapper.ResolveWorkTimeHours(o) == 24m);
        fullDay.ShouldAllBe(o => o.IsSaturday && o.IsSunday);
    }

    [Test]
    public void Generate_WeekendNightOrders_CarryOnlySaturdayAndSunday()
    {
        var result = ParseGenerated();

        var weekendNights = result.Orders
            .Where(o => o.IsSaturday && o.IsSunday && !o.IsMonday && !o.IsTuesday && !o.IsWednesday && !o.IsThursday && !o.IsFriday)
            .ToList();

        weekendNights.Count.ShouldBe(DemoOrderDefinitionFactory.NightShiftWeekendCount);
        weekendNights.ShouldAllBe(o => o.StartTime == new TimeOnly(23, 0) && o.EndTime == new TimeOnly(7, 0));
    }

    [Test]
    public void Generate_TimeRangeOrders_AreCountedAndAlwaysCarryADuration()
    {
        var result = ParseGenerated();

        var timeRanges = result.Orders.Where(o => o.IsTimeRange).ToList();

        timeRanges.Count.ShouldBe(
            (DemoOrderDefinitionFactory.TimeRangeShiftsPerRootGroup * DemoOrderDefinitionFactory.RootGroups.Count) + 2);
        timeRanges.ShouldAllBe(o => o.DurationMinutes != null && o.DurationMinutes > 0);
    }

    [Test]
    public void Generate_OrderReferences_AreUniqueAndStableAcrossRuns()
    {
        var first = ParseGenerated().Orders.Select(o => o.ExternalOrderReference).ToList();
        var second = ParseGenerated().Orders.Select(o => o.ExternalOrderReference).ToList();

        first.Distinct().Count().ShouldBe(first.Count);
        first.ShouldBe(second);
        first[0].ShouldBe($"{DemoOrderSeedConstants.OrderReferencePrefix}0001");
        first[^1].ShouldBe($"{DemoOrderSeedConstants.OrderReferencePrefix}{ExpectedOrderCount:D4}");
    }

    [Test]
    public void Generate_EveryCustomerBlock_CarriesTheKeyTheResolverReusesCustomersBy()
    {
        var result = ParseGenerated();

        result.Orders.ShouldAllBe(o => o.Customer.Company != string.Empty);
        result.Orders.ShouldAllBe(o => o.Customer.Street != string.Empty);
        result.Orders.ShouldAllBe(o => o.Customer.Zip != string.Empty);
        result.Orders.Select(o => o.Customer.Company).Distinct().Count().ShouldBe(Customers.Count);

        for (var i = 0; i < result.Orders.Count; i++)
        {
            var expected = Customers[i % Customers.Count];
            result.Orders[i].Customer.Company.ShouldBe(expected.Company);
            result.Orders[i].Customer.Zip.ShouldBe(expected.Zip);
            result.Orders[i].Customer.Street.ShouldBe(expected.Street);
            result.Orders[i].Customer.ExternalCustomerReference
                .ShouldBe($"{DemoOrderSeedConstants.CustomerReferencePrefix}{expected.IdNumber:D5}");
        }
    }

    [Test]
    public void Generate_WithoutCustomers_IsRejected()
    {
        var generator = new DemoOrderXmlGenerator(new SettableTimeProvider(FixedNowUtc));

        Should.Throw<ArgumentException>(() => generator.Generate([], Language));
    }

    [Test]
    public void Generate_TwoRuns_ProduceByteIdenticalDocuments()
    {
        GenerateXml().ShouldBe(GenerateXml());
    }

    [Test]
    public void Generate_OrderStartDate_IsTheFirstDayOfTheMonthOfTheInjectedClock()
    {
        var result = ParseGenerated();

        result.Orders.ShouldAllBe(o => o.FromDate == new DateOnly(2026, 9, 1));
        result.Orders.ShouldAllBe(o => o.UntilDate == null);
    }

    [Test]
    public void Generate_OnAnotherMonth_MovesEveryOrderStartWithTheClock()
    {
        var result = ParseGenerated(new DateTime(2027, 1, 31, 23, 59, 0, DateTimeKind.Utc));

        result.Orders.ShouldAllBe(o => o.FromDate == new DateOnly(2027, 1, 1));
    }

    private static string GenerateXml(DateTime? nowUtc = null)
    {
        var timeProvider = new SettableTimeProvider(nowUtc ?? FixedNowUtc);
        return new DemoOrderXmlGenerator(timeProvider).Generate(Customers, Language);
    }

    private static OrderImportParseResult ParseGenerated(DateTime? nowUtc = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(GenerateXml(nowUtc)));
        return new XmlOrderImportParser().Parse(stream);
    }

    private static string DescribeErrors(OrderImportParseResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Errors.Take(10).Select(e => $"{e.ExternalOrderReference} {e.Field}: {e.Message}"));
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

/// <summary>
/// OrderLocationAddressRule (owner decision 2026-10-24): an order takes place at the customer's workplace address, else
/// the main address, never the invoicing address; the newest address of a type wins. Select serves the group planners,
/// LocatedAt the order location filter (checked here in memory; the database translation is pinned by
/// OrderLocationSearchPostgresTests).
/// </summary>
[TestFixture]
public class OrderLocationAddressRuleTests
{
    [Test]
    public void Select_PrefersTheWorkplace_OverTheMainAddress()
    {
        var main = Addr(AddressTypeEnum.Employee, "Bern");
        var workplace = Addr(AddressTypeEnum.Workplace, "Thun");

        OrderLocationAddressRule.Select([main, workplace], OrderLocationAddressRule.HasPlaceData).ShouldBe(workplace);
    }

    [Test]
    public void Select_FallsBackToTheMainAddress_WhenNoWorkplaceIsUsable()
    {
        var main = Addr(AddressTypeEnum.Employee, "Bern");
        var emptyWorkplace = Addr(AddressTypeEnum.Workplace, string.Empty);

        OrderLocationAddressRule.Select([emptyWorkplace, main], OrderLocationAddressRule.HasPlaceData).ShouldBe(main);
    }

    [Test]
    public void Select_NeverUsesTheInvoicingAddress()
    {
        var invoicing = Addr(AddressTypeEnum.InvoicingAddress, "Bern");

        OrderLocationAddressRule.Select([invoicing], OrderLocationAddressRule.HasPlaceData).ShouldBeNull();
    }

    [Test]
    public void Select_TakesTheNewestAddressOfAType_AndSkipsDeletedOnes()
    {
        var older = Addr(AddressTypeEnum.Workplace, "Biel", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Addr(AddressTypeEnum.Workplace, "Thun", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newestDeleted = Addr(AddressTypeEnum.Workplace, "Spiez", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        newestDeleted.IsDeleted = true;

        OrderLocationAddressRule.Select([older, newer, newestDeleted], OrderLocationAddressRule.HasPlaceData).ShouldBe(newer);
    }

    [TestCase("Thun", true)]
    [TestCase("Bern", false)]
    public void LocatedAt_MatchesOnlyTheWorkplace_WhenTheCustomerHasOne(string city, bool expected)
    {
        var order = Order(Addr(AddressTypeEnum.Employee, "Bern"), Addr(AddressTypeEnum.Workplace, "Thun"));

        IsLocatedAt(order, City(city)).ShouldBe(expected);
    }

    [Test]
    public void LocatedAt_UsesTheMainAddress_WithoutWorkplace_AndNeverTheInvoicingAddress()
    {
        IsLocatedAt(Order(Addr(AddressTypeEnum.Employee, "Bern")), City("Bern")).ShouldBeTrue();
        IsLocatedAt(Order(Addr(AddressTypeEnum.InvoicingAddress, "Bern")), City("Bern")).ShouldBeFalse();
        IsLocatedAt(Order(Addr(AddressTypeEnum.Employee, "Thun"), Addr(AddressTypeEnum.InvoicingAddress, "Bern")), City("Bern"))
            .ShouldBeFalse();
    }

    [Test]
    public void LocatedAt_AnOrderWithoutCustomer_IsNowhere()
    {
        IsLocatedAt(new Shift { Id = Guid.NewGuid() }, City("Bern")).ShouldBeFalse();
    }

    private static AddressLocationCriteria City(string city) => new(null, [], city, null);

    private static bool IsLocatedAt(Shift order, AddressLocationCriteria criteria)
        => OrderLocationAddressRule.LocatedAt(criteria).Compile()(order);

    private static Shift Order(params Address[] addresses) => new()
    {
        Id = Guid.NewGuid(),
        Client = new Client { Id = Guid.NewGuid(), Addresses = addresses.ToList() }
    };

    private static Address Addr(AddressTypeEnum type, string city, DateTime? validFrom = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        City = city,
        ValidFrom = validFrom ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };
}

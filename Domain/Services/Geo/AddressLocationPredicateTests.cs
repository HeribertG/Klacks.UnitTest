// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Services.Geo;

namespace Klacks.UnitTest.Domain.Services.Geo;

/// <summary>
/// AddressLocationPredicate in memory: every set criterion must hold on the same, non-deleted address; a country-bound
/// state accepts its country and addresses without a country, never another country with the same code; city is
/// compared case-insensitively, the zip by prefix. The database translation is pinned by OrderLocationSearchPostgresTests.
/// </summary>
[TestFixture]
public class AddressLocationPredicateTests
{
    [Test]
    public void BoundState_AcceptsItsCountryAndAddressesWithoutCountry_NotTheOtherCountry()
    {
        var bern = new AddressLocationCriteria(null, [new StateCodeMatch("BE", "CH")], null, null);

        Matches(bern, Addr(state: "BE", country: "CH")).ShouldBeTrue();
        Matches(bern, Addr(state: "be", country: "")).ShouldBeTrue();
        Matches(bern, Addr(state: "BE", country: "DE")).ShouldBeFalse();
    }

    [Test]
    public void UnboundState_AcceptsEveryCountry()
    {
        var anyBe = AddressLocationCriteria.ForStateCode("BE", null, null);

        Matches(anyBe, Addr(state: "BE", country: "DE")).ShouldBeTrue();
        Matches(anyBe, Addr(state: "ZH", country: "CH")).ShouldBeFalse();
    }

    [Test]
    public void Country_City_AndZipPrefix_MustAllHoldOnTheAddress()
    {
        var criteria = new AddressLocationCriteria("ch", [], "bern", "30");

        Matches(criteria, Addr(city: "Bern", zip: "3011", country: "CH")).ShouldBeTrue();
        Matches(criteria, Addr(city: "Bern", zip: "3011", country: "DE")).ShouldBeFalse();
        Matches(criteria, Addr(city: "Bern", zip: "8001", country: "CH")).ShouldBeFalse();
        Matches(criteria, Addr(city: "Bern-Bümpliz", zip: "3018", country: "CH")).ShouldBeFalse();
    }

    [Test]
    public void CountryList_AcceptsAnyListedCountry_CaseInsensitively()
    {
        var criteria = new AddressLocationCriteria(null, [], null, null, ["ch", "DE"]);

        criteria.IsEmpty.ShouldBeFalse();
        Matches(criteria, Addr(city: "Bern", country: "CH")).ShouldBeTrue();
        Matches(criteria, Addr(city: "Berlin", country: "de")).ShouldBeTrue();
        Matches(criteria, Addr(city: "Paris", country: "FR")).ShouldBeFalse();
        Matches(criteria, Addr(city: "Nowhere", country: "")).ShouldBeFalse();
    }

    [Test]
    public void CountryList_AndBoundStatePairs_RejectTheSameCodeOfAnotherCountry()
    {
        var criteria = new AddressLocationCriteria(
            null, [new StateCodeMatch("BE", "CH"), new StateCodeMatch("ZH", "CH")], null, null, ["CH", "DE"]);

        Matches(criteria, Addr(state: "BE", country: "CH")).ShouldBeTrue();
        Matches(criteria, Addr(state: "ZH", country: "CH")).ShouldBeTrue();
        Matches(criteria, Addr(state: "BE", country: "DE")).ShouldBeFalse();
        Matches(criteria, Addr(state: "TI", country: "CH")).ShouldBeFalse();
    }

    [Test]
    public void CountryList_IsCombinedWithTheSingleCountry_AndEmptyListMeansAnyCountry()
    {
        Matches(new AddressLocationCriteria("CH", [], null, null, ["DE"]), Addr(city: "Bern", country: "CH")).ShouldBeFalse();
        Matches(new AddressLocationCriteria(null, [], "Bern", null, []), Addr(city: "Bern", country: "FR")).ShouldBeTrue();
        new AddressLocationCriteria(null, [], null, null, []).IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void DeletedAddress_NeverMatches()
    {
        var address = Addr(city: "Bern");
        address.IsDeleted = true;

        Matches(new AddressLocationCriteria(null, [], "Bern", null), address).ShouldBeFalse();
    }

    [Test]
    public void EmptyCriteria_MatchEveryLiveAddress()
    {
        AddressLocationCriteria.None.IsEmpty.ShouldBeTrue();
        Matches(AddressLocationCriteria.None, Addr(city: "Anywhere")).ShouldBeTrue();
    }

    private static bool Matches(AddressLocationCriteria criteria, Address address)
        => AddressLocationPredicate.Matches(criteria).Compile()(address);

    private static Address Addr(string city = "", string zip = "", string state = "", string country = "") => new()
    {
        Id = Guid.NewGuid(),
        City = city,
        Zip = zip,
        State = state,
        Country = country
    };
}

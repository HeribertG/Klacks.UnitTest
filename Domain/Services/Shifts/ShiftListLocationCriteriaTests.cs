// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

/// <summary>
/// The country and state lists of the shift list filter are merged into the criteria resolved from the text fields:
/// codes trimmed/upper-cased/de-duplicated, states country-bound, blanks dropped, empty lists change nothing.
/// </summary>
[TestFixture]
public class ShiftListLocationCriteriaTests
{
    [Test]
    public void EmptyOrMissingLists_LeaveTheCriteriaUnchanged()
    {
        var resolved = new AddressLocationCriteria("CH", [new StateCodeMatch("BE", "CH")], "Bern", "30");

        ShiftListLocationCriteria.Extend(resolved, new ShiftFilter()).ShouldBeSameAs(resolved);
        ShiftListLocationCriteria.Extend(resolved, new ShiftFilter { Countries = [], States = [] }).ShouldBeSameAs(resolved);
    }

    [Test]
    public void Countries_AreNormalized_AndBlanksDropped()
    {
        var filter = new ShiftFilter { Countries = [" ch ", "DE", "ch", "  ", ""] };

        var criteria = ShiftListLocationCriteria.Extend(AddressLocationCriteria.None, filter);

        criteria.CountryCodes.ShouldBe(["CH", "DE"]);
        criteria.IsEmpty.ShouldBeFalse();
    }

    [Test]
    public void States_BecomeCountryBoundPairs_AndAreAddedToTheResolvedTextStates()
    {
        var resolved = new AddressLocationCriteria(null, [new StateCodeMatch("ZH", "CH")], null, null);
        var filter = new ShiftFilter
        {
            States =
            [
                new ShiftFilterState { State = " BE ", Country = "ch" },
                new ShiftFilterState { State = "BE", Country = "DE" },
                new ShiftFilterState { State = "01", Country = "" },
                new ShiftFilterState { State = " ", Country = "CH" }
            ]
        };

        var criteria = ShiftListLocationCriteria.Extend(resolved, filter);

        criteria.States.ShouldBe(
        [
            new StateCodeMatch("ZH", "CH"),
            new StateCodeMatch("BE", "ch"),
            new StateCodeMatch("BE", "DE"),
            new StateCodeMatch("01", null)
        ]);
    }

    [Test]
    public void TextCountry_IsKept_AlongsideTheCountryList()
    {
        var resolved = new AddressLocationCriteria("CH", [], null, null);

        var criteria = ShiftListLocationCriteria.Extend(resolved, new ShiftFilter { Countries = ["CH", "DE"] });

        criteria.CountryCode.ShouldBe("CH");
        criteria.CountryCodes.ShouldBe(["CH", "DE"]);
    }
}

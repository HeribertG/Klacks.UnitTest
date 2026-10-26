// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Services.Geo;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Geo;

/// <summary>
/// StateCodeMatcher: a state is found by code or by any language variant of its name, case-insensitively; every match
/// keeps its country because codes repeat across countries; a country narrows the matches; unknown text is kept as typed.
/// </summary>
[TestFixture]
public class StateCodeMatcherTests
{
    private static readonly State[] States = TestAddressLocationResolver.SwissAndGermanStates();

    [TestCase("Bern")]
    [TestCase("berne")]
    [TestCase("Berna")]
    public void Name_InAnyLanguage_ResolvesToTheCodeAndItsCountry(string text)
    {
        StateCodeMatcher.Match(States, text, null).ShouldBe([new StateCodeMatch("BE", "CH")]);
    }

    [Test]
    public void SharedCode_WithoutCountry_KeepsEveryCountry()
    {
        StateCodeMatcher.Match(States, "be", null)
            .ShouldBe([new StateCodeMatch("BE", "CH"), new StateCodeMatch("BE", "DE")], ignoreOrder: true);
    }

    [Test]
    public void SharedCode_WithCountry_KeepsOnlyThatCountry()
    {
        StateCodeMatcher.Match(States, "BE", "DE").ShouldBe([new StateCodeMatch("BE", "DE")]);
    }

    [Test]
    public void Name_OfAnotherCountry_IsNotMatchedWhenACountryIsGiven()
    {
        StateCodeMatcher.Match(States, "Berlin", "CH").ShouldBe([new StateCodeMatch("Berlin", "CH")]);
    }

    [Test]
    public void UnknownText_IsKeptAsTyped()
    {
        StateCodeMatcher.Match(States, "  Vaud ", null).ShouldBe([new StateCodeMatch("Vaud", null)]);
    }

    [Test]
    public void DeletedStates_AreIgnored()
    {
        var deleted = TestAddressLocationResolver.State("TI", "CH", "Tessin", "Ticino", "Tessin", "Ticino");
        deleted.IsDeleted = true;

        StateCodeMatcher.Match([deleted], "Tessin", null).ShouldBe([new StateCodeMatch("Tessin", null)]);
    }
}

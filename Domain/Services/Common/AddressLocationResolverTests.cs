// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Services.Common;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Common;

/// <summary>
/// AddressLocationResolver: a country name resolves to its code through ICountryResolver (unknown text stays as typed),
/// the state to every (code, country) pair narrowed by that country; city and zip prefix are only trimmed. The bare
/// state code for the client search keeps the old fill_group_by_criteria order: code first, then names, else as typed.
/// </summary>
[TestFixture]
public class AddressLocationResolverTests
{
    private IStateRepository _states = null!;
    private ICountryResolver _countries = null!;
    private AddressLocationResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _states = Substitute.For<IStateRepository>();
        _states.List().Returns(TestAddressLocationResolver.SwissAndGermanStates().ToList());
        _countries = Substitute.For<ICountryResolver>();
        _countries.ResolveAsync("Schweiz", Arg.Any<CancellationToken>())
            .Returns(new Countries { Abbreviation = "CH", Name = new MultiLanguage { De = "Schweiz" } });
        _sut = new AddressLocationResolver(_states, _countries);
    }

    [Test]
    public async Task CountryName_NarrowsTheSharedStateCode_ToThatCountry()
    {
        var criteria = await _sut.ResolveAsync(" Schweiz ", "BE", " Bern ", " 30 ");

        criteria.CountryCode.ShouldBe("CH");
        criteria.States.ShouldBe([new StateCodeMatch("BE", "CH")]);
        criteria.City.ShouldBe("Bern");
        criteria.ZipPrefix.ShouldBe("30");
    }

    [Test]
    public async Task UnknownCountry_IsKeptAsTyped()
    {
        var criteria = await _sut.ResolveAsync("Atlantis", null, null, null);

        criteria.CountryCode.ShouldBe("Atlantis");
        criteria.States.ShouldBeEmpty();
    }

    [Test]
    public async Task BlankInput_YieldsEmptyCriteria_WithoutReadingTheStates()
    {
        var criteria = await _sut.ResolveAsync(" ", null, "", null);

        criteria.IsEmpty.ShouldBeTrue();
        await _states.DidNotReceive().List();
    }

    [TestCase("Berne", "BE")]
    [TestCase("zurigo", "ZH")]
    [TestCase("ti", "TI")]
    [TestCase(" Vaud ", "Vaud")]
    public async Task StateCode_ForTheClientSearch(string text, string expected)
    {
        (await _sut.ResolveStateCodeAsync(text)).ShouldBe(expected);
    }

    [Test]
    public async Task StateCode_OfBlankInput_IsNull()
    {
        (await _sut.ResolveStateCodeAsync("  ")).ShouldBeNull();
    }
}

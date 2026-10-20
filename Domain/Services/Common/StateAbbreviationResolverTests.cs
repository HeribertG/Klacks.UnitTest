// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Common;

namespace Klacks.UnitTest.Domain.Services.Common;

[TestFixture]
public class StateAbbreviationResolverTests
{
    private StateAbbreviationResolver _resolver = null!;

    [SetUp]
    public void Setup()
    {
        var repository = Substitute.For<IStateRepository>();
        repository.List().Returns(new List<State>
        {
            State("BE", "CH", "Bern", "Berne"),
            State("ZH", "CH", "Zürich", "Zurich"),
            State("59", "FR", "Nord", "Nord"),
            State("75", "FR", "Paris", "Paris"),
            State("02", "FR", "Aisne", "Aisne"),
            State("NW", "DE", "Nordrhein-Westfalen", "Rhénanie-du-Nord-Westphalie"),
            State("BE", "DE", "Berlin", "Berlin"),
            State("NY", "USA", "New York", "New York")
        });
        _resolver = new StateAbbreviationResolver(repository);
    }

    [Test]
    public async Task ResolveAsync_NordrheinWestfalen_InGermany_IsNotTheFrenchNord()
    {
        (await _resolver.ResolveAsync("DE", "Nordrhein-Westfalen")).ShouldBe("NW");
    }

    [Test]
    public async Task ResolveAsync_Berlin_InGermany_IsNotTheSwissBern()
    {
        var resolved = await _resolver.ResolveAsync("DE", "Berlin");

        resolved.ShouldBe("BE");
        (await _resolver.ResolveAsync("CH", "Berlin")).ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_France_UsesTheCountyAsDepartement()
    {
        (await _resolver.ResolveAsync("FR", "Île-de-France", "Paris")).ShouldBe("75");
    }

    [Test]
    public async Task ResolveAsync_France_WithoutCounty_ResolvesNothing()
    {
        (await _resolver.ResolveAsync("FR", "Hauts-de-France", null)).ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_ExactNameBeatsAPartialMatch()
    {
        (await _resolver.ResolveAsync("CH", "Zürich")).ShouldBe("ZH");
    }

    [Test]
    public async Task ResolveAsync_AlphaThreeCountry_MatchesTheSeededAlphaThreePrefix()
    {
        (await _resolver.ResolveAsync("US", "New York")).ShouldBe("NY");
        (await _resolver.ResolveAsync("USA", "New York")).ShouldBe("NY");
    }

    [Test]
    public async Task ResolveAsync_BlankState_ReturnsNull()
    {
        (await _resolver.ResolveAsync("CH", "  ")).ShouldBeNull();
    }

    private static State State(string abbreviation, string countryPrefix, string german, string french)
    {
        var name = new MultiLanguage();
        name.SetValue("de", german);
        name.SetValue("fr", french);
        return new State { Id = Guid.NewGuid(), Abbreviation = abbreviation, CountryPrefix = countryPrefix, Name = name };
    }
}

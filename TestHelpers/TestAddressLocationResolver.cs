// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A real AddressLocationResolver over a fixed states list and no known countries, for skills that resolve
/// state names to codes. SwissAndGerman holds Bern (BE/CH), Berlin (BE/DE), Zurich (ZH/CH) and Ticino (TI/CH),
/// so the cross-country code collision BE is always present.
/// </summary>

using Klacks.Api.Domain.Services.Common;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestAddressLocationResolver
{
    public static IAddressLocationResolver With(params State[] states)
    {
        var stateRepository = Substitute.For<IStateRepository>();
        stateRepository.List().Returns(states.ToList());
        return new AddressLocationResolver(stateRepository, Substitute.For<ICountryResolver>());
    }

    public static IAddressLocationResolver SwissAndGerman() => With(SwissAndGermanStates());

    public static State[] SwissAndGermanStates() =>
    [
        State("BE", "CH", "Bern", "Bern", "Berne", "Berna"),
        State("BE", "DE", "Berlin", "Berlin", "Berlin", "Berlino"),
        State("ZH", "CH", "Zürich", "Zurich", "Zurich", "Zurigo"),
        State("TI", "CH", "Tessin", "Ticino", "Tessin", "Ticino")
    ];

    public static State State(string code, string country, string de, string en, string fr, string it) => new()
    {
        Id = Guid.NewGuid(),
        Abbreviation = code,
        CountryPrefix = country,
        Name = new MultiLanguage { De = de, En = en, Fr = fr, It = it }
    };
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Assistant.Pseudonymization;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

/// <summary>
/// Builds pseudonym maps for tests with readable numbers: a person's number is its entity key (key "17" gives MA-0017),
/// an address's number is its entity key too (key "3" gives ADR-0003), ambiguous names count from 42, pattern values
/// per kind from 1 in order of appearance (as in production).
/// </summary>
internal static class PseudonymTestMaps
{
    public const int FirstAmbiguousOrdinal = 42;

    public static readonly DateOnly Today = new(2026, 11, 20);

    public static PseudonymPerson Employee(int number, string first, string last, GenderEnum gender = GenderEnum.Female, int? idNumber = null) =>
        new(PseudonymKind.Employee, number.ToString(), gender, first, last, null, null, null, false, idNumber);

    public static PseudonymPerson Customer(int number, string first, string last) =>
        new(PseudonymKind.Customer, number.ToString(), GenderEnum.Male, first, last, null, null, null, false, null);

    public static PseudonymPerson User(int number, string first, string last) =>
        new(PseudonymKind.User, number.ToString(), null, first, last, null, null, null, false, null);

    public static PseudonymAddress Address(int number, string street, string? street2 = null) =>
        new(number.ToString(), street, street2, null, null, null);

    public static PseudonymDictionary Dictionary(
        IEnumerable<PseudonymPerson> persons, IEnumerable<string>? phones = null, IEnumerable<PseudonymAddress>? addresses = null)
    {
        var vocabulary = PseudonymVocabulary.Create(persons, phones ?? [], addresses ?? []);
        var next = FirstAmbiguousOrdinal;
        var ordinals = vocabulary.RequiredKeys
            .OrderBy(key => key.Key, StringComparer.Ordinal)
            .ToDictionary(key => key, key => key.Kind == PseudonymKind.Name ? next++ : int.Parse(key.Key));
        return vocabulary.Bind(ordinals);
    }

    public static PseudonymMap Map(PseudonymDictionary dictionary, params string[] texts)
    {
        return new PseudonymMap(dictionary, texts.SelectMany(text => PseudonymTextPseudonymizer.FindPatternValues(text, dictionary)));
    }

    public static string Pseudonymize(PseudonymDictionary dictionary, string text, ISet<string>? used = null)
    {
        var map = Map(dictionary, text);
        return PseudonymTextPseudonymizer.Pseudonymize(text, map, used ?? new HashSet<string>(), Today);
    }
}

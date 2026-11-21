// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

/// <summary>
/// Pins what the vocabulary asks the database to store: keys built from entity ids only, never a name or a street,
/// also for a name that several people share or a street that several addresses share.
/// </summary>
[TestFixture]
public class PseudonymVocabularyTests
{
    [Test]
    public void RequiredKeys_NeverContainANameOrAPhoneNumber()
    {
        var vocabulary = PseudonymVocabulary.Create(
            [
                Employee(17, "Anna", "Meier"),
                Employee(30, "Beat", "Meier", GenderEnum.Male),
                Employee(18, "Jürg", "Müller", GenderEnum.Male),
                Employee(19, "Jürg", "Müller", GenderEnum.Male)
            ],
            ["079 123 45 67"],
            [Address(3, "Bahnhofstrasse 12"), Address(4, "Bahnhofstrasse 12"), Address(5, "Seeweg 3")]);

        var keys = vocabulary.RequiredKeys;

        keys.ShouldContain(key => key.Kind == PseudonymKind.Name);
        keys.Where(key => key.Kind == PseudonymKind.Address).Select(key => key.Key).Order().ShouldBe(["3", "5"]);
        foreach (var key in keys)
        {
            foreach (var forbidden in new[] { "meier", "anna", "beat", "jürg", "juerg", "müller", "mueller", "123", "bahnhof", "strasse", "seeweg", "12" })
            {
                key.Key.ShouldNotContain(forbidden, Case.Insensitive);
            }
        }
    }
}

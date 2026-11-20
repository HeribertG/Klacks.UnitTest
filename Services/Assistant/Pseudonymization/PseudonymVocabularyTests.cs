// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

/// <summary>
/// Pins what the vocabulary asks the database to store: keys built from entity ids only, never a name, also for a
/// name that several people share.
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
            ["079 123 45 67"]);

        var keys = vocabulary.RequiredKeys;

        keys.ShouldContain(key => key.Kind == PseudonymKind.Name);
        foreach (var key in keys)
        {
            foreach (var forbidden in new[] { "meier", "anna", "beat", "jürg", "juerg", "müller", "mueller", "123" })
            {
                key.Key.ShouldNotContain(forbidden, Case.Insensitive);
            }
        }
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Text;
using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

/// <summary>
/// Acceptance measurement of the design (section 2): dictionary of 5000 people with one address each, prompt of 100k
/// characters with names, street addresses, dates and times. The case without addresses is the like-for-like baseline
/// (same prompt, street lines stay plain text). The limits
/// are generous ceilings for a slow CI runner, the measured times are written to the test output.
/// </summary>
[TestFixture]
public class PseudonymPerformanceTests
{
    private const int PersonCount = 5000;
    private const int PromptLength = 100_000;

    [TestCase(true)]
    [TestCase(false)]
    public void FiveThousandPeople_HundredThousandCharacters_StaysWithinBudget(bool withAddresses)
    {
        var firstNames = new[] { "Anna", "Beat", "Claudia", "Daniel", "Erika", "Fritz", "Gabriela", "Hans", "Irene", "Jürg" };
        var persons = Enumerable.Range(1, PersonCount)
            .Select(i => Employee(i, firstNames[i % firstNames.Length], LetterName(i), idNumber: 10000 + i))
            .ToList();

        var build = Stopwatch.StartNew();
        var addresses = withAddresses
            ? Enumerable.Range(1, PersonCount).Select(i => Address(i, StreetLine(i))).ToList()
            : new List<PseudonymAddress>();
        var dictionary = Dictionary(persons, persons.Select((_, i) => $"079 {i:D3} {i % 100:D2} {i % 97:D2}"), addresses);
        build.Stop();

        var builder = new StringBuilder(PromptLength);
        var random = new Random(7);
        while (builder.Length < PromptLength)
        {
            var person = random.Next(1, PersonCount);
            builder.Append("{\"Name\":\"").Append(LetterName(person)).Append("\",\"FirstName\":\"")
                .Append(firstNames[person % firstNames.Length]).Append("\",\"IdNumber\":").Append(10000 + person)
                .Append(",\"Street\":\"").Append(StreetLine(person)).Append("\",\"Zip\":\"9472\",\"City\":\"Grabs\"")
                .Append(",\"Note\":\"Dienst am 2026-11-20 von 08:00 bis 17:00, Mail a").Append(person).Append("@example.ch\"} ");
        }

        var text = builder.ToString();
        var pass = Stopwatch.StartNew();
        var map = Map(dictionary, text);
        var result = PseudonymTextPseudonymizer.Pseudonymize(text, map, new HashSet<string>(), Today);
        pass.Stop();

        TestContext.WriteLine(
            $"Addresses {withAddresses}. Dictionary build: {build.ElapsedMilliseconds} ms, phrases: {dictionary.PhraseCount}, street keys: {dictionary.StreetCount}");
        TestContext.WriteLine($"Pattern scan + pseudonymisation of {text.Length} chars: {pass.ElapsedMilliseconds} ms");
        dictionary.PhraseCount.ShouldBeGreaterThan(PersonCount);
        result.ShouldContain("MA-");
        if (withAddresses)
        {
            result.ShouldNotContain("Zett");
            result.ShouldNotContain("strasse");
            result.ShouldContain("ADR-");
            dictionary.StreetCount.ShouldBeGreaterThanOrEqualTo(PersonCount);
        }

        build.ElapsedMilliseconds.ShouldBeLessThan(20_000);
        pass.ElapsedMilliseconds.ShouldBeLessThan(10_000);
    }

    private static string StreetLine(int number) => $"{LetterName(number)}strasse {number % 200 + 1}";

    private static string LetterName(int number)
    {
        var letters = new StringBuilder("Zett");
        for (var rest = number; rest > 0; rest /= 26)
        {
            letters.Append((char)('a' + rest % 26));
        }

        return letters.ToString();
    }
}

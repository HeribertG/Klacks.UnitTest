// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for QualificationNameHeuristic: a qualification name in any language found at a word start of the order
/// name, description or group name is suggested; inside a word, too short, deleted or already required it is not.
/// </summary>

using Klacks.Api.Application.Services.Orders.Review;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class QualificationNameHeuristicTests
{
    private static Qualification Qualification(string de, string? en = null)
    {
        var name = new MultiLanguage();
        name.SetValue("de", de);
        if (en != null)
        {
            name.SetValue("en", en);
        }

        return new Qualification { Id = Guid.NewGuid(), Name = name };
    }

    [Test]
    public void ANameInTheGroupName_IsSuggested()
    {
        var nursing = Qualification("Pflegefach", "Nursing");

        var suggested = QualificationNameHeuristic.Suggest(["Nachtwache", null, "Pflegefach Bern"], [nursing], []);

        Assert.That(suggested, Is.EqualTo(new[] { nursing }));
    }

    [Test]
    public void AnotherLanguageName_IsMatchedToo()
    {
        var nursing = Qualification("Pflegefach", "Nursing");

        var suggested = QualificationNameHeuristic.Suggest(["Night nursing Zurich"], [nursing], []);

        Assert.That(suggested, Is.EqualTo(new[] { nursing }));
    }

    [Test]
    public void ANameInsideAWord_TooShort_OrAlreadyRequired_IsNotSuggested()
    {
        var inside = Qualification("pflege");
        var tooShort = Qualification("RN");
        var required = Qualification("Sanität");

        var suggested = QualificationNameHeuristic.Suggest(
            ["Hauspflege RN Sanität"], [inside, tooShort, required], [required.Id]);

        Assert.That(suggested, Is.Empty);
    }
}

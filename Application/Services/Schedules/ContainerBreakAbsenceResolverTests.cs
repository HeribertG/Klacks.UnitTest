// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// ContainerBreakAbsenceResolver: only container absence types count; no name means the seeded unpaid break; a name
/// matches equal names and abbreviations in any language first, then names containing it; none or several end in an
/// error that names the options.
/// </summary>
[TestFixture]
public class ContainerBreakAbsenceResolverTests
{
    private static readonly Guid PaidBreakId = Guid.NewGuid();
    private static readonly Guid VacationId = Guid.NewGuid();

    [Test]
    public void NoName_SelectsTheSeededUnpaidBreak()
    {
        // Act
        var (absence, error) = ContainerBreakAbsenceResolver.Resolve(Absences(), null);

        // Assert
        error.ShouldBeNull();
        absence!.Id.ShouldBe(SeededAbsenceIds.UnpaidBreak);
    }

    [Test]
    public void NoName_WithoutTheSeededBreak_ListsTheContainerAbsences()
    {
        // Act
        var (absence, error) = ContainerBreakAbsenceResolver.Resolve(
            Absences().Where(a => a.Id != SeededAbsenceIds.UnpaidBreak), " ");

        // Assert
        absence.ShouldBeNull();
        error!.ShouldContain("Paid break");
        error.ShouldNotContain("Vacation");
    }

    [Test]
    public void NoName_SeededBreakNoLongerForContainers_IsRefused()
    {
        // Arrange
        var absences = Absences();
        absences.Single(a => a.Id == SeededAbsenceIds.UnpaidBreak).AppliesToContainer = false;

        // Act
        var (absence, error) = ContainerBreakAbsenceResolver.Resolve(absences, null);

        // Assert
        absence.ShouldBeNull();
        error!.ShouldContain("no longer");
    }

    [TestCase("Pause bezahlt")]
    [TestCase("  PAID BREAK ")]
    [TestCase("P+")]
    public void EqualNameOrAbbreviation_InAnyLanguage_Matches(string name)
    {
        // Act
        var (absence, error) = ContainerBreakAbsenceResolver.Resolve(Absences(), name);

        // Assert
        error.ShouldBeNull();
        absence!.Id.ShouldBe(PaidBreakId);
    }

    [Test]
    public void EqualName_WinsOverNamesContainingIt()
    {
        // Arrange
        var absences = Absences();
        absences.Add(Absence(Guid.NewGuid(), "Pause", "Break", "P", true));

        // Act
        var (absence, error) = ContainerBreakAbsenceResolver.Resolve(absences, "pause");

        // Assert
        error.ShouldBeNull();
        absence!.Name.De.ShouldBe("Pause");
    }

    [Test]
    public void SeveralNamesContainingIt_AreAmbiguous()
    {
        // Act
        var (absence, error) = ContainerBreakAbsenceResolver.Resolve(Absences(), "pause");

        // Assert
        absence.ShouldBeNull();
        error!.ShouldContain("Several");
        error.ShouldContain("Paid break, Unpaid break");
    }

    [Test]
    public void AbsenceNotForContainers_IsNotFound()
    {
        // Act
        var (absence, error) = ContainerBreakAbsenceResolver.Resolve(Absences(), "Ferien");

        // Assert
        absence.ShouldBeNull();
        error!.ShouldContain("No absence type for containers is named 'Ferien'");
        error.ShouldContain("Paid break, Unpaid break");
    }

    private static List<Absence> Absences() =>
    [
        Absence(SeededAbsenceIds.UnpaidBreak, "Pause unbezahlt", "Unpaid break", "P-", true),
        Absence(PaidBreakId, "Pause bezahlt", "Paid break", "P+", true),
        Absence(VacationId, "Ferien", "Vacation", "Fe", false)
    ];

    private static Absence Absence(Guid id, string de, string en, string abbreviation, bool appliesToContainer) => new()
    {
        Id = id,
        Name = new MultiLanguage { De = de, En = en },
        Abbreviation = new MultiLanguage { De = abbreviation },
        Description = new MultiLanguage(),
        AppliesToContainer = appliesToContainer
    };
}

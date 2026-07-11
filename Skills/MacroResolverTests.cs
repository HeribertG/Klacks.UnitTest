// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroResolver: spoken or decorated queries ("Makro-All-Shift", "all shift")
/// resolve the camel-case macro "AllShift" via the compact stage with label-word stripping
/// (2026-07-11 transcript regression), lightly damaged STT input still resolves, several
/// plausible macros return a disambiguation error, and an unknown name lists the real macros.
/// </summary>

using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MacroResolverTests
{
    private static readonly Guid AllShiftId = Guid.NewGuid();

    private static List<MacroResource> DefaultMacros() =>
    [
        new() { Id = AllShiftId, Name = "AllShift" },
        new() { Id = Guid.NewGuid(), Name = "Ferien" },
        new() { Id = Guid.NewGuid(), Name = "Krankheit" },
        new() { Id = Guid.NewGuid(), Name = "NachtZuschlag" },
    ];

    [TestCase("AllShift")]
    [TestCase("allshift")]
    [TestCase("Makro-All-Shift")]
    [TestCase("Makro All Shift")]
    [TestCase("All Shift")]
    [TestCase("All-Shift")]
    [TestCase("all schift")]
    public void Resolve_SpokenOrDecoratedVariants_FindAllShift(string query)
    {
        var (macro, error) = MacroResolver.Resolve(DefaultMacros(), query);

        Assert.That(error, Is.Null);
        Assert.That(macro, Is.Not.Null);
        Assert.That(macro!.Id, Is.EqualTo(AllShiftId));
    }

    [Test]
    public void Resolve_LabelSuffix_FindAllShift()
    {
        var (macro, error) = MacroResolver.Resolve(DefaultMacros(), "AllShift-Makro");

        Assert.That(error, Is.Null);
        Assert.That(macro!.Id, Is.EqualTo(AllShiftId));
    }

    [Test]
    public void Resolve_SeveralPlausibleMacros_ReturnsDisambiguation()
    {
        var macros = DefaultMacros();
        macros.Add(new MacroResource { Id = Guid.NewGuid(), Name = "AllShift2" });

        var (macro, error) = MacroResolver.Resolve(macros, "Makro All Schift");

        Assert.That(macro, Is.Null);
        Assert.That(error, Does.Contain("ambiguous"));
        Assert.That(error, Does.Contain("AllShift"));
        Assert.That(error, Does.Contain("AllShift2"));
    }

    [Test]
    public void Resolve_UnknownName_ListsAvailableMacros()
    {
        var (macro, error) = MacroResolver.Resolve(DefaultMacros(), "Sonderzulage");

        Assert.That(macro, Is.Null);
        Assert.That(error, Does.Contain("not found"));
        Assert.That(error, Does.Contain("Available macros"));
        Assert.That(error, Does.Contain("AllShift"));
        Assert.That(error, Does.Contain("Ferien"));
    }

    [Test]
    public void Resolve_EmptyQuery_ReturnsNotFound()
    {
        var (macro, error) = MacroResolver.Resolve(DefaultMacros(), "   ");

        Assert.That(macro, Is.Null);
        Assert.That(error, Does.Contain("not found"));
    }
}

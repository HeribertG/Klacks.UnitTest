// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the per-variant grouping of the scenario summary: the peak is the day maximum of the open slots summed over the
/// shifts of one variant, the earliest day wins a tie, a shift without a variant is left out, and no variant mapping gives null.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules.Summary;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ScenarioVariantCoverageCalculatorTests
{
    private static readonly Dictionary<Guid, (string Name, int Count)> NoCopies = [];

    private const string FirstName = "Alpha";
    private const string SecondName = "Beta";
    private static readonly DateOnly FirstDay = new(2099, 10, 1);

    private readonly Guid _firstVariant = Guid.NewGuid();
    private readonly Guid _secondVariant = Guid.NewGuid();
    private readonly Shift _early = new() { Id = Guid.NewGuid() };
    private readonly Shift _late = new() { Id = Guid.NewGuid() };
    private readonly Shift _single = new() { Id = Guid.NewGuid() };
    private readonly Shift _plain = new() { Id = Guid.NewGuid() };

    [Test]
    public void Summarize_PeakIsTheDayMaximumOfTheSum_NotThePeakOfOneShift()
    {
        var result = Summarize();

        var first = result!.Single(v => v.VariantId == _firstVariant);
        first.PeakOpenSlots.ShouldBe(4);
        first.PeakDate.ShouldBe(FirstDay.AddDays(1));
        first.DemandedSlots.ShouldBe(15);
        first.FilledSlots.ShouldBe(8);
        first.OpenSlots.ShouldBe(7);
    }

    [Test]
    public void Summarize_EarliestDayWinsATie()
    {
        var result = Summarize();

        var second = result!.Single(v => v.VariantId == _secondVariant);
        second.PeakOpenSlots.ShouldBe(2);
        second.PeakDate.ShouldBe(FirstDay);
    }

    [Test]
    public void Summarize_LeavesAShiftWithoutVariantOutAndOrdersByName()
    {
        var result = Summarize();

        result!.Select(v => v.Name).ShouldBe(new[] { FirstName, SecondName });
        result.Sum(v => v.DemandedSlots).ShouldBe(15 + 6);
    }

    [Test]
    public void Summarize_FullyStaffedVariantHasNoPeakDate()
    {
        var keys = new Dictionary<Guid, (Guid VariantId, string Name)> { [_single.Id] = (_secondVariant, SecondName) };
        var coverage = new List<ScenarioShiftCoverageDto> { new(_single.Id, "S", "S", 3, 3) };

        var result = ScenarioVariantCoverageCalculator.Summarize(keys, coverage, [], NoCopies);

        result!.Single().PeakOpenSlots.ShouldBe(0);
        result!.Single().PeakDate.ShouldBeNull();
    }

    [Test]
    public void Summarize_NoVariantMappingGivesNull()
    {
        var coverage = new List<ScenarioShiftCoverageDto> { new(_plain.Id, "P", "P", 3, 0) };

        var result = ScenarioVariantCoverageCalculator.Summarize(
            new Dictionary<Guid, (Guid VariantId, string Name)>(), coverage, [(_plain, FirstDay, 3)], NoCopies);

        result.ShouldBeNull();
    }

    [Test]
    public void Summarize_PutsTheCopiedCountOnTheEntryOfItsVariant()
    {
        var keys = new Dictionary<Guid, (Guid VariantId, string Name)> { [_single.Id] = (_secondVariant, SecondName) };
        var coverage = new List<ScenarioShiftCoverageDto> { new(_single.Id, "S", "S", 3, 3) };
        var copied = new Dictionary<Guid, (string Name, int Count)> { [_secondVariant] = (SecondName, 4) };

        var result = ScenarioVariantCoverageCalculator.Summarize(keys, coverage, [], copied);

        result!.Single().CopiedShiftCount.ShouldBe(4);
    }

    [Test]
    public void Summarize_AVariantThatPlansNothingButHoldsCopies_IsListedInNameOrder()
    {
        var keys = new Dictionary<Guid, (Guid VariantId, string Name)> { [_single.Id] = (_secondVariant, SecondName) };
        var coverage = new List<ScenarioShiftCoverageDto> { new(_single.Id, "S", "S", 3, 0) };
        var copied = new Dictionary<Guid, (string Name, int Count)> { [_firstVariant] = (FirstName, 2) };

        var result = ScenarioVariantCoverageCalculator.Summarize(keys, coverage, [], copied);

        result!.Select(v => v.Name).ShouldBe(new[] { FirstName, SecondName });
        var unplanned = result!.Single(v => v.VariantId == _firstVariant);
        unplanned.CopiedShiftCount.ShouldBe(2);
        unplanned.DemandedSlots.ShouldBe(0);
        unplanned.PeakDate.ShouldBeNull();
    }

    private IReadOnlyList<ScenarioVariantCoverageDto>? Summarize()
    {
        var keys = new Dictionary<Guid, (Guid VariantId, string Name)>
        {
            [_early.Id] = (_firstVariant, FirstName),
            [_late.Id] = (_firstVariant, FirstName),
            [_single.Id] = (_secondVariant, SecondName)
        };
        var coverage = new List<ScenarioShiftCoverageDto>
        {
            new(_early.Id, "E", "E", 6, 3),
            new(_late.Id, "L", "L", 9, 5),
            new(_single.Id, "S", "S", 6, 0),
            new(_plain.Id, "P", "P", 3, 0)
        };
        var open = new List<(Shift Shift, DateOnly Date, int Open)>
        {
            (_early, FirstDay, 2),
            (_early, FirstDay.AddDays(1), 1),
            (_late, FirstDay, 1),
            (_late, FirstDay.AddDays(1), 3),
            (_single, FirstDay, 2),
            (_single, FirstDay.AddDays(1), 2),
            (_single, FirstDay.AddDays(2), 2),
            (_plain, FirstDay.AddDays(1), 9)
        };

        return ScenarioVariantCoverageCalculator.Summarize(keys, coverage, open, NoCopies);
    }
}

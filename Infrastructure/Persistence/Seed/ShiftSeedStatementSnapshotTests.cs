// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class ShiftSeedStatementSnapshotTests
{
    private const string ShiftInsert = "INSERT INTO public.shift";
    private const string ShiftUpdate = "UPDATE public.shift";
    private const string OrderRowPattern = @"NULL, NULL, 0,\r?\n";
    private const string OriginalShiftRowPattern = @"NULL, NULL, 2,\r?\n";
    private const string SplitRowPattern = @"NULL, '[0-9a-f-]{36}', 3,\r?\n";
    private const string GuidPattern = @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    private const string TimestampPattern = @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{6}\+00";
    private const string GuidPlaceholder = "GUID";
    private const string TimestampPlaceholder = "TIMESTAMP";

    [Test]
    public void GenerateInsertScriptForShifts_EmitsTheSameStatementMixPerStatus()
    {
        var (script, shiftIds) = ShiftSeed.GenerateInsertScriptForShifts();

        Count(script, ShiftInsert).ShouldBe(490);
        Count(script, ShiftUpdate).ShouldBe(220);
        Matches(script, OrderRowPattern).ShouldBe(220);
        Matches(script, OriginalShiftRowPattern).ShouldBe(190);
        Matches(script, SplitRowPattern).ShouldBe(80);
        shiftIds.Count.ShouldBe(430);
    }

    [Test]
    public void GenerateInsertScriptForShifts_KeepsTheFixedShiftTimesOfEachCategory()
    {
        var (script, _) = ShiftSeed.GenerateInsertScriptForShifts();

        Matches(script, @"'00:00:00', '00:00:00', '07:00:00', '2025-01-01', '07:00:00', NULL,").ShouldBe(20);

        Matches(script, @"'00:00:00', '00:00:00', '17:00:00', '2025-01-01', '08:00:00', NULL,").ShouldBe(120);
        Matches(script, @"'00:00:00', '00:00:00', '07:00:00', '2025-01-01', '23:00:00', NULL,").ShouldBe(184);
        Matches(script, @"'00:00:00', '00:00:00', '06:00:00', '2025-01-01', '22:00:00', NULL,").ShouldBe(10);
        script.ShouldNotContain("{definition.");
    }

    [Test]
    public void GenerateContainerTemplates_EmitsOneRowPerTemplate()
    {
        var (script, containerIds) = ShiftSeed.GenerateContainerTemplates();

        Count(script, ShiftInsert).ShouldBe(20);
        Count(script, ShiftUpdate).ShouldBe(0);
        containerIds.Count.ShouldBe(20);
    }

    [Test]
    public void GenerateContainers_EmitsTwentyRowsPerRootGroupAndType()
    {
        var (script, containerIds) = ShiftSeed.GenerateContainers();

        Count(script, ShiftInsert).ShouldBe(240);
        Count(script, ShiftUpdate).ShouldBe(0);
        containerIds.Count.ShouldBe(240);
    }

    [Test]
    public void GenerateTimeRangeShiftsWithClients_EmitsTheOrderAndItsPlannableCopy()
    {
        var (script, shiftIds) = ShiftSeed.GenerateTimeRangeShiftsWithClients();

        Count(script, ShiftInsert).ShouldBe(800);
        Count(script, ShiftUpdate).ShouldBe(400);
        Matches(script, OrderRowPattern).ShouldBe(400);
        Matches(script, OriginalShiftRowPattern).ShouldBe(400);
        Matches(script, @"false, false, true, 1, '00:00:00', '00:00:00',").ShouldBe(800);
        shiftIds.Count.ShouldBe(800);
    }

    [Test]
    public void GenerateInsertScriptForShiftGroupItems_LinksEveryTrackedShiftToItsGroups()
    {
        var (_, shiftIds) = ShiftSeed.GenerateInsertScriptForShifts();

        var script = ShiftSeed.GenerateInsertScriptForShiftGroupItems(shiftIds);

        Count(script, "INSERT INTO public.group_item").ShouldBeGreaterThanOrEqualTo(shiftIds.Count);
    }

    [Test]
    public void GenerateInsertScriptForShifts_TwoRuns_DifferOnlyInGeneratedKeysAndAuditStamps()
    {
        var first = Normalise(ShiftSeed.GenerateInsertScriptForShifts().script);
        var second = Normalise(ShiftSeed.GenerateInsertScriptForShifts().script);

        first.ShouldBe(second);
    }

    [Test]
    public void GenerateTimeRangeShiftsWithClients_TwoRuns_DifferOnlyInGeneratedKeysAndAuditStamps()
    {
        var first = Normalise(ShiftSeed.GenerateTimeRangeShiftsWithClients().script);
        var second = Normalise(ShiftSeed.GenerateTimeRangeShiftsWithClients().script);

        first.ShouldBe(second);
    }

    private static string Normalise(string script)
    {
        var withoutTimestamps = Regex.Replace(script, TimestampPattern, TimestampPlaceholder);
        return Regex.Replace(withoutTimestamps, GuidPattern, GuidPlaceholder);
    }

    private static int Count(string script, string needle)
    {
        return script.Split(needle).Length - 1;
    }

    private static int Matches(string script, string pattern)
    {
        return Regex.Matches(script, pattern).Count;
    }
}

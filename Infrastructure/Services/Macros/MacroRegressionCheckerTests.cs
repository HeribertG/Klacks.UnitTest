// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the macro regression check: an identical or purely additive copy passes, a copy that
/// changes a surcharge channel the original emits with a non-zero value fails with a per-input diff. The result
/// channel 1 is strict (0 is a real result): it may only stay unchanged or become the original result plus exactly
/// the surcharges added on channels the original leaves at zero (sum form with a floating-point tolerance far below
/// a cent), and a copy may not add channel 1 where the original has none. The caller's cancellation token stops the
/// check with OperationCanceledException instead of a budget failure. Compile errors
/// of either script abort the check, inputs the original fails on are skipped, a copy that fails where the
/// original runs aborts the check, the time budget stops the check, and the grid has its documented size
/// with unique sample descriptions. The seeded AllShift macro is used as a real original: the grid must
/// drive every one of its channels, harmless clones pass and clones that touch its surcharges fail; a clone with a
/// Wednesday surcharge and a total recomputed through a copy of its function passes, while calling the function of
/// the original fails at runtime (known interpreter defect).
/// </summary>

using System.Diagnostics;
using System.Globalization;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroRegressionCheckerTests
{
    private const int ExpectedGridSize = 672;
    private const int SamplesPerWeekday = ExpectedGridSize / 7;

    private const string SundayNightBonus =
        "IMPORT Hour, Weekday, NightRate\n"
        + "DIM Bonus\n"
        + "Bonus = 0\n"
        + "IF Weekday = 7 THEN Bonus = Hour * NightRate ENDIF\n"
        + "OUTPUT 10, Bonus\n"
        + "OUTPUT 1, Hour";

    private const string FailsOnSundays =
        "IMPORT Hour, Weekday\n"
        + "IF Weekday = 7 THEN\n"
        + "MSGBOX \"sunday\"\n"
        + "ENDIF\n"
        + "OUTPUT 1, Hour";

    private const string FailsOnWednesdays =
        "\nIF Weekday = 3 THEN\n"
        + "MSGBOX \"wednesday\"\n"
        + "ENDIF";

    private const string SlowOriginal =
        "DIM I\n"
        + "FOR I = 1 TO 100000\n"
        + "NEXT\n"
        + "OUTPUT 1, 1";

    private const string FastOriginal = "IMPORT Hour\nOUTPUT 1, Hour";
    private const string PlainResult = "IMPORT Hour\nOUTPUT 1, Hour";
    private const string FreeChannelSurcharge = "DIM Extra, NewTotal\nExtra = Hour * 0.5";
    private const string RawStartIndexText = "startIndex";

    private static readonly int[] AllShiftChannels = [1, 10, 11, 12, 13, 14];

    private MacroRegressionChecker _checker = null!;

    [SetUp]
    public void SetUp()
    {
        _checker = new MacroRegressionChecker();
    }

    [Test]
    public void Grid_HasDocumentedSize_AndUniqueDescriptions()
    {
        MacroRegressionGrid.Samples.Count.ShouldBe(ExpectedGridSize);
        MacroRegressionGrid.Samples.Select(s => s.Description).Distinct().Count().ShouldBe(ExpectedGridSize);
    }

    [Test]
    public void Grid_DrivesEveryChannelOfTheSeededAllShiftMacro()
    {
        var compiled = CompiledScript.Compile(AllShiftScript());
        compiled.HasError.ShouldBeFalse(compiled.Error?.Description);
        var nonZeroChannels = new HashSet<int>();

        foreach (var sample in MacroRegressionGrid.Samples)
        {
            var script = compiled.CloneForExecution();
            MacroDataImportBinder.Bind(script, sample.Data);
            var result = new ScriptExecutionContext(script).Execute();
            result.Success.ShouldBeTrue(result.Error?.Description);
            foreach (var message in result.Messages.Where(m => IsNonZero(m.Message)))
            {
                nonZeroChannels.Add(message.Type);
            }
        }

        nonZeroChannels.ShouldBe(AllShiftChannels, ignoreOrder: true);
    }

    [Test]
    public void IdenticalScript_Passes_OnEveryInput()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus);

        result.Passed.ShouldBeTrue(result.FailureMessage);
        result.ComparedSamples.ShouldBe(ExpectedGridSize);
        result.SkippedSamples.ShouldBe(0);
    }

    [Test]
    public void AdditionalChannel_Passes()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + "\nOUTPUT 13, Hour * 0.5");

        result.Passed.ShouldBeTrue(result.FailureMessage);
    }

    [Test]
    public void ExtraOutputOnAChannelTheOriginalFills_Fails_WithDiff()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + "\nOUTPUT 10, 1");

        result.Passed.ShouldBeFalse();
        result.FailureMessage.ShouldBeNull();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.None);
        result.TotalDeviationCount.ShouldBe(SamplesPerWeekday);
        result.Deviations.ShouldAllBe(d => d.Channel == 10 && d.CopyValue == d.OriginalValue + 1m);
        result.Deviations.First().SampleDescription.ShouldContain("weekday 7");
    }

    [Test]
    public void OverriddenResultChannel_Fails()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + "\nOUTPUT 1, Hour * 2");

        result.Passed.ShouldBeFalse();
        result.TotalDeviationCount.ShouldBe(ExpectedGridSize);
        result.Deviations.ShouldAllBe(d => d.Channel == 1);
    }

    [Test]
    public void OriginalResultZero_CopyResultNonZeroWithoutAddedSurcharge_Fails()
    {
        const string original = "IMPORT Hour\nOUTPUT 1, 0";

        var result = _checker.Check(original, original + "\nOUTPUT 1, Hour");

        result.Passed.ShouldBeFalse();
        result.FailureMessage.ShouldBeNull();
        result.TotalDeviationCount.ShouldBe(ExpectedGridSize);
        result.Deviations.ShouldAllBe(d => d.Channel == 1 && d.OriginalValue == 0m && d.AcceptedTotal == null);
    }

    [Test]
    public void SurchargeOnAFreeChannel_ResultUnchanged_Passes()
    {
        var result = _checker.Check(PlainResult, PlainResult + "\n" + FreeChannelSurcharge + "\nOUTPUT 13, Extra");

        result.Passed.ShouldBeTrue(result.FailureMessage);
        result.ComparedSamples.ShouldBe(ExpectedGridSize);
    }

    [Test]
    public void SurchargeOnAFreeChannel_ResultSetToOriginalPlusSurcharge_Passes()
    {
        var copy = PlainResult + "\n" + FreeChannelSurcharge + "\nNewTotal = Hour + Extra\nOUTPUT 13, Extra\nOUTPUT 1, NewTotal";

        var result = _checker.Check(PlainResult, copy);

        result.Passed.ShouldBeTrue(result.FailureMessage ?? DescribeDeviations(result));
        result.ComparedSamples.ShouldBe(ExpectedGridSize);
    }

    [Test]
    public void SurchargeOnAFreeChannel_ResultSetToAWrongSum_Fails_WithAcceptedTotal()
    {
        var copy = PlainResult + "\n" + FreeChannelSurcharge + "\nNewTotal = Hour + Extra + 1\nOUTPUT 13, Extra\nOUTPUT 1, NewTotal";

        var result = _checker.Check(PlainResult, copy);

        result.Passed.ShouldBeFalse();
        result.FailureMessage.ShouldBeNull();
        result.TotalDeviationCount.ShouldBe(ExpectedGridSize);
        result.Deviations.ShouldAllBe(d =>
            d.Channel == 1 && d.CopyValue == d.OriginalValue * 1.5m + 1m && d.AcceptedTotal == d.OriginalValue * 1.5m);
    }

    [Test]
    public void SurchargeOnAFreeChannel_ResultOffByOneCentFromTheSum_Fails()
    {
        var copy = PlainResult + "\n" + FreeChannelSurcharge + "\nNewTotal = Hour + Extra + 0.01\nOUTPUT 13, Extra\nOUTPUT 1, NewTotal";

        var result = _checker.Check(PlainResult, copy);

        result.Passed.ShouldBeFalse();
        result.TotalDeviationCount.ShouldBe(ExpectedGridSize);
    }

    [Test]
    public void CallerTokenAlreadyCancelled_Throws()
    {
        using var turn = new CancellationTokenSource();
        turn.Cancel();

        Should.Throw<OperationCanceledException>(() => _checker.Check(PlainResult, PlainResult, turn.Token));
    }

    [Test]
    public void CallerCancellingDuringTheCheck_Throws_InsteadOfReportingTheBudget()
    {
        using var turn = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();

        Should.Throw<OperationCanceledException>(() => _checker.Check(SlowOriginal, SlowOriginal, turn.Token));

        watch.Stop();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public void ResultRaisedBySurchargeOnAChannelTheOriginalFills_Fails()
    {
        const string original = "IMPORT Hour\nOUTPUT 10, Hour\nOUTPUT 1, Hour";

        var result = _checker.Check(original, original + "\nOUTPUT 1, Hour * 2");

        result.Passed.ShouldBeFalse();
        result.Deviations.ShouldAllBe(d => d.Channel == 1 && d.AcceptedTotal == null);
    }

    [Test]
    public void OriginalWithoutResultChannel_CopyAddingOne_Fails()
    {
        const string original = "IMPORT Hour\nOUTPUT 10, Hour";

        var result = _checker.Check(original, original + "\nOUTPUT 1, Hour");

        result.Passed.ShouldBeFalse();
        result.TotalDeviationCount.ShouldBe(ExpectedGridSize);
        result.Deviations.ShouldAllBe(d => d.Channel == 1 && d.OriginalValue == null && d.CopyValue != null);
    }

    [Test]
    public void CandidateCompileError_AbortsTheCheck()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + "\nDIM 123abc");

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.CopyCompileError);
        result.FailureMessage.ShouldNotBeNull();
        result.FailureMessage.ShouldContain("extended script does not compile");
    }

    [Test]
    public void ReImportingASymbolTheOriginalImports_IsACompileError()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + "\nIMPORT Hour");

        result.Passed.ShouldBeFalse();
        result.FailureMessage.ShouldNotBeNull();
        result.FailureMessage.ShouldContain("extended script does not compile");
    }

    [Test]
    public void CopyReadingAVariableOfTheOriginal_FailsAtRuntime_KnownInterpreterDefect()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + "\nOUTPUT 13, Bonus");

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.CopyRuntimeError);
        result.FailureMessage!.ShouldContain("although the original runs there");
        result.FailureMessage.ShouldContain("has not been assigned a value");
    }

    [Test]
    public void OriginalCompileError_AbortsTheCheck()
    {
        var result = _checker.Check("DIM 123abc", SundayNightBonus);

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.OriginalCompileError);
        result.FailureMessage!.ShouldContain("original macro script does not compile");
    }

    [Test]
    public void OriginalThatCrashesTheCompiler_AbortsAsOriginalCompileError_WithoutRawExceptionText()
    {
        const string crashingOriginal = "OUTPUT 1, 0 ' note";

        var result = _checker.Check(crashingOriginal, crashingOriginal + "\nOUTPUT 13, 1");

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.OriginalCompileError);
        result.FailureMessage!.ShouldContain("original macro script does not compile");
        result.FailureMessage.ShouldNotContain(RawStartIndexText);
    }

    [Test]
    public void CopyThatCrashesTheCompiler_AbortsAsCopyCompileError_WithoutRawExceptionText()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + "\nOUTPUT 13, 1 ' note");

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.CopyCompileError);
        result.FailureMessage!.ShouldContain("extended script does not compile");
        result.FailureMessage.ShouldNotContain(RawStartIndexText);
    }

    [Test]
    public void InputsTheOriginalFailsOn_AreSkipped()
    {
        var result = _checker.Check(FailsOnSundays, FailsOnSundays);

        result.Passed.ShouldBeTrue(result.FailureMessage);
        result.SkippedSamples.ShouldBe(SamplesPerWeekday);
        result.ComparedSamples.ShouldBe(ExpectedGridSize - SamplesPerWeekday);
    }

    [Test]
    public void OriginalFailingOnEveryInput_CannotBeCompared()
    {
        const string original = "MSGBOX \"always\"\nOUTPUT 1, 1";

        var result = _checker.Check(original, original);

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.NoComparableSample);
        result.FailureMessage!.ShouldContain("could not be executed on any test input");
    }

    [Test]
    public void CopyFailingWhereTheOriginalRuns_AbortsTheCheck()
    {
        var result = _checker.Check(SundayNightBonus, SundayNightBonus + FailsOnWednesdays);

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.CopyRuntimeError);
        result.FailureMessage!.ShouldContain("fails at test input [weekday 3");
    }

    [Test]
    public void CheckExceedingItsBudget_StopsAndFails()
    {
        var checker = new MacroRegressionChecker(TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();

        var result = checker.Check(SlowOriginal, SlowOriginal);

        watch.Stop();
        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.BudgetExceeded);
        result.FailureMessage!.ShouldContain("did not finish within 100 ms");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public void SlowCopyExceedingTheBudget_IsReportedAsBudgetExceeded_NotAsACopyFailure()
    {
        var checker = new MacroRegressionChecker(TimeSpan.FromMilliseconds(100));

        var result = checker.Check(FastOriginal, FastOriginal + "\n" + SlowOriginal);

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.BudgetExceeded);
        result.FailureMessage!.ShouldContain("did not finish within 100 ms");
    }

    [Test]
    public void AllShift_CommentAndWhitespaceClone_PassesOnEveryInput()
    {
        var original = AllShiftScript();
        var clone = "' extended copy made by the assistant\n\n" + original.Replace("\n", "\n\n") + "\n' end\n";

        var first = Stopwatch.StartNew();
        var result = _checker.Check(original, clone);
        first.Stop();
        var warm = Stopwatch.StartNew();
        _checker.Check(original, clone);
        warm.Stop();
        TestContext.Out.WriteLine(
            $"AllShift regression check: first {first.ElapsedMilliseconds} ms, warm {warm.ElapsedMilliseconds} ms");

        result.Passed.ShouldBeTrue(result.FailureMessage);
        result.ComparedSamples.ShouldBe(ExpectedGridSize);
        result.SkippedSamples.ShouldBe(0);
    }

    [Test]
    public void AllShift_SurchargeOnAWeekdayOutsideEveryWeekendProfile_Passes()
    {
        var original = AllShiftScript();
        var clone = original + "\nIF Weekday = 3 THEN\nOUTPUT 13, Hour * 0.1\nENDIF\n";

        var result = _checker.Check(original, clone);

        result.Passed.ShouldBeTrue(result.FailureMessage);
    }

    [Test]
    public void AllShift_AppendedBlockWithItsOwnVariable_Passes()
    {
        var original = AllShiftScript();
        var clone = original
            + "\nDIM WednesdayBonus\nWednesdayBonus = 0\nIF Weekday = 3 THEN\nWednesdayBonus = Hour * 0.1\nENDIF\n"
            + "OUTPUT 13, WednesdayBonus\n";

        var result = _checker.Check(original, clone);

        result.Passed.ShouldBeTrue(result.FailureMessage);
        result.ComparedSamples.ShouldBe(ExpectedGridSize);
    }

    [Test]
    public void AllShift_UnconditionalSurchargeOnTheThirdWeekendChannel_Fails()
    {
        var original = AllShiftScript();

        var result = _checker.Check(original, original + "\nOUTPUT 13, Hour * 0.1\n");

        result.Passed.ShouldBeFalse();
        result.FailureMessage.ShouldBeNull();
        result.Deviations.ShouldAllBe(d => d.Channel == 13);
    }

    [Test]
    public void AllShift_OverridingTheTotal_Fails()
    {
        var original = AllShiftScript();

        var result = _checker.Check(original, original + "\nOUTPUT 1, 0\n");

        result.Passed.ShouldBeFalse();
        result.Deviations.ShouldAllBe(d => d.Channel == 1 && d.CopyValue == 0m);
    }

    [Test]
    public void AllShift_SurchargeOnAFreeChannel_WithTheResultRecomputedFromImports_Passes()
    {
        var original = AllShiftScript();
        var block = SeededMacroScripts.AllShiftWednesdayBonusBlock(SeededMacroScripts.CopiedFunctionName);

        var result = _checker.Check(original, original + "\n" + block);

        result.Passed.ShouldBeTrue(result.FailureMessage ?? DescribeDeviations(result));
        result.ComparedSamples.ShouldBe(ExpectedGridSize);
    }

    [Test]
    public void AllShift_BlockCallingTheFunctionOfTheOriginal_FailsAtRuntime_KnownInterpreterDefect()
    {
        var original = AllShiftScript();
        var block = SeededMacroScripts.AllShiftWednesdayBonusBlock(SeededMacroScripts.OriginalFunctionName);

        var result = _checker.Check(original, original + "\n" + block);

        result.Passed.ShouldBeFalse();
        result.FailureKind.ShouldBe(MacroRegressionFailureKind.CopyRuntimeError);
        result.FailureMessage!.ShouldContain(SeededMacroScripts.OriginalFunctionName);
        result.FailureMessage.ShouldContain("has not been assigned a value");
    }

    [Test]
    public void AllShift_WednesdayBonusBlockWithAWrongTotal_Fails()
    {
        var original = AllShiftScript();
        var block = SeededMacroScripts.AllShiftWednesdayBonusBlock(SeededMacroScripts.CopiedFunctionName).Replace(
            "Round(NewTotal, 2) + WednesdayBonus", "Round(NewTotal, 2) + WednesdayBonus * 2");

        var result = _checker.Check(original, original + "\n" + block);

        result.Passed.ShouldBeFalse();
        result.TotalDeviationCount.ShouldBe(SamplesPerWeekday);
        result.Deviations.ShouldAllBe(d => d.Channel == 1 && d.SampleDescription.Contains("weekday 3"));
    }

    private static string DescribeDeviations(MacroRegressionResult result) =>
        string.Join("; ", result.Deviations.Select(d =>
            $"[{d.SampleDescription}] ch {d.Channel}: {d.OriginalValue} -> {d.CopyValue} (accepted {d.AcceptedTotal})"));

    private static bool IsNonZero(string message) =>
        decimal.TryParse(message, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value != 0m;

    private static string AllShiftScript() => SeededMacroScripts.AllShiftScript();
}

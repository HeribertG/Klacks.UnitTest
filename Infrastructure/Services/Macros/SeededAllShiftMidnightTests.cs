// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins what the shipped AllShift and AllShiftAdditive scripts surcharge when a shift touches midnight exactly: a shift
/// that ends at 00:00 has an empty second segment (00:00-00:00, zero hours) and must earn nothing for it, a 24 hour shift
/// from 00:00 to 00:00 is a full day on its weekday and earns positive surcharges for all of it, and a 00:00-00:00 window
/// that lasts zero hours (imported Hour is 0) earns no surcharge at all. Hand-computed against the default night window
/// 23:00-06:00 and the first rate profile of <see cref="MacroRegressionGrid"/> (night 0.10, holiday 0.50, weekend days 6
/// and 7 at 0.25 and 0.50). Against the AllShift text as it was before the fix (still in FixAllShiftMidnightSegments.Down),
/// the fixed script gives identical messages on every grid input whose window is not 00:00-00:00, and no channel of any
/// grid input is negative any more.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class SeededAllShiftMidnightTests
{
    private const double Tolerance = 1e-9;
    private const int Monday = 1;
    private const int Saturday = 6;
    private const int SurchargeChannelCount = 5;
    private const int FirstSurchargeChannel = 10;
    private const int ResultChannel = 1;
    private const string UpKeyPrefix = "FixAllShiftMidnightSegments:Up:";
    private const string DownKeyPrefix = "FixAllShiftMidnightSegments:Down:";
    private const string NonNightMarker = "NonNightHours";
    private const string FixedMarker = "NightHours = 0";

    private static readonly int[] SurchargeChannels = Enumerable.Range(FirstSurchargeChannel, SurchargeChannelCount).ToArray();

    [TestCase("16:00", "00:00", Monday, false, false, 8.0, 0.1, 0.0, 0.0, 0.0, 0.0)]
    [TestCase("16:00", "00:00", Saturday, false, false, 8.0, 0.0, 2.0, 0.0, 0.0, 0.0)]
    [TestCase("16:00", "00:00", Monday, true, false, 8.0, 0.0, 0.0, 0.0, 0.0, 4.0)]
    [TestCase("00:00", "00:00", Monday, false, false, 24.0, 0.7, 0.0, 0.0, 0.0, 0.0)]
    [TestCase("00:00", "00:00", Saturday, false, false, 24.0, 0.0, 6.0, 0.0, 0.0, 0.0)]
    [TestCase("00:00", "00:00", Monday, false, false, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
    [TestCase("00:00", "00:00", Saturday, false, false, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
    [TestCase("08:00", "08:00", Saturday, false, false, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
    public void AllShift_ShiftTouchingMidnight_EarnsTheHandComputedSurcharges(
        string from, string until, int weekday, bool holiday, bool holidayNextDay, double hours,
        double night, double weekend1, double weekend2, double weekend3, double holidayBonus)
    {
        var messages = Run(SeededMacroScripts.AllShiftScript(), Data(from, until, weekday, holiday, holidayNextDay, hours));

        messages[FirstSurchargeChannel].ShouldBe(night, Tolerance);
        messages[FirstSurchargeChannel + 1].ShouldBe(weekend1, Tolerance);
        messages[FirstSurchargeChannel + 2].ShouldBe(weekend2, Tolerance);
        messages[FirstSurchargeChannel + 3].ShouldBe(weekend3, Tolerance);
        messages[FirstSurchargeChannel + 4].ShouldBe(holidayBonus, Tolerance);
        messages[ResultChannel].ShouldBe(Math.Round(SurchargeChannels.Sum(channel => messages[channel]), 2), Tolerance);
    }

    [TestCase("16:00", "00:00", Monday, false, false, 8.0, 0.1, 0.0, 0.0, 0.0, 0.0)]
    [TestCase("16:00", "00:00", Monday, true, false, 8.0, 0.1, 0.0, 0.0, 0.0, 4.0)]
    [TestCase("00:00", "00:00", Monday, false, false, 24.0, 0.7, 0.0, 0.0, 0.0, 0.0)]
    [TestCase("00:00", "00:00", Saturday, false, false, 24.0, 0.7, 6.0, 0.0, 0.0, 0.0)]
    [TestCase("00:00", "00:00", Monday, false, false, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
    public void AllShiftAdditive_ShiftTouchingMidnight_EarnsTheHandComputedSurcharges(
        string from, string until, int weekday, bool holiday, bool holidayNextDay, double hours,
        double night, double weekend1, double weekend2, double weekend3, double holidayBonus)
    {
        var messages = Run(FixedAdditiveScript(), Data(from, until, weekday, holiday, holidayNextDay, hours));

        messages[FirstSurchargeChannel].ShouldBe(night, Tolerance);
        messages[FirstSurchargeChannel + 1].ShouldBe(weekend1, Tolerance);
        messages[FirstSurchargeChannel + 2].ShouldBe(weekend2, Tolerance);
        messages[FirstSurchargeChannel + 3].ShouldBe(weekend3, Tolerance);
        messages[FirstSurchargeChannel + 4].ShouldBe(holidayBonus, Tolerance);
    }

    [Test]
    public void AllShift_FixedScript_ChangesNothingOnTheGridExceptTheFullDayWindow()
    {
        var before = Compile(ScriptOfVariant(DownKeyPrefix, isAdditive: false, isFixed: false));
        var after = Compile(SeededMacroScripts.AllShiftScript());
        var changed = new List<int>();

        for (var index = 0; index < MacroRegressionGrid.Samples.Count; index++)
        {
            var data = MacroRegressionGrid.Samples[index].Data;
            var oldRun = MacroScriptRunner.Run(before, data, CancellationToken.None);
            var newRun = MacroScriptRunner.Run(after, data, CancellationToken.None);
            if (!MacroOutputGoldenRecorder.RawMessages(oldRun).SequenceEqual(MacroOutputGoldenRecorder.RawMessages(newRun)))
            {
                changed.Add(index);
            }
        }

        changed.ShouldNotBeEmpty();
        changed.ShouldAllBe(index =>
            MacroRegressionGrid.Samples[index].Data.FromHour == MacroRegressionGrid.Samples[index].Data.UntilHour);
    }

    [Test]
    public void FixedScripts_NeverEmitANegativeChannel_AndChannelOneIsTheRoundedSum()
    {
        var scripts = new[]
        {
            Compile(SeededMacroScripts.AllShiftScript()),
            Compile(FixedAdditiveScript())
        };

        foreach (var script in scripts)
        {
            foreach (var sample in MacroRegressionGrid.Samples)
            {
                var run = MacroScriptRunner.Run(script, sample.Data, CancellationToken.None);
                run.IsCompleted.ShouldBeTrue(run.Error);
                var messages = ToChannels(run);
                messages.Values.ShouldAllBe(value => value >= 0, sample.Description);
                messages[ResultChannel].ShouldBe(
                    Math.Round(SurchargeChannels.Sum(channel => messages[channel]), 2), Tolerance, sample.Description);
            }
        }
    }

    private static string FixedAdditiveScript() => ScriptOfVariant(UpKeyPrefix, isAdditive: true, isFixed: true);

    private static string ScriptOfVariant(string keyPrefix, bool isAdditive, bool isFixed) =>
        SeededMacroScripts.ShippedScriptVariants()
            .Where(variant => variant.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
            .Select(variant => variant.Content)
            .Distinct()
            .Single(content => content.Contains(NonNightMarker) != isAdditive && content.Contains(FixedMarker) == isFixed);

    private static CompiledScript Compile(string content)
    {
        var (compiled, error) = MacroScriptRunner.TryCompile(content);
        compiled.ShouldNotBeNull(error);
        return compiled;
    }

    private static MacroData Data(string from, string until, int weekday, bool holiday, bool holidayNextDay, double hours)
    {
        var template = MacroRegressionGrid.Samples[0].Data;
        return new MacroData
        {
            Hour = (decimal)hours,
            FromHour = from,
            UntilHour = until,
            Weekday = weekday,
            Holiday = holiday,
            HolidayNextDay = holidayNextDay,
            NightRate = template.NightRate,
            HolidayRate = template.HolidayRate,
            WE1Rate = template.WE1Rate,
            WE2Rate = template.WE2Rate,
            WE3Rate = template.WE3Rate,
            NightStart = template.NightStart,
            NightEnd = template.NightEnd,
            GuaranteedHours = template.GuaranteedHours,
            FullTime = template.FullTime,
            WorkloadPercent = template.WorkloadPercent,
            WeekendDay1 = template.WeekendDay1,
            WeekendDay2 = template.WeekendDay2,
            WeekendDay3 = template.WeekendDay3
        };
    }

    private static Dictionary<int, double> Run(string script, MacroData data)
    {
        var run = MacroScriptRunner.Run(Compile(script), data, CancellationToken.None);
        run.IsCompleted.ShouldBeTrue(run.Error);
        return ToChannels(run);
    }

    private static Dictionary<int, double> ToChannels(MacroScriptRun run) =>
        MacroOutputGoldenRecorder.RawMessages(run)
            .Select(message => message.Split(':'))
            .ToDictionary(
                parts => int.Parse(parts[0], CultureInfo.InvariantCulture),
                parts => double.Parse(parts[1], CultureInfo.InvariantCulture));
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Scripting;
using NUnit.Framework;

namespace Klacks.UnitTest.Scripting;

[TestFixture]
public class MacroAdditiveSurchargeTest
{
    // Mirrors the "AllShiftAdditive" macro seeded in migration AddAllShiftAdditiveMacro (K4): unlike
    // "AllShift", the night, weekend and holiday portions do not compete highest-wins — each applicable
    // portion is computed independently and they all stack. Any drift between this constant and the
    // migration's content is a bug in whichever was edited without the other.
    private const string AllShiftAdditiveMacro = @"
IMPORT Hour, FromHour, UntilHour
IMPORT Weekday, Holiday, HolidayNextDay
IMPORT NightRate, HolidayRate, WE1Rate, WE2Rate, WE3Rate
IMPORT NightStart, NightEnd
IMPORT WeekendDay1, WeekendDay2, WeekendDay3

FUNCTION SegBonusForType(StartTime, EndTime, HolidayFlag, WeekdayNum, WantType)
    DIM SegmentHours, NightHours, Amount
    DIM HasHoliday, IsWE1, IsWE2, IsWE3

    SegmentHours = TimeToHours(EndTime) - TimeToHours(StartTime)
    IF SegmentHours < 0 THEN SegmentHours = SegmentHours + 24 ENDIF

    NightHours = TimeOverlap(NightStart, NightEnd, StartTime, EndTime)

    HasHoliday = HolidayFlag = 1
    IsWE1 = WeekdayNum = WeekendDay1
    IsWE2 = WeekdayNum = WeekendDay2
    IsWE3 = WeekdayNum = WeekendDay3

    Amount = 0
    IF WantType = 10 THEN
        Amount = NightHours * NightRate
    ENDIF
    IF WantType = 11 THEN
        IF IsWE1 THEN Amount = SegmentHours * WE1Rate ENDIF
    ENDIF
    IF WantType = 12 THEN
        IF IsWE2 THEN Amount = SegmentHours * WE2Rate ENDIF
    ENDIF
    IF WantType = 13 THEN
        IF IsWE3 THEN Amount = SegmentHours * WE3Rate ENDIF
    ENDIF
    IF WantType = 14 THEN
        IF HasHoliday THEN Amount = SegmentHours * HolidayRate ENDIF
    ENDIF

    SegBonusForType = Amount
ENDFUNCTION

DIM TotalBonus, WeekdayNextDay
DIM BonusNight, BonusWeekend1, BonusWeekend2, BonusWeekend3, BonusHoliday

WeekdayNextDay = (Weekday MOD 7) + 1

IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN
    BonusNight = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 10) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 10)
    BonusWeekend1 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 11) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 11)
    BonusWeekend2 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 12) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 12)
    BonusWeekend3 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 13) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 13)
    BonusHoliday = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 14) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 14)
ELSE
    BonusNight = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 10)
    BonusWeekend1 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 11)
    BonusWeekend2 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 12)
    BonusWeekend3 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 13)
    BonusHoliday = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 14)
ENDIF

TotalBonus = BonusNight + BonusWeekend1 + BonusWeekend2 + BonusWeekend3 + BonusHoliday

OUTPUT 1, Round(TotalBonus, 2)
OUTPUT 10, BonusNight
OUTPUT 11, BonusWeekend1
OUTPUT 12, BonusWeekend2
OUTPUT 13, BonusWeekend3
OUTPUT 14, BonusHoliday
";

    private static List<ResultMessage> Run(
        string fromHour, string untilHour, int weekday, bool holiday, bool holidayNextDay,
        decimal nightRate, decimal holidayRate, decimal we1Rate, decimal we2Rate)
    {
        var compiled = CompiledScript.Compile(AllShiftAdditiveMacro);
        Assert.That(compiled.HasError, Is.False, $"Compile error: {compiled.Error?.Description}");

        compiled.SetExternalValue("hour", 0m);
        compiled.SetExternalValue("fromhour", fromHour);
        compiled.SetExternalValue("untilhour", untilHour);
        compiled.SetExternalValue("weekday", weekday);
        compiled.SetExternalValue("holiday", holiday ? 1 : 0);
        compiled.SetExternalValue("holidaynextday", holidayNextDay ? 1 : 0);
        compiled.SetExternalValue("nightrate", nightRate);
        compiled.SetExternalValue("holidayrate", holidayRate);
        compiled.SetExternalValue("we1rate", we1Rate);
        compiled.SetExternalValue("we2rate", we2Rate);
        compiled.SetExternalValue("we3rate", 0m);
        compiled.SetExternalValue("nightstart", "23:00");
        compiled.SetExternalValue("nightend", "06:00");
        compiled.SetExternalValue("weekendday1", 6);
        compiled.SetExternalValue("weekendday2", 7);
        compiled.SetExternalValue("weekendday3", 0);

        var context = new ScriptExecutionContext(compiled);
        var execResult = context.Execute();
        Assert.That(execResult.Success, Is.True, $"Runtime error: {execResult.Error?.Description}");
        return execResult.Messages;
    }

    private static decimal Amount(List<ResultMessage> messages, MacroTypeEnum type)
    {
        foreach (var msg in messages)
        {
            if (msg.Type == (int)type &&
                decimal.TryParse(msg.Message, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
        }
        return 0m;
    }

    [Test]
    public void SaturdayDayShift_YieldsPlainWeekend1Portion()
    {
        var messages = Run("08:00", "16:00", 6, false, false, 0.10m, 0.15m, 0.10m, 0.10m);

        Assert.That(Amount(messages, MacroTypeEnum.SurchargeWeekend1), Is.EqualTo(0.80m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeNight), Is.EqualTo(0m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeHoliday), Is.EqualTo(0m));
        Assert.That(Amount(messages, MacroTypeEnum.DefaultResult), Is.EqualTo(0.80m));
    }

    [Test]
    public void HolidayNightShiftCrossingMidnight_StacksNightAndHolidayInsteadOfHighestWins()
    {
        // 22:00–06:00 with both calendar days flagged as holiday. Night window 23:00–06:00 overlaps
        // 7 of the 8 hours; the holiday portion covers all 8 hours. Additive semantics pay BOTH:
        // 7 * 0.25 + 8 * 0.50 = 5.75, whereas the "AllShift" highest-wins cascade would pay every hour
        // at the single best rate only (8 * 0.50 = 4.00) and never stack night on top of holiday.
        var messages = Run("22:00", "06:00", 3, true, true, 0.25m, 0.50m, 0.10m, 0.10m);

        Assert.That(Amount(messages, MacroTypeEnum.SurchargeNight), Is.EqualTo(1.75m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeHoliday), Is.EqualTo(4.00m));
        Assert.That(Amount(messages, MacroTypeEnum.DefaultResult), Is.EqualTo(5.75m));
    }

    [Test]
    public void SundayHolidayDayShift_StacksWeekend2AndHoliday()
    {
        var messages = Run("09:00", "17:00", 7, true, false, 0.25m, 0.50m, 0.10m, 0.30m);

        Assert.That(Amount(messages, MacroTypeEnum.SurchargeWeekend2), Is.EqualTo(2.40m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeHoliday), Is.EqualTo(4.00m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeNight), Is.EqualTo(0m));
        Assert.That(Amount(messages, MacroTypeEnum.DefaultResult), Is.EqualTo(6.40m));
    }

    [Test]
    public void PlainWeekdayDayShift_YieldsNoSurcharges()
    {
        var messages = Run("09:00", "17:00", 3, false, false, 0.25m, 0.50m, 0.10m, 0.30m);

        Assert.That(Amount(messages, MacroTypeEnum.DefaultResult), Is.EqualTo(0m));
    }

    [Test]
    public void TypedPortions_SumRoundsToDefaultResult()
    {
        var messages = Run("20:00", "04:00", 6, false, true, 0.25m, 0.50m, 0.10m, 0.30m);

        var total = Amount(messages, MacroTypeEnum.DefaultResult);
        var typedSum = Amount(messages, MacroTypeEnum.SurchargeNight)
            + Amount(messages, MacroTypeEnum.SurchargeWeekend1)
            + Amount(messages, MacroTypeEnum.SurchargeWeekend2)
            + Amount(messages, MacroTypeEnum.SurchargeWeekend3)
            + Amount(messages, MacroTypeEnum.SurchargeHoliday);

        Assert.That(Math.Round(typedSum, 2), Is.EqualTo(total),
            "Sum of typed surcharge portions must round to the DefaultResult total");
    }
}

using System.Globalization;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Scripting;
using NUnit.Framework;

namespace Klacks.UnitTest.Scripting;

[TestFixture]
public class MacroTypedSurchargeTest
{
    // Mirrors the "AllShift" surcharge macro seeded in MacrosSeed.cs. Emits the
    // rounded total (DefaultResult=1) plus one unrounded typed portion per
    // surcharge category (10=Night, 11=Weekend1, 12=Weekend2, 14=Holiday).
    private const string AllShiftMacro = @"
import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import we1rate
import we2rate

FUNCTION SegBonusForType(StartTime, EndTime, HolidayFlag, WeekdayNum, WantType)
    DIM SegmentHours, NightHours, NonNightHours, Amount
    DIM NRate, DRate, NType, DType, HasHoliday, IsSaturday, IsSunday

    SegmentHours = TimeToHours(EndTime) - TimeToHours(StartTime)
    IF SegmentHours < 0 THEN SegmentHours = SegmentHours + 24 ENDIF

    NightHours = TimeOverlap(""23:00"", ""06:00"", StartTime, EndTime)
    NonNightHours = SegmentHours - NightHours

    HasHoliday = HolidayFlag = 1
    IsSaturday = WeekdayNum = 6
    IsSunday = WeekdayNum = 7

    NRate = 0
    NType = 0
    IF NightHours > 0 THEN
        NRate = NightRate
        NType = 10
    ENDIF
    IF HasHoliday AndAlso HolidayRate > NRate THEN
        NRate = HolidayRate
        NType = 14
    ENDIF
    IF IsSaturday AndAlso WE1Rate > NRate THEN
        NRate = WE1Rate
        NType = 11
    ENDIF
    IF IsSunday AndAlso WE2Rate > NRate THEN
        NRate = WE2Rate
        NType = 12
    ENDIF

    DRate = 0
    DType = 0
    IF HasHoliday AndAlso HolidayRate > DRate THEN
        DRate = HolidayRate
        DType = 14
    ENDIF
    IF IsSaturday AndAlso WE1Rate > DRate THEN
        DRate = WE1Rate
        DType = 11
    ENDIF
    IF IsSunday AndAlso WE2Rate > DRate THEN
        DRate = WE2Rate
        DType = 12
    ENDIF

    Amount = 0
    IF NType = WantType THEN Amount = Amount + NightHours * NRate ENDIF
    IF DType = WantType THEN Amount = Amount + NonNightHours * DRate ENDIF

    SegBonusForType = Amount
ENDFUNCTION

DIM TotalBonus, WeekdayNextDay
DIM BonusNight, BonusWeekend1, BonusWeekend2, BonusHoliday

WeekdayNextDay = (Weekday MOD 7) + 1

IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN
    BonusNight = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 10) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 10)
    BonusWeekend1 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 11) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 11)
    BonusWeekend2 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 12) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 12)
    BonusHoliday = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 14) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 14)
ELSE
    BonusNight = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 10)
    BonusWeekend1 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 11)
    BonusWeekend2 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 12)
    BonusHoliday = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 14)
ENDIF

TotalBonus = BonusNight + BonusWeekend1 + BonusWeekend2 + BonusHoliday

OUTPUT 1, Round(TotalBonus, 2)
OUTPUT 10, BonusNight
OUTPUT 11, BonusWeekend1
OUTPUT 12, BonusWeekend2
OUTPUT 14, BonusHoliday
";

    private static List<ResultMessage> Run(
        string fromHour, string untilHour, int weekday, bool holiday, bool holidayNextDay,
        decimal nightRate, decimal holidayRate, decimal we1Rate, decimal we2Rate)
    {
        var compiled = CompiledScript.Compile(AllShiftMacro);
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

    [TestCase("08:00", "16:00", 6, false, false, 0.10, 0.15, 0.10, 0.10, TestName = "SaturdayDayShift")]
    [TestCase("22:00", "06:00", 3, false, false, 0.25, 0.15, 0.10, 0.10, TestName = "WeekdayNightShiftCrossMidnight")]
    [TestCase("20:00", "04:00", 7, false, true, 0.25, 0.50, 0.10, 0.30, TestName = "SundayNightIntoHolidayMultiType")]
    [TestCase("09:00", "17:00", 3, false, false, 0.25, 0.15, 0.10, 0.10, TestName = "PlainWeekdayNoSurcharge")]
    public void TypedSurcharges_SumRoundsToDefaultResult(
        string fromHour, string untilHour, int weekday, bool holiday, bool holidayNextDay,
        decimal nightRate, decimal holidayRate, decimal we1Rate, decimal we2Rate)
    {
        var messages = Run(fromHour, untilHour, weekday, holiday, holidayNextDay,
            nightRate, holidayRate, we1Rate, we2Rate);

        var total = Amount(messages, MacroTypeEnum.DefaultResult);
        var typedSum = Amount(messages, MacroTypeEnum.SurchargeNight)
            + Amount(messages, MacroTypeEnum.SurchargeWeekend1)
            + Amount(messages, MacroTypeEnum.SurchargeWeekend2)
            + Amount(messages, MacroTypeEnum.SurchargeHoliday);

        Assert.That(Math.Round(typedSum, 2), Is.EqualTo(total),
            "Sum of typed surcharge portions must round to the DefaultResult total");
    }

    [Test]
    public void SaturdayDayShift_AttributesEntirelyToWeekend1()
    {
        var messages = Run("08:00", "16:00", 6, false, false, 0.10m, 0.15m, 0.10m, 0.10m);

        Assert.That(Amount(messages, MacroTypeEnum.SurchargeWeekend1), Is.EqualTo(0.80m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeNight), Is.EqualTo(0m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeWeekend2), Is.EqualTo(0m));
        Assert.That(Amount(messages, MacroTypeEnum.SurchargeHoliday), Is.EqualTo(0m));
    }
}

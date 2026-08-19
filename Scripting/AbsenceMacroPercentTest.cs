// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Executes the reworked absence macro sources (Vacation shape, the 50% variant, Accident shape and
/// Paid Absence) exactly as seeded by MacrosSeed / the two wiring migrations: the daily credit is
/// Hour * Percent / 100 (month independent), the CONFIGURED weekend days (imports weekendday1/2/3,
/// unused slots 0) and holidays credit nothing, and GuaranteedHours = 0 (on-call contracts) still
/// credits nothing regardless of the percent. Paid Absence credits exactly the recorded time span
/// (midnight wrap included); the schedule's full-day marker 00:00-23:59 and missing times credit 0
/// per owner ruling 2026-08-19.
/// </summary>

using System.Globalization;
using Klacks.Api.Infrastructure.Scripting;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Scripting;

[TestFixture]
public class AbsenceMacroPercentTest
{
    private const string VacationMacro = @"import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import we1rate
import we2rate
import guaranteedhours
import fulltime
import percent
import weekendday1
import weekendday2
import weekendday3

IF Weekday = WeekendDay1 OR Weekday = WeekendDay2 OR Weekday = WeekendDay3 OR Holiday THEN
	Hour = 0
ELSE
	IF GuaranteedHours > 0 THEN
		Hour = Hour * Percent / 100
	ELSE
		Hour = 0
	END IF
END IF

OUTPUT 1, Hour";

    private const string Vacation50Macro = @"import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import we1rate
import we2rate
import guaranteedhours
import fulltime
import percent
import weekendday1
import weekendday2
import weekendday3

IF Weekday = WeekendDay1 OR Weekday = WeekendDay2 OR Weekday = WeekendDay3 OR Holiday THEN
	Hour = 0
ELSE
	IF GuaranteedHours > 0 THEN
		Hour = Hour * Percent / 100
		Hour = Hour / 2
	ELSE
		Hour = 0
	END IF
END IF

OUTPUT 1, Hour";

    private const string AccidentMacro = @"import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import we1rate
import we2rate
import guaranteedhours
import fulltime
import percent

IF GuaranteedHours > 0 THEN
	Hour = Hour * Percent / 100
ELSE
	Hour = 0
END IF

OUTPUT 1, Hour";

    public const string PaidAbsenceMacro = @"import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import we1rate
import we2rate
import guaranteedhours
import fulltime
import percent

DIM Duration
Duration = TimeToHours(UntilHour) - TimeToHours(FromHour)
IF Duration < 0 THEN
	Duration = Duration + 24
END IF
IF Duration >= 23.9 THEN
	Duration = 0
END IF

OUTPUT 1, Duration";

    [Test]
    public void Vacation_Weekday_FullWorkload_CreditsFullDailyHours()
    {
        var result = Run(VacationMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 184m, percent: 100m);

        result.ShouldBe(8.5m);
    }

    [Test]
    public void Vacation_Weekday_PartTimePercent_ScalesDailyHours()
    {
        var result = Run(VacationMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 110.4m, percent: 60m);

        result.ShouldBe(5.1m);
    }

    [Test]
    public void Vacation_MonthlyValueVaries_CreditIsMonthIndependent()
    {
        var february = Run(VacationMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 168m, percent: 100m);
        var march = Run(VacationMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 184m, percent: 100m);

        february.ShouldBe(8.5m);
        march.ShouldBe(8.5m);
    }

    [Test]
    public void Vacation_Saturday_CreditsNothing()
    {
        var result = Run(VacationMacro, hour: 8.5m, weekday: 6, holiday: false, guaranteedHours: 184m, percent: 100m);

        result.ShouldBe(0m);
    }

    [Test]
    public void Vacation_Holiday_CreditsNothing()
    {
        var result = Run(VacationMacro, hour: 8.5m, weekday: 3, holiday: true, guaranteedHours: 184m, percent: 100m);

        result.ShouldBe(0m);
    }

    [Test]
    public void Vacation_OnCallContractWithZeroGuaranteedHours_CreditsNothing()
    {
        var result = Run(VacationMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 0m, percent: 100m);

        result.ShouldBe(0m);
    }

    [Test]
    public void Vacation50_Weekday_CreditsHalfTheScaledHours()
    {
        var result = Run(Vacation50Macro, hour: 8m, weekday: 3, holiday: false, guaranteedHours: 184m, percent: 50m);

        result.ShouldBe(2m);
    }

    [Test]
    public void Accident_Weekend_StillCredits()
    {
        var result = Run(AccidentMacro, hour: 8m, weekday: 6, holiday: false, guaranteedHours: 184m, percent: 100m);

        result.ShouldBe(8m);
    }

    [Test]
    public void Accident_FullTimeEmployee_PercentHundred_NoLongerDependsOnFullTimeRatio()
    {
        var result = Run(AccidentMacro, hour: 8m, weekday: 3, holiday: false, guaranteedHours: 168m, percent: 100m);

        result.ShouldBe(8m);
    }

    [Test]
    public void Vacation_ExplicitPartTimeRatio_MatchesOwnerTable()
    {
        var partTimePercent = 160m / 180m * 100m;

        var result = Run(VacationMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 160m, percent: partTimePercent);

        result.ShouldBe(7.5556m, 0.001m);
    }

    [Test]
    public void PaidAbsence_RecordedTimeSpan_CreditsSpanRegardlessOfWorkload()
    {
        var result = Run(
            PaidAbsenceMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 110.4m, percent: 60m,
            fromHour: "09:00", untilHour: "09:30");

        result.ShouldBe(0.5m);
    }

    [Test]
    public void PaidAbsence_MidnightCrossingSpan_WrapsAndCreditsSpan()
    {
        var result = Run(
            PaidAbsenceMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 184m, percent: 100m,
            fromHour: "22:00", untilHour: "02:00");

        result.ShouldBe(4m);
    }

    [Test]
    public void PaidAbsence_FullDayMarkerAsBookedBySchedule_CreditsNothing()
    {
        // A full-day absence reaches the macro as 00:00-23:59: calculateBreakTimes
        // (schedule-entry-actions.service.ts) books '00:00:00'-'23:59:00' whenever no concrete
        // time range is recorded, and MacroDataProvider formats those TimeOnly values as HH:mm.
        var result = Run(
            PaidAbsenceMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 184m, percent: 100m,
            fromHour: "00:00", untilHour: "23:59");

        result.ShouldBe(0m);
    }

    [Test]
    public void PaidAbsence_NoTimesRecorded_CreditsNothing()
    {
        var result = Run(
            PaidAbsenceMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 184m, percent: 100m,
            fromHour: "00:00", untilHour: "00:00");

        result.ShouldBe(0m);
    }

    [Test]
    public void Vacation_ConfiguredFridaySaturdayWeekend_FridayCreditsNothing()
    {
        var result = Run(
            VacationMacro, hour: 8.5m, weekday: 5, holiday: false, guaranteedHours: 184m, percent: 100m,
            weekendDay1: 5, weekendDay2: 6, weekendDay3: 0);

        result.ShouldBe(0m);
    }

    [Test]
    public void Vacation_ConfiguredFridaySaturdayWeekend_SundayIsAWorkdayAndCredits()
    {
        // The old hardcoded Weekday = 6 OR Weekday = 7 check would have zeroed Sunday here.
        var result = Run(
            VacationMacro, hour: 8.5m, weekday: 7, holiday: false, guaranteedHours: 184m, percent: 100m,
            weekendDay1: 5, weekendDay2: 6, weekendDay3: 0);

        result.ShouldBe(8.5m);
    }

    [Test]
    public void Vacation_UnusedWeekendSlots_NeverMatchAWeekday()
    {
        var result = Run(
            VacationMacro, hour: 8.5m, weekday: 3, holiday: false, guaranteedHours: 184m, percent: 100m,
            weekendDay1: 5, weekendDay2: 0, weekendDay3: 0);

        result.ShouldBe(8.5m);
    }

    [Test]
    public void Vacation50_ConfiguredThreeDayWeekend_ThirdSlotZeroes()
    {
        var result = Run(
            Vacation50Macro, hour: 8m, weekday: 7, holiday: false, guaranteedHours: 184m, percent: 100m,
            weekendDay1: 5, weekendDay2: 6, weekendDay3: 7);

        result.ShouldBe(0m);
    }

    private static decimal Run(
        string script, decimal hour, int weekday, bool holiday, decimal guaranteedHours, decimal percent,
        string fromHour = "00:00", string untilHour = "00:00",
        int weekendDay1 = 6, int weekendDay2 = 7, int weekendDay3 = 0)
    {
        var compiled = CompiledScript.Compile(script);
        compiled.HasError.ShouldBeFalse(compiled.Error?.Description);

        compiled.SetExternalValue("hour", hour);
        compiled.SetExternalValue("fromhour", fromHour);
        compiled.SetExternalValue("untilhour", untilHour);
        compiled.SetExternalValue("weekday", weekday);
        compiled.SetExternalValue("holiday", holiday ? 1 : 0);
        compiled.SetExternalValue("holidaynextday", 0);
        compiled.SetExternalValue("nightrate", 0m);
        compiled.SetExternalValue("holidayrate", 0m);
        compiled.SetExternalValue("we1rate", 0m);
        compiled.SetExternalValue("we2rate", 0m);
        compiled.SetExternalValue("guaranteedhours", guaranteedHours);
        compiled.SetExternalValue("fulltime", 180m);
        compiled.SetExternalValue("percent", percent);
        compiled.SetExternalValue("weekendday1", weekendDay1);
        compiled.SetExternalValue("weekendday2", weekendDay2);
        compiled.SetExternalValue("weekendday3", weekendDay3);

        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        result.Success.ShouldBeTrue(result.Error?.Description);
        var output = result.Messages.FirstOrDefault(m => m.Type == 1);
        output.ShouldNotBeNull();
        return decimal.Parse(output!.Message, NumberStyles.Any, CultureInfo.InvariantCulture);
    }
}

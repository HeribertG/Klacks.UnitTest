using Klacks.Api.Infrastructure.Scripting;
using NUnit.Framework;

namespace Klacks.UnitTest.Scripting;

[TestFixture]
public class MacroCompilationTest
{
    [Test]
    public void CompileAndRunMacro_ShouldWork()
    {
        // Arrange
        var script = @"
import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import sarate
import sorate
import guaranteedhours
import fulltime

FUNCTION CalcSegment(StartTime, EndTime, HolidayFlag, WeekdayNum)
      DIM SegmentHours, NightHours, NonNightHours
      DIM NRate, DRate, HasHoliday, IsSaturday, IsSunday

      SegmentHours = TimeToHours(EndTime) - TimeToHours(StartTime)
      IF SegmentHours < 0 THEN SegmentHours = SegmentHours + 24 ENDIF

      NightHours = TimeOverlap(""23:00"", ""06:00"", StartTime, EndTime)
      NonNightHours = SegmentHours - NightHours

      HasHoliday = HolidayFlag = 1
      IsSaturday = WeekdayNum = 6
      IsSunday = WeekdayNum = 7

      NRate = 0
      IF NightHours > 0 THEN NRate = NightRate ENDIF
      IF HasHoliday AndAlso HolidayRate > NRate THEN NRate = HolidayRate ENDIF
      IF IsSaturday AndAlso SaRate > NRate THEN NRate = SaRate ENDIF
      IF IsSunday AndAlso SoRate > NRate THEN NRate = SoRate ENDIF

      DRate = 0
      IF HasHoliday AndAlso HolidayRate > DRate THEN DRate = HolidayRate ENDIF
      IF IsSaturday AndAlso SaRate > DRate THEN DRate = SaRate ENDIF
      IF IsSunday AndAlso SoRate > DRate THEN DRate = SoRate ENDIF

      CalcSegment = NightHours * NRate + NonNightHours * DRate
  ENDFUNCTION

  DIM TotalBonus, WeekdayNextDay

  WeekdayNextDay = (Weekday MOD 7) + 1

  IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN
      TotalBonus = CalcSegment(FromHour, ""00:00"", Holiday, Weekday)
      TotalBonus = TotalBonus + CalcSegment(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay)
  ELSE
      TotalBonus = CalcSegment(FromHour, UntilHour, Holiday, Weekday)
  ENDIF

  OUTPUT 1, Round(TotalBonus, 2)
";

        // Act - Compile
        var compiled = CompiledScript.Compile(script);

        // Assert - Compilation
        Console.WriteLine($"HasError: {compiled.HasError}");

        if (compiled.HasError)
        {
            Console.WriteLine($"Error Code: {compiled.Error!.Code}");
            Console.WriteLine($"Error Description: {compiled.Error.Description}");
            Console.WriteLine($"Error Line: {compiled.Error.Line}");
            Console.WriteLine($"Error Column: {compiled.Error.Column}");
            Assert.Fail($"Compilation failed: {compiled.Error.Description}");
        }

        Console.WriteLine($"Instructions count: {compiled.Instructions.Count}");
        Console.WriteLine($"External symbols: {string.Join(", ", compiled.ExternalSymbols.Keys)}");

        // Act - Set external values
        compiled.SetExternalValue("hour", 8m);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:00");
        compiled.SetExternalValue("weekday", 6);  // Saturday
        compiled.SetExternalValue("holiday", false);
        compiled.SetExternalValue("holidaynextday", false);
        compiled.SetExternalValue("nightrate", 0.1m);
        compiled.SetExternalValue("holidayrate", 0.15m);
        compiled.SetExternalValue("sarate", 0.1m);
        compiled.SetExternalValue("sorate", 0.1m);
        compiled.SetExternalValue("guaranteedhours", 160m);
        compiled.SetExternalValue("fulltime", 180m);

        // Act - Execute
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert - Execution
        Console.WriteLine($"Execution Success: {result.Success}");

        if (!result.Success && result.Error != null)
        {
            Console.WriteLine($"Runtime Error Code: {result.Error.Code}");
            Console.WriteLine($"Runtime Error Description: {result.Error.Description}");
            Console.WriteLine($"Runtime Error Line: {result.Error.Line}");
            Assert.Fail($"Execution failed: {result.Error.Description}");
        }

        Console.WriteLine($"Messages count: {result.Messages.Count}");
        foreach (var msg in result.Messages)
        {
            Console.WriteLine($"  Output Type={msg.Type}, Value={msg.Message}");
        }

        Assert.That(result.Success, Is.True);
        Assert.That(result.Messages, Is.Not.Empty);
    }
}

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

    [Test]
    public void WorkMacroServiceFlow_CacheCloneSetRun_ShouldWork()
    {
        var script = @"
import hour
import fromhour
import untilhour
import weekday
import holiday
import nightrate

dim result
result = hour * nightrate
output 1, result
";

        var cache = new MacroCache();
        var macroId = Guid.NewGuid();

        var cached = cache.GetOrCompile(macroId, script);
        Assert.That(cached.HasError, Is.False, $"Compile error: {cached.Error?.Description}");

        Console.WriteLine($"Cached ExternalSymbols: [{string.Join(", ", cached.ExternalSymbols.Keys)}]");

        var clone = cached.CloneForExecution();
        Console.WriteLine($"Cloned ExternalSymbols before set: [{string.Join(", ", clone.ExternalSymbols.Keys)}]");

        clone.SetExternalValue("hour", 8.0m);
        clone.SetExternalValue("fromhour", "08:00");
        clone.SetExternalValue("untilhour", "16:00");
        clone.SetExternalValue("weekday", 3);
        clone.SetExternalValue("holiday", 0);
        clone.SetExternalValue("nightrate", 0.25m);

        Console.WriteLine($"Cloned ExternalSymbols after set: [{string.Join(", ", clone.ExternalSymbols.Keys)}]");

        var engine = new MacroEngine();
        var results = engine.RunWithScript(clone);

        Console.WriteLine($"ErrorNumber: {engine.ErrorNumber}");
        Console.WriteLine($"ErrorCode: {engine.ErrorCode}");
        Console.WriteLine($"Results count: {results.Count}");
        foreach (var msg in results)
        {
            Console.WriteLine($"  Type={msg.Type}, Message={msg.Message}");
        }

        Assert.That(engine.ErrorNumber, Is.EqualTo(0));
        Assert.That(results.Count, Is.GreaterThan(0));
        Assert.That(results[0].Message, Is.EqualTo("2"));
    }

    [Test]
    public void NumberLiteralComparison_ShouldWorkCorrectly()
    {
        var script = @"
import weekday

dim result
result = 0
if weekday = 6 then
  result = 100
endif
output 1, result
";

        var compiled = CompiledScript.Compile(script);
        Assert.That(compiled.HasError, Is.False);

        compiled.SetExternalValue("weekday", 6);

        var context = new ScriptExecutionContext(compiled);
        var execResult = context.Execute();

        Console.WriteLine($"Success: {execResult.Success}");
        foreach (var msg in execResult.Messages)
        {
            Console.WriteLine($"  Type={msg.Type}, Message={msg.Message}");
        }

        Assert.That(execResult.Success, Is.True);
        Assert.That(execResult.Messages.Count, Is.GreaterThan(0));
        Assert.That(execResult.Messages[0].Message, Is.EqualTo("100"));
    }

    [Test]
    public void BooleanExternalVariable_EqualityComparison_ShouldWork()
    {
        var script = @"
import holiday

dim result
result = 0
if holiday = 1 then
  result = 50
endif
output 1, result
";

        var compiled = CompiledScript.Compile(script);
        Assert.That(compiled.HasError, Is.False);

        compiled.SetExternalValue("holiday", 1);

        var context = new ScriptExecutionContext(compiled);
        var execResult = context.Execute();

        Console.WriteLine($"Success: {execResult.Success}");
        foreach (var msg in execResult.Messages)
        {
            Console.WriteLine($"  Type={msg.Type}, Message={msg.Message}");
        }

        Assert.That(execResult.Success, Is.True);
        Assert.That(execResult.Messages.Count, Is.GreaterThan(0));
        Assert.That(execResult.Messages[0].Message, Is.EqualTo("50"));
    }

    [Test]
    public void DecimalExternalVariable_Arithmetic_ShouldWork()
    {
        var script = @"
import hour
import nightrate

dim result
result = hour * nightrate
output 1, result
";

        var compiled = CompiledScript.Compile(script);
        Assert.That(compiled.HasError, Is.False);

        compiled.SetExternalValue("hour", 8.5m);
        compiled.SetExternalValue("nightrate", 0.25m);

        var context = new ScriptExecutionContext(compiled);
        var execResult = context.Execute();

        Console.WriteLine($"Success: {execResult.Success}");
        foreach (var msg in execResult.Messages)
        {
            Console.WriteLine($"  Type={msg.Type}, Message={msg.Message}");
        }

        Assert.That(execResult.Success, Is.True);
        Assert.That(execResult.Messages.Count, Is.GreaterThan(0));
        Assert.That(execResult.Messages[0].Message, Is.EqualTo("2.125"));
    }
}

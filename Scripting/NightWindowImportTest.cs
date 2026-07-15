using System.Linq;
using Klacks.Api.Infrastructure.Scripting;
using NUnit.Framework;

namespace Klacks.UnitTest.Scripting;

[TestFixture]
public class NightWindowImportTest
{
    // Minimal mirror of the night-hours computation inside the seeded "AllShift" macro
    // (MacrosSeed.cs), proving that NightStart/NightEnd can be supplied as IMPORT
    // variables instead of the historical "23:00"/"06:00" string literals, including
    // across a midnight-crossing window (e.g. the AE default 22:00-04:00).
    private const string NightHoursMacro = @"
import fromhour
import untilhour
import nightstart
import nightend

DIM NightHours
NightHours = TimeOverlap(NightStart, NightEnd, FromHour, UntilHour)

OUTPUT 1, NightHours
";

    private static decimal RunNightHours(string fromHour, string untilHour, string nightStart, string nightEnd)
    {
        var compiled = CompiledScript.Compile(NightHoursMacro);
        Assert.That(compiled.HasError, Is.False, $"Compile error: {compiled.Error?.Description}");

        compiled.SetExternalValue("fromhour", fromHour);
        compiled.SetExternalValue("untilhour", untilHour);
        compiled.SetExternalValue("nightstart", nightStart);
        compiled.SetExternalValue("nightend", nightEnd);

        var context = new ScriptExecutionContext(compiled);
        var execResult = context.Execute();
        Assert.That(execResult.Success, Is.True, $"Runtime error: {execResult.Error?.Description}");

        var message = execResult.Messages.Single(m => m.Type == 1);
        return decimal.Parse(message.Message, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Test]
    public void DefaultWindow_ShiftFullyInsideWindow_ReturnsFullOverlap()
    {
        var hours = RunNightHours("23:00", "05:00", "23:00", "06:00");

        Assert.That(hours, Is.EqualTo(6m));
    }

    [Test]
    public void AeWindow_CrossesMidnight_MatchesConfiguredTwentyTwoToFour()
    {
        var hours = RunNightHours("20:00", "06:00", "22:00", "04:00");

        Assert.That(hours, Is.EqualTo(6m));
    }

    [Test]
    public void JpWindow_TwentyTwoToFive_ShiftOutsideWindow_ReturnsZero()
    {
        var hours = RunNightHours("08:00", "16:00", "22:00", "05:00");

        Assert.That(hours, Is.EqualTo(0m));
    }
}

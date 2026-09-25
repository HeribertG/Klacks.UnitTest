// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroDataImportBinder: every MacroData field reaches the script under the import name
/// production macros use, booleans as 1/0, time-of-day values as strings. A characterization test pins
/// that the production execution path (MacroCompilationService with the real engine) still receives the
/// bound values and maps channel 1 and channels 10-14 as before.
/// </summary>

using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroDataImportBinderTests
{
    private const string AllImportsScript =
        "IMPORT Hour, FromHour, UntilHour, Weekday, Holiday, HolidayNextDay\n"
        + "IMPORT NightRate, HolidayRate, WE1Rate, WE2Rate, WE3Rate, NightStart, NightEnd\n"
        + "IMPORT GuaranteedHours, FullTime, Percent, WeekendDay1, WeekendDay2, WeekendDay3\n"
        + "OUTPUT 1, 0";

    private const string ChannelProbeScript =
        "IMPORT Hour, FromHour, Weekday, Holiday, HolidayNextDay, Percent, WeekendDay1\n"
        + "OUTPUT 1, Hour + TimeToHours(FromHour)\n"
        + "OUTPUT 10, Weekday\n"
        + "OUTPUT 11, Holiday * 10 + HolidayNextDay\n"
        + "OUTPUT 12, Percent\n"
        + "OUTPUT 14, WeekendDay1";

    [Test]
    public void Bind_SetsEveryImportFromMacroData()
    {
        var script = CompiledScript.Compile(AllImportsScript);
        script.HasError.ShouldBeFalse(script.Error?.Description);

        MacroDataImportBinder.Bind(script, CreateSampleData());

        var symbols = script.ExternalSymbols;
        symbols["hour"].Value.AsDouble().ShouldBe(8d);
        symbols["fromhour"].Value.AsString().ShouldBe("22:00");
        symbols["untilhour"].Value.AsString().ShouldBe("06:00");
        symbols["weekday"].Value.AsInt().ShouldBe(6);
        symbols["holiday"].Value.AsInt().ShouldBe(1);
        symbols["holidaynextday"].Value.AsInt().ShouldBe(0);
        symbols["nightrate"].Value.AsDouble().ShouldBe(0.1d, 1e-9);
        symbols["holidayrate"].Value.AsDouble().ShouldBe(0.5d, 1e-9);
        symbols["we1rate"].Value.AsDouble().ShouldBe(0.25d, 1e-9);
        symbols["we2rate"].Value.AsDouble().ShouldBe(0.3d, 1e-9);
        symbols["we3rate"].Value.AsDouble().ShouldBe(0.4d, 1e-9);
        symbols["nightstart"].Value.AsString().ShouldBe("23:00");
        symbols["nightend"].Value.AsString().ShouldBe("06:00");
        symbols["guaranteedhours"].Value.AsDouble().ShouldBe(160d);
        symbols["fulltime"].Value.AsDouble().ShouldBe(42d);
        symbols["percent"].Value.AsDouble().ShouldBe(60d);
        symbols["weekendday1"].Value.AsInt().ShouldBe(6);
        symbols["weekendday2"].Value.AsInt().ShouldBe(7);
        symbols["weekendday3"].Value.AsInt().ShouldBe(0);
    }

    [Test]
    public async Task ProductionExecutionPath_RunsTheScriptWithTheBoundValues()
    {
        var macroId = Guid.NewGuid();
        var management = Substitute.For<IMacroManagementService>();
        management.GetMacroAsync(macroId).Returns(new Macro { Id = macroId, Name = "probe", Content = ChannelProbeScript });
        var service = new MacroCompilationService(
            management, new MacroCache(), new MacroEngine(), NullLogger<MacroCompilationService>.Instance);

        var result = await service.CompileAndExecuteAsync(macroId, CreateSampleData());

        result.Success.ShouldBeTrue();
        result.ResultValue.ShouldBe(30m);
        result.Surcharges.ShouldBe(
        [
            new MacroSurchargeItem(SurchargeType.Night, 6m),
            new MacroSurchargeItem(SurchargeType.Weekend1, 10m),
            new MacroSurchargeItem(SurchargeType.Weekend2, 60m),
            new MacroSurchargeItem(SurchargeType.Holiday, 6m)
        ]);
    }

    private static MacroData CreateSampleData() => new()
    {
        Hour = 8m,
        FromHour = "22:00",
        UntilHour = "06:00",
        Weekday = 6,
        Holiday = true,
        HolidayNextDay = false,
        NightRate = 0.1m,
        HolidayRate = 0.5m,
        WE1Rate = 0.25m,
        WE2Rate = 0.3m,
        WE3Rate = 0.4m,
        NightStart = "23:00",
        NightEnd = "06:00",
        GuaranteedHours = 160m,
        FullTime = 42m,
        WorkloadPercent = 60m,
        WeekendDay1 = 6,
        WeekendDay2 = 7,
        WeekendDay3 = 0
    };
}

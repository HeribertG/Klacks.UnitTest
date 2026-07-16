// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the shared macro script validator: a valid script passes, a script that fails
/// during probe execution reports a runtime error, and malformed input that hangs the script
/// parser ("DIM 123abc") is rejected via the hard wall-clock timeout instead of freezing the caller.
/// </summary>

using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroScriptValidatorTests
{
    private MacroScriptValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new MacroScriptValidator();
    }

    [Test]
    public void Validate_ValidScript_ReturnsValid()
    {
        var result = _validator.Validate("import hour\nOUTPUT 1, Hour");

        result.IsValid.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();
    }

    [Test]
    public void Validate_ScriptWithTimeOfDayImports_ReturnsValid()
    {
        var result = _validator.Validate("import fromhour\nimport untilhour\nOUTPUT 1, 0");

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_RuntimeError_ReturnsFailureWithRuntimeError()
    {
        var result = _validator.Validate("OUTPUT 1, NoSuchFunction(1)");

        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_HistoricallyParserHangingScript_FailsFast()
    {
        // "DIM 123abc" used to loop the SyntaxAnalyser forever; a parser hardening now reports a
        // compile error instead. Either way the validator must reject it quickly - as a compile
        // error or, should the parser regress, via the hard wall-clock timeout.
        var started = DateTime.UtcNow;

        var result = _validator.Validate("DIM 123abc");

        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}

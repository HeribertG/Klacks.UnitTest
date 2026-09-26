// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// EXIT DO leaves the innermost DO loop, in every loop form (plain DO, DO WHILE, LOOP UNTIL, LOOP WHILE), from inside an IF,
/// from a nested loop (only the inner one ends) and from inside a FUNCTION, and it leaves the interpreter stack balanced
/// (the variables and the return value read after the loop are intact). It is refused at compile time outside a DO loop.
/// The compiler used to test "exits allowed + EXIT DO = EXIT DO" instead of masking the allowed exits, so EXIT DO was
/// refused inside every loop and only accepted where no loop was open.
/// </summary>

using Klacks.Api.Infrastructure.Scripting;

namespace Klacks.UnitTest.Scripting;

[TestFixture]
public class ExitDoStatementTests
{
    private const string OutputOne = "OUTPUT 1, Counter";

    [Test]
    public void ExitDo_InsideIf_LeavesThePlainLoop()
    {
        var messages = Run(@"DIM Counter
Counter = 0
DO
    Counter = Counter + 1
    IF Counter >= 3 THEN EXIT DO ENDIF
LOOP
" + OutputOne);

        messages.ShouldBe(new[] { "3" });
    }

    [Test]
    public void ExitDo_InDoWhile_LeavesTheLoopBeforeItsConditionEnds()
    {
        var messages = Run(@"DIM Counter
Counter = 0
DO WHILE Counter < 100
    Counter = Counter + 1
    IF Counter >= 4 THEN EXIT DO ENDIF
LOOP
" + OutputOne);

        messages.ShouldBe(new[] { "4" });
    }

    [Test]
    public void ExitDo_InLoopUntil_LeavesTheLoopBeforeItsConditionEnds()
    {
        var messages = Run(@"DIM Counter
Counter = 0
DO
    Counter = Counter + 1
    IF Counter >= 2 THEN EXIT DO ENDIF
LOOP UNTIL Counter >= 100
" + OutputOne);

        messages.ShouldBe(new[] { "2" });
    }

    [Test]
    public void ExitDo_InLoopWhile_LeavesTheLoopBeforeItsConditionEnds()
    {
        var messages = Run(@"DIM Counter
Counter = 0
DO
    Counter = Counter + 1
    IF Counter >= 5 THEN EXIT DO ENDIF
LOOP WHILE Counter < 100
" + OutputOne);

        messages.ShouldBe(new[] { "5" });
    }

    [Test]
    public void ExitDo_InANestedLoop_EndsOnlyTheInnerLoop()
    {
        var messages = Run(@"DIM Outer, Inner, Counter
Outer = 0
Counter = 0
DO
    Outer = Outer + 1
    Inner = 0
    DO
        Inner = Inner + 1
        IF Inner >= 2 THEN EXIT DO ENDIF
    LOOP
    Counter = Counter + Inner
    IF Outer >= 3 THEN EXIT DO ENDIF
LOOP
" + OutputOne);

        messages.ShouldBe(new[] { "6" });
    }

    [Test]
    public void ExitDo_InAFunction_LeavesTheStackBalancedForTheReturnValue()
    {
        var messages = Run(@"FUNCTION CountUpTo(Limit)
    DIM Position
    Position = 0
    DO
        Position = Position + 1
        IF Position >= Limit THEN EXIT DO ENDIF
    LOOP
    CountUpTo = Position
ENDFUNCTION

DIM Counter
Counter = CountUpTo(4) + CountUpTo(2)
" + OutputOne);

        messages.ShouldBe(new[] { "6" });
    }

    [Test]
    public void ExitFunction_InsideADoLoop_ReturnsTheValueAndLeavesTheCallerIntact()
    {
        var messages = Run(@"FUNCTION FirstAbove(Limit)
    DIM Position
    Position = 0
    DO
        Position = Position + 1
        IF Position > Limit THEN
            FirstAbove = Position
            EXIT FUNCTION
        ENDIF
    LOOP
    FirstAbove = -1
ENDFUNCTION

DIM Counter
Counter = FirstAbove(3) + FirstAbove(1)
" + OutputOne);

        messages.ShouldBe(new[] { "6" });
    }

    [Test]
    public void ExitFor_InsideAForLoop_StillLeavesTheLoop()
    {
        var messages = Run(@"DIM Counter, Total
Total = 0
FOR Counter = 1 TO 10
    Total = Total + Counter
    IF Counter >= 3 THEN EXIT FOR ENDIF
NEXT
OUTPUT 1, Total");

        messages.ShouldBe(new[] { "6" });
    }

    [Test]
    public void ExitDo_AfterAForLoop_KeepsTheVariablesReadable()
    {
        var messages = Run(@"DIM Counter, Total
Total = 0
FOR Counter = 1 TO 3
    Total = Total + Counter
NEXT
Counter = 0
DO
    Counter = Counter + 1
    IF Counter >= 2 THEN EXIT DO ENDIF
LOOP
OUTPUT 1, Total + Counter");

        messages.ShouldBe(new[] { "8" });
    }

    [Test]
    public void ExitDo_OutsideALoop_IsRefusedAtCompileTime()
    {
        var compiled = CompiledScript.Compile("DIM Counter\nCounter = 1\nEXIT DO\nOUTPUT 1, Counter");

        compiled.HasError.ShouldBeTrue();
        compiled.Error!.Description.ShouldContain("EXIT DO");
    }

    [Test]
    public void ExitDo_InsideAForLoopWithoutADoLoop_IsRefusedAtCompileTime()
    {
        var compiled = CompiledScript.Compile("DIM Counter\nFOR Counter = 1 TO 3\nEXIT DO\nNEXT\nOUTPUT 1, Counter");

        compiled.HasError.ShouldBeTrue();
    }

    private static string[] Run(string script)
    {
        var compiled = CompiledScript.Compile(script);
        compiled.HasError.ShouldBeFalse(compiled.Error?.Description);

        var result = new ScriptExecutionContext(compiled).Execute();

        result.Success.ShouldBeTrue(result.Error?.Description);
        return result.Messages.Select(message => message.Message).ToArray();
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Correctness tests for OUTPUT and the value stack of the macro interpreter. OUTPUT consumes its two operands and
/// leaves one empty value, which the statement form drops again and the expression form (x = OUTPUT(...)) assigns, so
/// an OUTPUT never touches the variables, the FUNCTIONs, the IMPORT symbols or the bookkeeping of an enclosing FOR, DO,
/// SELECT CASE or FUNCTION call. Until 2026-09-26 the statement form removed one entry too many (the oldest declared
/// variable of the current scope, a loop's exit address or a function's return address); these tests replace the
/// characterization tests that pinned that defect.
/// </summary>

using Klacks.Api.Infrastructure.Scripting;

namespace Klacks.UnitTest.Infrastructure.Scripting;

[TestFixture]
public class OutputStatementStackBalanceTests
{
    private const int ThrowingChannel = 10;

    [Test]
    public void ReadingADeclaredVariableAfterAnOutput_Works()
    {
        var result = Run("DIM A\nA = 1\nOUTPUT 1, A\nOUTPUT 10, A");

        AssertMessages(result, "1:1", "10:1");
    }

    [Test]
    public void ReadingAnImportAfterOutputs_Works()
    {
        var result = Run("IMPORT Hour\nOUTPUT 1, Hour\nOUTPUT 10, Hour\nOUTPUT 11, Hour", ("hour", 8m));

        AssertMessages(result, "1:8", "10:8", "11:8");
    }

    [Test]
    public void ImportsAndVariablesStayReadable_AfterOutputsOfBoth()
    {
        var result = Run("IMPORT Hour\nDIM A\nA = 1\nOUTPUT 10, A\nOUTPUT 11, Hour\nOUTPUT 1, A + Hour", ("hour", 8m));

        AssertMessages(result, "10:1", "11:8", "1:9");
    }

    [Test]
    public void OutputsInDeclarationOrder_Work()
    {
        var result = Run("DIM A, B\nA = 1\nB = 2\nOUTPUT 10, A\nOUTPUT 11, B");

        AssertMessages(result, "10:1", "11:2");
    }

    [Test]
    public void OutputsAgainstDeclarationOrder_Work()
    {
        var result = Run("DIM A, B\nA = 1\nB = 2\nOUTPUT 11, B\nOUTPUT 10, A");

        AssertMessages(result, "11:2", "10:1");
    }

    [Test]
    public void TheSameVariableCanBeOutputManyTimes_OnEveryChannel()
    {
        var result = Run("DIM A, B, C\nA = 1\nB = 2\nC = 3\nOUTPUT 12, C\nOUTPUT 11, B\nOUTPUT 10, A\nOUTPUT 13, C\nOUTPUT 1, A + B + C");

        AssertMessages(result, "12:3", "11:2", "10:1", "13:3", "1:6");
    }

    [Test]
    public void OutputInsideIf_LeavesTheVariablesReadable()
    {
        var result = Run("DIM A\nA = 1\nIF A = 1 THEN\nOUTPUT 10, 5\nENDIF\nOUTPUT 1, A");

        AssertMessages(result, "10:5", "1:1");
    }

    [Test]
    public void OutputInBothBranchesOfAnIfElse_LeavesTheVariablesReadable()
    {
        var result = Run("DIM A, B\nA = 1\nB = 2\nIF A = 2 THEN\nOUTPUT 10, A\nELSE\nOUTPUT 11, B\nENDIF\nOUTPUT 1, A + B");

        AssertMessages(result, "11:2", "1:3");
    }

    [Test]
    public void OutputInsideForLoop_RunsEveryIteration()
    {
        var result = Run("DIM I\nFOR I = 1 TO 3\nOUTPUT 10, I\nNEXT\nOUTPUT 1, 99");

        AssertMessages(result, "10:1", "10:2", "10:3", "1:99");
    }

    [Test]
    public void OutputInsideForLoopWithFourVariablesDeclaredBefore_RunsEveryIteration()
    {
        var result = Run("DIM A, B, C, D\nA = 1\nB = 2\nC = 3\nD = 4\nDIM I\nFOR I = 1 TO 3\nOUTPUT 10, I\nNEXT\nOUTPUT 1, A + B + C + D");

        AssertMessages(result, "10:1", "10:2", "10:3", "1:10");
    }

    [Test]
    public void OutputInsideForLoopWithSevenVariablesDeclaredBefore_RunsEveryIteration()
    {
        var result = Run(
            "DIM A, B, C, D, E, F, G\nA = 1\nB = 2\nC = 3\nD = 4\nE = 5\nF = 6\nG = 7\nDIM I\nFOR I = 1 TO 5\nOUTPUT 10, I\nNEXT\nOUTPUT 1, 99");

        AssertMessages(result, "10:1", "10:2", "10:3", "10:4", "10:5", "1:99");
    }

    [Test]
    public void ExitForAfterAnOutput_LeavesTheLoopAtTheRightPlace()
    {
        var result = Run(
            "DIM I, Last\nLast = 0\nFOR I = 1 TO 10\nOUTPUT 10, I\nLast = I\nIF I = 3 THEN\nEXIT FOR\nENDIF\nNEXT\nOUTPUT 1, Last");

        AssertMessages(result, "10:1", "10:2", "10:3", "1:3");
    }

    [Test]
    public void NestedForLoopsWithOutputs_RunEveryIteration()
    {
        var result = Run("DIM I, J, S\nS = 0\nFOR I = 1 TO 2\nFOR J = 1 TO 2\nOUTPUT 10, I * 10 + J\nS = S + 1\nNEXT\nOUTPUT 11, I\nNEXT\nOUTPUT 1, S");

        AssertMessages(result, "10:11", "10:12", "11:1", "10:21", "10:22", "11:2", "1:4");
    }

    [Test]
    public void OutputInsideDoWhileLoop_RunsEveryIteration()
    {
        var result = Run("DIM I\nI = 0\nDO WHILE I < 3\nI = I + 1\nOUTPUT 10, I\nLOOP\nOUTPUT 1, 99");

        AssertMessages(result, "10:1", "10:2", "10:3", "1:99");
    }

    [Test]
    public void OutputInsideDoLoopUntil_KeepsTheLoopVariables()
    {
        var result = Run("DIM I, S\nS = 0\nI = 0\nDO\nI = I + 1\nS = S + I\nOUTPUT 10, S\nLOOP UNTIL I >= 3\nOUTPUT 1, S");

        AssertMessages(result, "10:1", "10:3", "10:6", "1:6");
    }

    [Test]
    public void ForLoopInsideADoLoop_WithOutputsInBoth_RunsEveryIteration()
    {
        var result = Run("DIM I, J\nI = 0\nDO WHILE I < 2\nI = I + 1\nOUTPUT 11, I\nFOR J = 1 TO 2\nOUTPUT 10, J\nNEXT\nLOOP\nOUTPUT 1, I");

        AssertMessages(result, "11:1", "10:1", "10:2", "11:2", "10:1", "10:2", "1:2");
    }

    [Test]
    public void OutputInsideSelectCase_KeepsTheSelectorAndTheVariables()
    {
        var result = Run(
            "DIM A, X\nA = 5\nX = 2\nSELECT CASE X\nCASE 1\nOUTPUT 10, 1\nCASE 2\nOUTPUT 10, 2\nCASE ELSE\nOUTPUT 10, 3\nEND SELECT\nOUTPUT 1, A");

        AssertMessages(result, "10:2", "1:5");
    }

    [Test]
    public void AFunctionDeclaredBeforeAnOutput_CanStillBeCalledAfterIt()
    {
        var result = Run(
            "FUNCTION Twice(V)\nTwice = V * 2\nENDFUNCTION\nDIM A\nA = 4\nOUTPUT 10, A\nOUTPUT 11, Twice(A)\nOUTPUT 1, Twice(A) + A");

        AssertMessages(result, "10:4", "11:8", "1:12");
    }

    [Test]
    public void OutputInsideAFunctionWithoutParameters_KeepsItsReturnAddress()
    {
        var result = Run("FUNCTION F()\nOUTPUT 10, 1\nF = 2\nENDFUNCTION\nDIM R\nR = F()\nOUTPUT 1, R");

        AssertMessages(result, "10:1", "1:2");
    }

    [Test]
    public void OutputInsideAFunction_KeepsItsParameters()
    {
        var result = Run("FUNCTION G(P)\nOUTPUT 10, P\nG = P * 2\nENDFUNCTION\nDIM R\nR = G(3)\nOUTPUT 1, R");

        AssertMessages(result, "10:3", "1:6");
    }

    [Test]
    public void NestedFunctionCallsWithOutputs_ReturnTheirValues()
    {
        var result = Run(
            "FUNCTION Inner(X)\nOUTPUT 11, X\nInner = X + 1\nENDFUNCTION\n"
            + "FUNCTION Outer(Y)\nOUTPUT 10, Y\nOuter = Inner(Y) * 2\nENDFUNCTION\n"
            + "DIM R\nR = Outer(2)\nOUTPUT 1, R\nOUTPUT 12, Inner(5)\nOUTPUT 13, R");

        AssertMessages(result, "10:2", "11:2", "1:6", "11:5", "12:6", "13:6");
    }

    [Test]
    public void OutputUsedAsAnExpression_YieldsAnEmptyValue_AndKeepsTheVariables()
    {
        var result = Run("DIM A, X\nA = 7\nX = OUTPUT(1, 5)\nOUTPUT 10, X\nOUTPUT 11, A");

        AssertMessages(result, "1:5", "11:7");
    }

    [Test]
    public void OutputUsedAsAnExpression_WithoutOtherVariables_EmitsItsMessageOnly()
    {
        var result = Run("DIM X\nX = OUTPUT(1, 5)\nOUTPUT 10, X");

        AssertMessages(result, "1:5");
    }

    [Test]
    public void OutputStatementWithParentheses_IsACompileError()
    {
        var compiled = CompiledScript.Compile("DIM A\nA = 3\nOUTPUT(1, A)\nOUTPUT 10, A");

        compiled.HasError.ShouldBeTrue();
        compiled.Error!.Description.ShouldContain("Missing closing bracket");
    }

    [Test]
    public void AMessageHandlerThatFails_DoesNotUnbalanceTheStack()
    {
        var compiled = CompiledScript.Compile("DIM A\nA = 1\nOUTPUT 10, A\nOUTPUT 1, A");
        compiled.HasError.ShouldBeFalse(compiled.Error?.Description);
        var context = new ScriptExecutionContext(compiled.CloneForExecution());
        var handled = new List<string>();
        context.Message += (type, message) =>
        {
            handled.Add(type + ":" + message);
            if (type == ThrowingChannel)
            {
                throw new InvalidOperationException();
            }
        };

        var result = context.Execute();

        AssertMessages(result, "1:1");
        handled.ShouldBe(["10:1", "-1:", "1:1"]);
    }

    private static void AssertMessages(ScriptResult result, params string[] expected)
    {
        result.Success.ShouldBeTrue(result.Error?.Description);
        result.Messages.Select(m => m.Type + ":" + m.Message).ShouldBe(expected);
    }

    private static ScriptResult Run(string content, params (string Name, object Value)[] imports)
    {
        var compiled = CompiledScript.Compile(content);
        compiled.HasError.ShouldBeFalse(compiled.Error?.Description);
        var script = compiled.CloneForExecution();
        foreach (var (name, value) in imports)
        {
            script.SetExternalValue(name, value);
        }

        return new ScriptExecutionContext(script).Execute();
    }
}

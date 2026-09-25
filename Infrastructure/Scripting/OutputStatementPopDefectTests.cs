// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Characterization tests for a known defect of the macro interpreter that the owner decided to keep: every
/// OUTPUT statement compiles to Message followed by an extra Pop, although Message already consumes both of its
/// operands. The extra Pop removes the last entry of the current scope, which is the oldest variable the script
/// declared or, inside a loop, the loop's bookkeeping on the value stack. IMPORT symbols live in the external
/// scope and are never removed. These tests pin the defect so that fixing the interpreter turns them red and
/// the assistant guidance for extended macro copies can be relaxed deliberately.
/// </summary>

using Klacks.Api.Infrastructure.Scripting;

namespace Klacks.UnitTest.Infrastructure.Scripting;

[TestFixture]
public class OutputStatementPopDefectTests
{
    private const string UnassignedVariableText = "has not been assigned a value";

    [Test]
    public void ReadingADeclaredVariableAfterAnOutput_FailsAsUnassigned()
    {
        var result = Run("DIM A\nA = 1\nOUTPUT 1, A\nOUTPUT 10, A");

        result.Success.ShouldBeFalse();
        result.Error!.Description.ShouldContain(UnassignedVariableText);
    }

    [Test]
    public void ReadingAnImportAfterOutputs_Works()
    {
        var result = Run("IMPORT Hour\nOUTPUT 1, Hour\nOUTPUT 10, Hour\nOUTPUT 11, Hour", ("hour", 8m));

        result.Success.ShouldBeTrue(result.Error?.Description);
        result.Messages.Select(m => m.Message).ShouldBe(["8", "8", "8"]);
    }

    [Test]
    public void OutputsInDeclarationOrder_Work()
    {
        var result = Run("DIM A, B\nA = 1\nB = 2\nOUTPUT 10, A\nOUTPUT 11, B");

        result.Success.ShouldBeTrue(result.Error?.Description);
        result.Messages.Select(m => m.Message).ShouldBe(["1", "2"]);
    }

    [Test]
    public void OutputsAgainstDeclarationOrder_FailAsUnassigned()
    {
        var result = Run("DIM A, B\nA = 1\nB = 2\nOUTPUT 11, B\nOUTPUT 10, A");

        result.Success.ShouldBeFalse();
        result.Error!.Description.ShouldContain(UnassignedVariableText);
    }

    [Test]
    public void OutputInsideIf_RemovesAVariableLikeAnyOtherOutput()
    {
        var result = Run("DIM A\nA = 1\nIF A = 1 THEN\nOUTPUT 10, 5\nENDIF\nOUTPUT 1, A");

        result.Success.ShouldBeFalse();
        result.Error!.Description.ShouldContain(UnassignedVariableText);
    }

    [Test]
    public void OutputInsideForLoop_DestroysTheLoopStateAndCrashesTheInterpreter()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Run("DIM I\nFOR I = 1 TO 3\nOUTPUT 10, I\nNEXT\nOUTPUT 1, 99"));
    }

    [Test]
    public void OutputInsideDoLoop_LosesTheLoopVariableAfterTwoIterations()
    {
        var result = Run("DIM I\nI = 0\nDO WHILE I < 3\nI = I + 1\nOUTPUT 10, I\nLOOP\nOUTPUT 1, 99");

        result.Success.ShouldBeFalse();
        result.Error!.Description.ShouldContain(UnassignedVariableText);
        result.Messages.Select(m => m.Message).ShouldBe(["1", "2"]);
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

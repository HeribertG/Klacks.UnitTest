// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroScriptRunner: a valid script compiles, a compile error returns the compiler's message, the compiler
/// crash on a comment in the last line becomes the fixed trailing-comment message, a run binds the inputs and returns the
/// OUTPUT messages, a runtime failure returns its error, and a cancelled token ends a run that completes without it.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroScriptRunnerTests
{
    private const string DoubleHour = "IMPORT Hour\nOUTPUT 1, Hour * 2";
    private const string DuplicateImport = "IMPORT Hour\nIMPORT Hour\nOUTPUT 1, Hour";
    private const string TrailingComment = "OUTPUT 1, 1 ' note";
    private const string FailsAtRuntime = "MSGBOX \"always\"\nOUTPUT 1, 1";
    private const string Loop = "DIM I\nFOR I = 1 TO 100000\nNEXT\nOUTPUT 1, 1";

    [Test]
    public void TryCompile_ValidScript_ReturnsTheScript()
    {
        var (script, error) = MacroScriptRunner.TryCompile(DoubleHour);

        script.ShouldNotBeNull();
        error.ShouldBeNull();
    }

    [Test]
    public void TryCompile_CompileError_ReturnsTheCompilerMessage()
    {
        var (script, error) = MacroScriptRunner.TryCompile(DuplicateImport);

        script.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void TryCompile_CommentOnTheLastLine_ReportsTheFixedMessage()
    {
        var (script, error) = MacroScriptRunner.TryCompile(TrailingComment);

        script.ShouldBeNull();
        error.ShouldBe(MacroScriptRunner.TrailingCommentCompileError);
    }

    [Test]
    public void Run_BindsTheInputs_AndReturnsTheOutput()
    {
        var (script, _) = MacroScriptRunner.TryCompile(DoubleHour);

        var run = MacroScriptRunner.Run(script!, new MacroData { Hour = 4m }, CancellationToken.None);

        run.IsCompleted.ShouldBeTrue(run.Error);
        var message = run.Messages!.Single();
        message.Type.ShouldBe(1);
        decimal.Parse(message.Message, CultureInfo.InvariantCulture).ShouldBe(8m);
    }

    [Test]
    public void Run_RuntimeFailure_ReturnsTheError()
    {
        var (script, _) = MacroScriptRunner.TryCompile(FailsAtRuntime);

        var run = MacroScriptRunner.Run(script!, new MacroData(), CancellationToken.None);

        run.IsCompleted.ShouldBeFalse();
        run.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Run_CancelledToken_EndsWithoutSuccess()
    {
        var (script, _) = MacroScriptRunner.TryCompile(Loop);

        var uncancelled = MacroScriptRunner.Run(script!, new MacroData(), CancellationToken.None);
        var cancelled = MacroScriptRunner.Run(script!, new MacroData(), new CancellationToken(true));

        uncancelled.IsCompleted.ShouldBeTrue(uncancelled.Error);
        cancelled.IsCompleted.ShouldBeFalse();
    }
}

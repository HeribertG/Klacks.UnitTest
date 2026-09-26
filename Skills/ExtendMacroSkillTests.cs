// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for extend_macro: the copy is the original script plus the appended block, stored as a plain
/// custom macro with origin AssistantExtension while the original is never touched; name clashes (including
/// template names), unsupported channels, invalid scripts and regression deviations are refused before anything is
/// stored, and the regression check runs under the cancellation token of the skill call. An AllShift copy with a
/// surcharge on a free channel and a result recomputed from the IMPORT symbols (through a copied function) is
/// stored under the result-channel rule of the check, and so is a block that reads the total variable and the FUNCTION
/// of the real AllShift seed. Tests with the real validator, channel inspector and regression checker run end to end:
/// a block may read the variables of the original, in any OUTPUT order. A refusal by the validator or a runtime failure
/// of the copy carries the guidance on what the appended block sees and may declare; a regression abort that is not a
/// runtime failure of the copy (original does not compile, no comparable input, time budget, result total of the copy
/// beyond the decimal range) carries no such guidance,
/// and a block ending in a comment line is refused by the channel scan on the trimmed block.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Macros;
using Klacks.UnitTest.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ExtendMacroSkillTests
{
    private const string OriginalScript =
        "IMPORT Hour, Weekday, NightRate\n"
        + "DIM Bonus\n"
        + "Bonus = 0\n"
        + "IF Weekday = 7 THEN Bonus = Hour * NightRate ENDIF\n"
        + "OUTPUT 10, Bonus\n"
        + "OUTPUT 1, Hour";

    private const string AdditiveBlock = "OUTPUT 13, Hour * 0.5";
    private const string UnassignedVariableText = "has not been assigned a value";
    private const string AppendedBlockHintText = "The appended block runs after the original script";
    private const string TrailingCommentText = "comment on the last line of the script";

    private readonly Guid _sourceId = Guid.NewGuid();
    private IMediator _mediator = null!;
    private IMacroScriptValidator _validator = null!;
    private IMacroRegressionChecker _checker = null!;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new()
                {
                    Id = _sourceId,
                    Name = "AllShift",
                    Content = OriginalScript,
                    Type = (int)MacroFunctionEnum.Standard,
                    Category = MacroCategoryEnum.Shift,
                    Origin = MacroOrigin.Seed,
                    Description = new MultiLanguage()
                }
            }.AsEnumerable());
        _mediator.Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => new MacroResource { Id = Guid.NewGuid(), Name = ((PostCommand)ci[0]).model.Name });

        _validator = Substitute.For<IMacroScriptValidator>();
        _validator.Validate(Arg.Any<string>()).Returns(MacroScriptValidationResult.Success());

        _checker = Substitute.For<IMacroRegressionChecker>();
        _checker.Check(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MacroRegressionResult(672, 0, Array.Empty<MacroRegressionDeviation>(), 0, null));
    }

    private ExtendMacroSkill CreateSkill() =>
        new(_mediator, new MacroOutputChannelInspector(), _validator, _checker);

    private ExtendMacroSkill CreateSkillWithRealComponents() =>
        new(_mediator, new MacroOutputChannelInspector(), new MacroScriptValidator(), new MacroRegressionChecker());

    private static Dictionary<string, object> Parameters(string newName = "AllShift plus early bonus", string block = AdditiveBlock) => new()
    {
        ["macroName"] = "AllShift",
        ["name"] = newName,
        ["additionalScript"] = block
    };

    [Test]
    public async Task Extend_StoresCustomAssistantExtensionCopy_WithAppendedBlock_OriginalUntouched()
    {
        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand>(c =>
                c.Origin == MacroOrigin.AssistantExtension &&
                c.model.Name == "AllShift plus early bonus" &&
                c.model.Content == OriginalScript + "\n" + AdditiveBlock &&
                c.model.Type == (int)MacroFunctionEnum.Custom &&
                c.model.Category == MacroCategoryEnum.Unspecified),
            Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>());
        _checker.Received(1).Check(OriginalScript, OriginalScript + "\n" + AdditiveBlock, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_NameAlreadyTaken_IsRefused_NoPost()
    {
        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters(newName: " allshift "));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("already exists");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_NameOfATemplateThatIsNotListed_IsRefused_NoPost()
    {
        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters(newName: "Vacation"));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("template macro shipped with Klacks");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_UnsupportedChannelInBlock_IsRefused_BeforeValidation()
    {
        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters(block: "OUTPUT 42, 1"));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("42");
        _validator.DidNotReceive().Validate(Arg.Any<string>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_BlockEndingWithACommentLine_IsRefusedByTheChannelScan_BeforeValidation()
    {
        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters(block: AdditiveBlock + " ' early bonus\n"));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(TrailingCommentText);
        _validator.DidNotReceive().Validate(Arg.Any<string>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_InvalidCombinedScript_IsRefused_BeforeRegressionCheck()
    {
        _validator.Validate(Arg.Any<string>()).Returns(MacroScriptValidationResult.Failure("compile error: already declared"));

        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("compile error: already declared");
        result.Message.ShouldContain(AppendedBlockHintText);
        _checker.DidNotReceive().Check(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_RegressionDeviation_IsRefused_WithDiff()
    {
        _checker.Check(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new MacroRegressionResult(
            672,
            0,
            new[] { new MacroRegressionDeviation("weekday 7, 07:00-15:00", 10, 0.8m, 1.8m) },
            12,
            null));

        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("weekday 7, 07:00-15:00");
        result.Message.ShouldContain("channel 10");
        result.Message.ShouldContain("original 0.8");
        result.Message.ShouldContain("copy 1.8");
        result.Message.ShouldContain("12");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_CopyFailsAtRuntime_IsRefused_WithReasonAndGuidance()
    {
        _checker.Check(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MacroRegressionResult.Failure(
                MacroRegressionFailureKind.CopyRuntimeError, "The extended script fails at test input [weekday 3]: boom"));

        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("fails at test input [weekday 3]: boom");
        result.Message.ShouldContain(AppendedBlockHintText);
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [TestCase(MacroRegressionFailureKind.OriginalCompileError, "The original macro script does not compile: boom")]
    [TestCase(MacroRegressionFailureKind.CopyCompileError, "The extended script does not compile: boom")]
    [TestCase(MacroRegressionFailureKind.NoComparableSample, "The original macro could not be executed on any test input.")]
    [TestCase(MacroRegressionFailureKind.BudgetExceeded, "The regression check did not finish within 30000 ms.")]
    [TestCase(MacroRegressionFailureKind.CopyTotalOutOfRange, "The extended script adds surcharges at test input [weekday 3] whose total lies beyond the decimal range.")]
    public async Task Extend_RegressionCheckAbortedNotByACopyRuntimeFailure_IsRefused_WithReasonButWithoutGuidance(
        MacroRegressionFailureKind kind, string reason)
    {
        _checker.Check(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(MacroRegressionResult.Failure(kind, reason));

        var result = await CreateSkill().ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(reason);
        result.Message.ShouldNotContain(AppendedBlockHintText);
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_MissingBlock_IsRefused()
    {
        var parameters = Parameters();
        parameters.Remove("additionalScript");

        var result = await CreateSkill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_UnknownMacroId_IsRefused()
    {
        var result = await CreateSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = Guid.NewGuid().ToString(),
            ["name"] = "Copy",
            ["additionalScript"] = AdditiveBlock
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("No macro found");
    }

    [TestCase(AdditiveBlock, true)]
    [TestCase("DIM EarlyBonus\nEarlyBonus = 0\nIF Weekday = 3 THEN EarlyBonus = Hour * 0.1 ENDIF\nOUTPUT 13, EarlyBonus", true)]
    [TestCase("IMPORT Holiday\nOUTPUT 14, Holiday * Hour", true)]
    [TestCase("DIM A, B\nA = 0\nB = 0\nIF Weekday = 3 THEN\nA = Hour * 0.1\nB = Hour * 0.2\nENDIF\nOUTPUT 13, A\nOUTPUT 14, B", true)]
    [TestCase("DIM A, B\nA = 0\nB = 0\nIF Weekday = 3 THEN\nA = Hour * 0.1\nB = Hour * 0.2\nENDIF\nOUTPUT 14, B\nOUTPUT 13, A", true)]
    [TestCase("OUTPUT 10, 1", false)]
    [TestCase("DIM Extra, NewTotal\nExtra = Hour * 0.5\nNewTotal = Hour + Extra\nOUTPUT 13, Extra\nOUTPUT 1, NewTotal", true)]
    [TestCase("DIM Extra, NewTotal\nExtra = Hour * 0.5\nNewTotal = Hour + Extra + 0.01\nOUTPUT 13, Extra\nOUTPUT 1, NewTotal", false)]
    [TestCase("OUTPUT 1, Hour + 1", false)]
    public async Task Extend_WithRealComponents_AcceptsAdditiveAndRejectsChangingBlocks(string block, bool expectedSuccess)
    {
        var result = await CreateSkillWithRealComponents().ExecuteAsync(Ctx(), Parameters(block: block));

        result.Success.ShouldBe(expectedSuccess, result.Message);
    }

    [Test]
    public async Task Extend_AllShiftWithAFreeChannelSurchargeAndARecomputedResult_WithRealComponents_IsStored()
    {
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new()
                {
                    Id = _sourceId,
                    Name = "AllShift",
                    Content = SeededMacroScripts.AllShiftScript(),
                    Type = (int)MacroFunctionEnum.Standard,
                    Category = MacroCategoryEnum.Shift,
                    Origin = MacroOrigin.Seed,
                    Description = new MultiLanguage()
                }
            }.AsEnumerable());
        var block = SeededMacroScripts.AllShiftWednesdayBonusBlock(SeededMacroScripts.CopiedFunctionName);

        var result = await CreateSkillWithRealComponents().ExecuteAsync(Ctx(), Parameters(block: block));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand>(c => c.model.Content.EndsWith(block)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_PassesTheTurnCancellationToken_ToTheRegressionCheck()
    {
        using var turn = new CancellationTokenSource();

        await CreateSkill().ExecuteAsync(Ctx(), Parameters(), turn.Token);

        _checker.Received(1).Check(Arg.Any<string>(), Arg.Any<string>(), turn.Token);
    }

    [Test]
    public async Task Extend_TurnStoppedDuringTheRegressionCheck_PropagatesTheCancellation_NoPost()
    {
        _checker.Check(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<MacroRegressionResult>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => CreateSkill().ExecuteAsync(Ctx(), Parameters()));

        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_BlockReadingAVariableOfTheOriginal_WithRealComponents_IsStored()
    {
        var result = await CreateSkillWithRealComponents().ExecuteAsync(Ctx(), Parameters(block: "OUTPUT 13, Bonus"));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand>(c => c.model.Content == OriginalScript + "\nOUTPUT 13, Bonus"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_ConditionalReadOfAVariableOfTheOriginal_WithRealComponents_IsStored()
    {
        var result = await CreateSkillWithRealComponents().ExecuteAsync(
            Ctx(), Parameters(block: "IF Weekday = 3 THEN\nOUTPUT 13, Bonus\nENDIF"));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_BlockChangingASurchargeOfTheOriginalThroughItsVariable_WithRealComponents_IsRefused()
    {
        var result = await CreateSkillWithRealComponents().ExecuteAsync(Ctx(), Parameters(block: "OUTPUT 10, Bonus"));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("channel 10");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_AllShiftBlockReadingItsTotalAndItsFunction_WithRealComponents_IsStored()
    {
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new()
                {
                    Id = _sourceId,
                    Name = "AllShift",
                    Content = SeededMacroScripts.AllShiftScript(),
                    Type = (int)MacroFunctionEnum.Standard,
                    Category = MacroCategoryEnum.Shift,
                    Origin = MacroOrigin.Seed,
                    Description = new MultiLanguage()
                }
            }.AsEnumerable());
        var block = SeededMacroScripts.WednesdayBonusReadingTheOriginal;

        var result = await CreateSkillWithRealComponents().ExecuteAsync(Ctx(), Parameters(block: block));

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldContain("compared 672 test inputs (0 skipped");
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand>(c => c.model.Content.EndsWith(block)), Arg.Any<CancellationToken>());
    }
}

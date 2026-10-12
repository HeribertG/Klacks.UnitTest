// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ToolsetMembershipGuard: a skill the model calls must be part of the turn's FULL toolset. Aliases
/// resolve to the skill they stand for, confirm_pending_action always belongs, the check ignores case, a turn
/// without a toolset is not judged, and a call without a name is outside. Observe flags and logs but lets the call
/// run with its real outcome; Enforce rejects it with the instructive result and flags it separately, so failure
/// consumers can tell it from a real failure. The log line carries turn, conversation, model, mode and toolset
/// size. Both call sites hand the guard the turn's full toolset, never a narrowed iteration's; a test of
/// "narrowed iteration, member call" needs a recipe-forced or confirm-forced whole turn and is not part of this class
/// (the former test of that name never passed the narrowed list to Apply and proved nothing).
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ToolsetMembershipGuardTests
{
    private const string OfferedSkill = "update_contract";
    private const string OutsideSkill = "delete_client";

    private static readonly List<LLMFunction> Toolset =
    [
        new() { Name = OfferedSkill },
        new() { Name = "create_employee" },
        new() { Name = "get_user_context" },
        new() { Name = SkillNames.NavigateTo }
    ];

    private static readonly Guid TurnId = Guid.NewGuid();
    private const string ConversationId = "conv-guard-test";
    private const string ModelId = "model-guard-test";

    private static LLMFunctionCall Call(string name) => new() { FunctionName = name };

    private static LLMContext Context(List<LLMFunction>? toolset) => new()
    {
        AvailableFunctions = toolset!,
        TurnId = TurnId,
        ConversationId = ConversationId,
        ModelId = ModelId
    };

    private static List<LLMFunctionCall> Apply(
        ToolsetMembershipMode mode, RecordingLogger<ToolsetMembershipGuardTests> logger, params LLMFunctionCall[] calls) =>
        ToolsetMembershipGuard.Apply(calls.ToList(), Context(Toolset), mode, logger);

    [TestCase(ToolsetMembershipMode.Observe)]
    [TestCase(ToolsetMembershipMode.Enforce)]
    public void CallToAnOfferedSkill_IsNeitherFlaggedNorRejected(ToolsetMembershipMode mode)
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call(OfferedSkill);

        var executable = Apply(mode, logger, call);

        executable.ShouldBe([call]);
        call.IsOutsideToolset.ShouldBeFalse();
        call.IsRejectedOutsideToolset.ShouldBeFalse();
        call.Success.ShouldBeTrue();
        logger.Entries.ShouldBeEmpty();
    }

    [TestCase("create_client")]
    [TestCase("CREATE_CLIENT")]
    [TestCase("get_current_user")]
    [TestCase("navigate_to_page")]
    public void AliasOfAnOfferedSkill_IsAnOfferedSkill_EvenInEnforceMode(string alias)
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call(alias);

        var executable = Apply(ToolsetMembershipMode.Enforce, logger, call);

        executable.ShouldBe([call]);
        call.IsOutsideToolset.ShouldBeFalse();
        call.FunctionName.ShouldBe(alias);
    }

    [Test]
    public void AliasWhoseTargetIsNotOffered_IsOutside()
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call("search_clients");

        var executable = Apply(ToolsetMembershipMode.Enforce, logger, call);

        executable.ShouldBeEmpty();
        call.IsRejectedOutsideToolset.ShouldBeTrue();
    }

    [TestCase(ToolsetMembershipMode.Observe)]
    [TestCase(ToolsetMembershipMode.Enforce)]
    public void ConfirmPendingAction_AlwaysBelongs_EvenWhenTheToolsetLacksIt(ToolsetMembershipMode mode)
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call(AutonomyDefaults.ConfirmPendingActionSkillName);

        var executable = Apply(mode, logger, call);

        executable.ShouldBe([call]);
        call.IsOutsideToolset.ShouldBeFalse();
        call.IsRejectedOutsideToolset.ShouldBeFalse();
    }

    [TestCase("Update_Contract")]
    [TestCase("UPDATE_CONTRACT")]
    public void Membership_IgnoresCase(string name)
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call(name);

        var executable = Apply(ToolsetMembershipMode.Enforce, logger, call);

        executable.ShouldBe([call]);
        call.IsOutsideToolset.ShouldBeFalse();
    }

    [Test]
    public void Observe_FlagsAndLogsTheOutsideCall_ButLetsItRunWithItsRealOutcome()
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call(OutsideSkill);

        var executable = Apply(ToolsetMembershipMode.Observe, logger, call);

        executable.ShouldBe([call]);
        call.IsOutsideToolset.ShouldBeTrue();
        call.IsRejectedOutsideToolset.ShouldBeFalse();
        call.Success.ShouldBeTrue();
        call.Result.ShouldBeNull();
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(OutsideSkill));
    }

    [Test]
    public void Enforce_RejectsTheOutsideCallWithTheInstructiveResult_AndKeepsTheOthers()
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var outside = Call(OutsideSkill);
        var inside = Call(OfferedSkill);

        var executable = Apply(ToolsetMembershipMode.Enforce, logger, outside, inside);

        executable.ShouldBe([inside]);
        outside.IsOutsideToolset.ShouldBeTrue();
        outside.IsRejectedOutsideToolset.ShouldBeTrue();
        outside.IsRejectedRepeat.ShouldBeFalse();
        outside.Success.ShouldBeFalse();
        outside.Result.ShouldBe(LLMLoopConstants.ToolsetMembershipRejectedResult);
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(OutsideSkill));
    }

    [TestCase(ToolsetMembershipMode.Observe)]
    [TestCase(ToolsetMembershipMode.Enforce)]
    public void TurnWithoutAToolset_IsNotJudged(ToolsetMembershipMode mode)
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call(OutsideSkill);

        var withoutToolset = ToolsetMembershipGuard.Apply([call], Context(null), mode, logger);
        var withEmptyToolset = ToolsetMembershipGuard.Apply([call], Context([]), mode, logger);

        withoutToolset.ShouldBe([call]);
        withEmptyToolset.ShouldBe([call]);
        call.IsOutsideToolset.ShouldBeFalse();
        logger.Entries.ShouldBeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void CallWithoutAName_IsOutside_AndDoesNotThrow(string? name)
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();
        var call = Call(name!);

        var executable = Apply(ToolsetMembershipMode.Enforce, logger, call);

        executable.ShouldBeEmpty();
        call.IsOutsideToolset.ShouldBeTrue();
        call.IsRejectedOutsideToolset.ShouldBeTrue();
    }

    [Test]
    public void TheOutsideCallLog_CarriesTheFieldsThatMakeItEvaluable()
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();

        Apply(ToolsetMembershipMode.Observe, logger, Call(OutsideSkill));

        var message = logger.Entries.Single(entry => entry.Level == LogLevel.Warning).Message;
        message.ShouldContain(OutsideSkill);
        message.ShouldContain("toolset");
        message.ShouldContain(TurnId.ToString());
        message.ShouldContain(ConversationId);
        message.ShouldContain(ModelId);
        message.ShouldContain(nameof(ToolsetMembershipMode.Observe));
        message.ShouldContain(Toolset.Count.ToString());
    }

    [Test]
    public void OneLogLinePerOutsideCall_AndNoneForTheOfferedOnes()
    {
        var logger = new RecordingLogger<ToolsetMembershipGuardTests>();

        Apply(ToolsetMembershipMode.Observe, logger, Call(OutsideSkill), Call(OfferedSkill), Call("drop_table"));

        logger.Entries.Count(entry => entry.Level == LogLevel.Warning).ShouldBe(2);
    }

    [Test]
    public void TheShippedMode_IsObserve_UntilTheLoggedRateProvesEnforceSafe()
    {
        LLMLoopConstants.DefaultToolsetMembershipMode.ShouldBe(ToolsetMembershipMode.Observe);
    }

    [Test]
    public void FailureConsumers_DoNotCountAMembershipRejectionAsASkillFailure()
    {
        var rejected = new LLMFunctionCall
        {
            FunctionName = OutsideSkill,
            Success = false,
            IsOutsideToolset = true,
            IsRejectedOutsideToolset = true,
            Result = LLMLoopConstants.ToolsetMembershipRejectedResult
        };
        var genuine = new LLMFunctionCall { FunctionName = OfferedSkill, Success = false, Result = "No such contract." };

        rejected.IsRejectedWithoutRunning.ShouldBeTrue();
        LLMBackgroundTaskService.SelectFailedCalls([rejected, genuine]).ShouldBe([genuine]);
        TurnClosingNotices.LastUnrecoveredFailure([genuine, rejected], string.Empty).ShouldBe(genuine);
        TurnClosingNotices.LastUnrecoveredFailure([rejected], string.Empty).ShouldBeNull();
        StoppedTurnSummary.ExecutedCalls([rejected, genuine]).ShouldBe([genuine]);
    }

    [Test]
    public void ObservedOutsideCall_StaysARealFailureForTheConsumers()
    {
        var observed = new LLMFunctionCall
        {
            FunctionName = OutsideSkill,
            Success = false,
            IsOutsideToolset = true,
            Result = "Error: no such client."
        };

        observed.IsRejectedWithoutRunning.ShouldBeFalse();
        LLMBackgroundTaskService.SelectFailedCalls([observed]).ShouldBe([observed]);
        StoppedTurnSummary.ExecutedCalls([observed]).ShouldBe([observed]);
    }

    [Test]
    public void IsRejectedWithoutRunning_IsTrueForEachRejectionKindAndFalseOtherwise()
    {
        new LLMFunctionCall { IsRejectedRepeat = true }.IsRejectedWithoutRunning.ShouldBeTrue();
        new LLMFunctionCall { IsRejectedOutsideToolset = true }.IsRejectedWithoutRunning.ShouldBeTrue();
        new LLMFunctionCall { IsRejectedByReadOnlyRecipeGuard = true }.IsRejectedWithoutRunning.ShouldBeTrue();
        new LLMFunctionCall { Success = false }.IsRejectedWithoutRunning.ShouldBeFalse();
        new LLMFunctionCall { IsOutsideToolset = true }.IsRejectedWithoutRunning.ShouldBeFalse();
        new LLMFunctionCall { SkippedByStop = true }.IsRejectedWithoutRunning.ShouldBeFalse();
        new LLMFunctionCall { RequiresConfirmation = true }.IsRejectedWithoutRunning.ShouldBeFalse();
    }
}

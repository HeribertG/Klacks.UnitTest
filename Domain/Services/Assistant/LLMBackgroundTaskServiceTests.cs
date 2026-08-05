// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Truth-table tests for the reflection trigger filter of LLMBackgroundTaskService: only genuinely
/// failed function calls count as a negative signal; a confirmation prompt sets Success=false without
/// being a failure and must never trigger a skill-failure reflection.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMBackgroundTaskServiceTests
{
    private static LLMFunctionCall Call(bool success, bool requiresConfirmation = false) => new()
    {
        FunctionName = "some_skill",
        Success = success,
        RequiresConfirmation = requiresConfirmation
    };

    [Test]
    public void SelectFailedCalls_GenuineFailure_IsSelected()
    {
        var calls = new List<LLMFunctionCall> { Call(success: false) };

        LLMBackgroundTaskService.SelectFailedCalls(calls).Count.ShouldBe(1);
    }

    [Test]
    public void SelectFailedCalls_ConfirmationOnlyTurn_SelectsNothing()
    {
        var calls = new List<LLMFunctionCall> { Call(success: false, requiresConfirmation: true) };

        LLMBackgroundTaskService.SelectFailedCalls(calls).ShouldBeEmpty();
    }

    [Test]
    public void SelectFailedCalls_MixedTurn_KeepsOnlyTheGenuineFailure()
    {
        var genuine = Call(success: false);
        var calls = new List<LLMFunctionCall>
        {
            Call(success: true),
            Call(success: false, requiresConfirmation: true),
            genuine
        };

        var selected = LLMBackgroundTaskService.SelectFailedCalls(calls);

        selected.ShouldHaveSingleItem();
        selected[0].ShouldBeSameAs(genuine);
    }

    [Test]
    public void SelectFailedCalls_AllSuccessful_SelectsNothing()
    {
        var calls = new List<LLMFunctionCall> { Call(success: true), Call(success: true) };

        LLMBackgroundTaskService.SelectFailedCalls(calls).ShouldBeEmpty();
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMService.RejectRepeatedWriteCalls: the execution-time guard that replaced the
/// per-iteration toolset shrinking. The tool array now stays identical across loop iterations
/// (provider prompt-prefix caching), so the once-per-turn rule for write skills is enforced here:
/// a repeat call of a write skill from an earlier iteration is rejected with an instructive result,
/// read-only skills and navigation may repeat freely, same-batch duplicates stay allowed and a
/// recipe-forced iteration is exempt.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceRejectRepeatedWriteCallsTests
{
    private static LLMFunctionCall MakeCall(string name) => new() { FunctionName = name };

    private static HashSet<string> Called(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    [Test]
    public void RepeatedWriteCall_IsRejectedAndRemovedFromExecutableList()
    {
        var repeat = MakeCall("create_employee");
        var calls = new List<LLMFunctionCall> { repeat };

        var executable = LLMService.RejectRepeatedWriteCalls(calls, Called("create_employee"), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Is.Empty);
            Assert.That(repeat.Success, Is.False);
            Assert.That(repeat.IsRejectedRepeat, Is.True);
            Assert.That(repeat.Result, Is.EqualTo(LLMLoopConstants.RepeatedWriteCallRejectedResult));
        });
    }

    [TestCase("get_employee")]
    [TestCase("list_contracts")]
    [TestCase("search_employees")]
    [TestCase("navigate_to")]
    public void ReadOnlyAndNavigationCalls_MayRepeatAcrossIterations(string name)
    {
        var call = MakeCall(name);
        var calls = new List<LLMFunctionCall> { call };

        var executable = LLMService.RejectRepeatedWriteCalls(calls, Called(name), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Has.Count.EqualTo(1));
            Assert.That(call.Success, Is.True);
            Assert.That(call.IsRejectedRepeat, Is.False);
        });
    }

    // The repeat guard and SkillRiskClassifier now share ReadOnlySkillPrefixes. These names were
    // classified read-only by the classifier but treated as write calls by this guard before the merge.
    [TestCase("find_replacement")]
    [TestCase("read_email")]
    [TestCase("read_messages")]
    [TestCase("lookup_location")]
    [TestCase("verify_my_last_action")]
    [TestCase("check_absence_conflicts")]
    [TestCase("detect_conflicts")]
    [TestCase("interpret_resource_monitor")]
    [TestCase("validate_address")]
    [TestCase("test_imap_connection")]
    [TestCase("evaluate_scenario")]
    [TestCase("generate_period_summary")]
    public void SharedReadOnlyPrefixes_MayRepeatAcrossIterations(string name)
    {
        var call = MakeCall(name);
        var calls = new List<LLMFunctionCall> { call };

        var executable = LLMService.RejectRepeatedWriteCalls(calls, Called(name), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Has.Count.EqualTo(1));
            Assert.That(call.IsRejectedRepeat, Is.False);
        });
    }

    [TestCase("Get_Employee")]
    [TestCase("NAVIGATE_TO")]
    public void ReadOnlyAndNavigationDetection_IsCaseInsensitive(string name)
    {
        var call = MakeCall(name);
        var calls = new List<LLMFunctionCall> { call };

        var executable = LLMService.RejectRepeatedWriteCalls(calls, Called(name), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Has.Count.EqualTo(1));
            Assert.That(call.IsRejectedRepeat, Is.False);
        });
    }

    [Test]
    public void SameBatchDuplicates_StayAllowed_WhenNothingWasCalledBefore()
    {
        var first = MakeCall("create_employee");
        var second = MakeCall("create_employee");
        var calls = new List<LLMFunctionCall> { first, second };

        var executable = LLMService.RejectRepeatedWriteCalls(calls, Called(), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Has.Count.EqualTo(2));
            Assert.That(first.IsRejectedRepeat, Is.False);
            Assert.That(second.IsRejectedRepeat, Is.False);
        });
    }

    [Test]
    public void RecipeForcedIteration_MayRepeatWriteCalls()
    {
        var repeat = MakeCall("add_client_to_group");
        var calls = new List<LLMFunctionCall> { repeat };

        var executable = LLMService.RejectRepeatedWriteCalls(calls, Called("add_client_to_group"), forceRecipe: true);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Has.Count.EqualTo(1));
            Assert.That(repeat.Success, Is.True);
            Assert.That(repeat.IsRejectedRepeat, Is.False);
        });
    }

    [Test]
    public void MixedBatch_RejectsOnlyTheRepeatedWriteCall()
    {
        var freshWrite = MakeCall("update_client");
        var repeatedWrite = MakeCall("create_employee");
        var readOnly = MakeCall("list_groups");
        var calls = new List<LLMFunctionCall> { freshWrite, repeatedWrite, readOnly };

        var executable = LLMService.RejectRepeatedWriteCalls(
            calls, Called("create_employee", "list_groups"), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Is.EqualTo(new[] { freshWrite, readOnly }));
            Assert.That(repeatedWrite.IsRejectedRepeat, Is.True);
            Assert.That(freshWrite.IsRejectedRepeat, Is.False);
            Assert.That(readOnly.IsRejectedRepeat, Is.False);
        });
    }

    [Test]
    public void RepeatCheck_IsCaseInsensitive()
    {
        var repeat = MakeCall("Create_Employee");
        var calls = new List<LLMFunctionCall> { repeat };

        var executable = LLMService.RejectRepeatedWriteCalls(calls, Called("create_employee"), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Is.Empty);
            Assert.That(repeat.IsRejectedRepeat, Is.True);
        });
    }
}

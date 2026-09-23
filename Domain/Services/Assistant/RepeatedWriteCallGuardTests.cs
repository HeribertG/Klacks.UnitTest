// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RepeatedWriteCallGuard (formerly LLMService.RejectRepeatedWriteCalls): the
/// execution-time guard that replaced the per-iteration toolset shrinking. The tool array now stays identical across loop iterations
/// (provider prompt-prefix caching), so the once-per-turn rule for write skills is enforced here:
/// a repeat call of a write skill from an earlier iteration is rejected with an instructive result,
/// read-only skills and navigation may repeat freely, same-batch duplicates stay allowed and a
/// recipe-forced iteration is exempt. Catalogued read-only actions of a multi-action skill
/// (ReadOnlySkillActions, e.g. manage_pending_notes with action "read") count as read-only: they are never
/// rejected and never recorded, so the live bug - read, then mark_delivered rejected as a repeat, notes
/// re-delivered on every turn - cannot recur, while a repeated mark_delivered is still rejected.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RepeatedWriteCallGuardTests
{
    private static LLMFunctionCall MakeCall(string name) => new() { FunctionName = name };

    private static HashSet<string> Called(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    [Test]
    public void RepeatedWriteCall_IsRejectedAndRemovedFromExecutableList()
    {
        var repeat = MakeCall("create_employee");
        var calls = new List<LLMFunctionCall> { repeat };

        var executable = RepeatedWriteCallGuard.Reject(calls, Called("create_employee"), forceRecipe: false);

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

        var executable = RepeatedWriteCallGuard.Reject(calls, Called(name), forceRecipe: false);

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

        var executable = RepeatedWriteCallGuard.Reject(calls, Called(name), forceRecipe: false);

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

        var executable = RepeatedWriteCallGuard.Reject(calls, Called(name), forceRecipe: false);

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

        var executable = RepeatedWriteCallGuard.Reject(calls, Called(), forceRecipe: false);

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

        var executable = RepeatedWriteCallGuard.Reject(calls, Called("add_client_to_group"), forceRecipe: true);

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

        var executable = RepeatedWriteCallGuard.Reject(
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

        var executable = RepeatedWriteCallGuard.Reject(calls, Called("create_employee"), forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Is.Empty);
            Assert.That(repeat.IsRejectedRepeat, Is.True);
        });
    }

    private const string PendingNotes = "manage_pending_notes";
    private const string NoteIds = "0b8e3c9a-1111-4a4a-9a9a-000000000001";

    private static LLMFunctionCall PendingNotesCall(string? action, bool asJson = false)
    {
        var parameters = new Dictionary<string, object>();
        if (action != null)
        {
            parameters["action"] = asJson ? JsonSerializer.SerializeToElement(action) : action;
        }

        if (string.Equals(action, "mark_delivered", StringComparison.OrdinalIgnoreCase))
        {
            parameters["noteIds"] = NoteIds;
        }

        return new LLMFunctionCall { FunctionName = PendingNotes, Parameters = parameters };
    }

    private static List<LLMFunctionCall> RunIteration(HashSet<string> called, params LLMFunctionCall[] calls) =>
        RepeatedWriteCallGuard.RejectAndRecord(calls.ToList(), called, forceRecipe: false);

    [Test]
    public void PendingNotesRead_ThenMarkDelivered_BothExecute()
    {
        var called = Called();
        var read = PendingNotesCall("read", asJson: true);
        var markDelivered = PendingNotesCall("mark_delivered", asJson: true);

        var firstIteration = RunIteration(called, read);
        var secondIteration = RunIteration(called, markDelivered);

        Assert.Multiple(() =>
        {
            Assert.That(firstIteration, Is.EqualTo(new[] { read }));
            Assert.That(secondIteration, Is.EqualTo(new[] { markDelivered }));
            Assert.That(markDelivered.IsRejectedRepeat, Is.False);
        });
    }

    [Test]
    public void MarkDelivered_IsExecutable_WhenTheSkillWasSeenOnlyThroughAReadCall()
    {
        var called = Called();
        RepeatedWriteCallGuard.Record(new[] { PendingNotesCall("read") }, called);
        var markDelivered = PendingNotesCall("mark_delivered");

        var executable = RepeatedWriteCallGuard.Reject(new List<LLMFunctionCall> { markDelivered }, called, forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(called, Is.Empty);
            Assert.That(executable, Is.EqualTo(new[] { markDelivered }));
            Assert.That(markDelivered.Success, Is.True);
        });
    }

    [TestCase("read")]
    [TestCase("READ")]
    [TestCase(" Read ")]
    [TestCase(null)]
    public void PendingNotesRead_RepeatedAcrossIterations_IsNeverRejected(string? action)
    {
        var called = Called();
        RunIteration(called, PendingNotesCall(action));
        var secondRead = PendingNotesCall(action);

        var executable = RunIteration(called, secondRead);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Is.EqualTo(new[] { secondRead }));
            Assert.That(secondRead.IsRejectedRepeat, Is.False);
        });
    }

    [Test]
    public void MarkDelivered_Repeated_IsStillRejectedOnTheSecondAndThirdAttempt()
    {
        var called = Called();
        RunIteration(called, PendingNotesCall("read"));
        RunIteration(called, PendingNotesCall("mark_delivered"));
        var second = PendingNotesCall("mark_delivered");
        var third = PendingNotesCall("mark_delivered");

        var secondExecutable = RunIteration(called, second);
        var thirdExecutable = RunIteration(called, third);

        Assert.Multiple(() =>
        {
            Assert.That(secondExecutable, Is.Empty);
            Assert.That(thirdExecutable, Is.Empty);
            Assert.That(second.IsRejectedRepeat, Is.True);
            Assert.That(third.IsRejectedRepeat, Is.True);
            Assert.That(third.Result, Is.EqualTo(LLMLoopConstants.RepeatedWriteCallRejectedResult));
        });
    }

    [Test]
    public void ReadAfterMarkDelivered_StillExecutes()
    {
        var called = Called();
        RunIteration(called, PendingNotesCall("mark_delivered"));
        var read = PendingNotesCall("read");

        var executable = RunIteration(called, read);

        Assert.That(executable, Is.EqualTo(new[] { read }));
    }

    [TestCase("list")]
    [TestCase("delete")]
    public void UncataloguedPendingNotesActions_AreTreatedAsWrites(string action)
    {
        var called = Called();
        RunIteration(called, PendingNotesCall(action));
        var repeat = PendingNotesCall(action);

        var executable = RunIteration(called, repeat);

        Assert.That(repeat.IsRejectedRepeat, Is.True);
        Assert.That(executable, Is.Empty);
    }

    [Test]
    public void UnrelatedWriteSkill_WithAReadAction_IsStillRejectedAsRepeat()
    {
        var called = Called();
        var first = new LLMFunctionCall
        {
            FunctionName = "create_employee",
            Parameters = new Dictionary<string, object> { ["action"] = "read" }
        };
        RunIteration(called, first);
        var repeat = new LLMFunctionCall
        {
            FunctionName = "create_employee",
            Parameters = new Dictionary<string, object> { ["action"] = "read" }
        };

        var executable = RunIteration(called, repeat);

        Assert.Multiple(() =>
        {
            Assert.That(executable, Is.Empty);
            Assert.That(repeat.IsRejectedRepeat, Is.True);
        });
    }
}

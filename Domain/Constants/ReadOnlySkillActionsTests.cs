// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The read-only action catalogue lets manage_pending_notes read its notes without blocking the later
/// mark_delivered call of the same turn. Pinned: the action is read from raw tool-call arguments (a
/// JsonElement as well as a plain string), matching ignores case and surrounding whitespace, an absent
/// action counts as a read only because the skill itself resolves it to one, and nothing outside the
/// catalogue - another action, another skill - is treated as read-only.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class ReadOnlySkillActionsTests
{
    private static Dictionary<string, object> Action(object value) =>
        new() { [ReadOnlySkillActions.ActionParameter] = value };

    [TestCase("read")]
    [TestCase("READ")]
    [TestCase("  read ")]
    public void PendingNotesRead_IsReadOnly(string action)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReadOnlySkillActions.IsReadOnlyCall(SkillNames.ManagePendingNotes, Action(action)), Is.True);
            Assert.That(
                ReadOnlySkillActions.IsReadOnlyCall(
                    SkillNames.ManagePendingNotes, Action(JsonSerializer.SerializeToElement(action))),
                Is.True);
        });
    }

    [Test]
    public void PendingNotesWithoutAction_IsReadOnly_BecauseTheSkillDefaultsToRead()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ReadOnlySkillActions.IsReadOnlyCall(SkillNames.ManagePendingNotes, new Dictionary<string, object>()),
                Is.True);
            Assert.That(ReadOnlySkillActions.IsReadOnlyCall(SkillNames.ManagePendingNotes, null), Is.True);
            Assert.That(ReadOnlySkillActions.IsReadOnlyCall(SkillNames.ManagePendingNotes, Action("  ")), Is.True);
        });
    }

    [TestCase("mark_delivered")]
    [TestCase("list")]
    [TestCase("readall")]
    public void OtherPendingNotesActions_AreNotReadOnly(string action)
    {
        Assert.That(ReadOnlySkillActions.IsReadOnlyCall(SkillNames.ManagePendingNotes, Action(action)), Is.False);
    }

    [TestCase("create_employee")]
    [TestCase("manage_something_else")]
    [TestCase("")]
    [TestCase(null)]
    public void UncataloguedSkills_AreNeverReadOnly(string? skill)
    {
        Assert.That(ReadOnlySkillActions.IsReadOnlyCall(skill, Action("read")), Is.False);
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroAssistantGuard: the assistant may change macros it created (Assistant) freely, may rename
/// its extended copies (AssistantExtension) but never change their script, may delete both, and may not assign
/// either to an order; templates, imports and user macros are refused with an actionable message per origin. The
/// persisted integer values of MacroOrigin are frozen, because the database column stores them.
/// </summary>

using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MacroAssistantGuardTests
{
    private static MacroResource MacroWith(MacroOrigin origin) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Sunday rate",
        Origin = origin
    };

    [TestCase(MacroOrigin.Seed, "template")]
    [TestCase(MacroOrigin.Import, "region setup")]
    [TestCase(MacroOrigin.User, "created by a user")]
    public void EnsureMayUpdate_ForeignOrigin_Throws(MacroOrigin origin, string expectedReason)
    {
        var ex = Should.Throw<InvalidRequestException>(
            () => MacroAssistantGuard.EnsureMayUpdate(MacroWith(origin), changesScript: false));

        ex.Message.ShouldContain("Sunday rate");
        ex.Message.ShouldContain(expectedReason);
        ex.Message.ShouldContain("extended copy");
    }

    [TestCase(MacroOrigin.Seed, "template")]
    [TestCase(MacroOrigin.Import, "region setup")]
    [TestCase(MacroOrigin.User, "created by a user")]
    public void EnsureMayDelete_ForeignOrigin_Throws(MacroOrigin origin, string expectedReason)
    {
        var ex = Should.Throw<InvalidRequestException>(() => MacroAssistantGuard.EnsureMayDelete(MacroWith(origin)));

        ex.Message.ShouldContain(expectedReason);
        ex.Message.ShouldContain("administrator");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AssistantOrigin_PassesBothChecks(bool changesScript)
    {
        var macro = MacroWith(MacroOrigin.Assistant);

        Should.NotThrow(() => MacroAssistantGuard.EnsureMayUpdate(macro, changesScript));
        Should.NotThrow(() => MacroAssistantGuard.EnsureMayDelete(macro));
    }

    [Test]
    public void AssistantExtension_ScriptChange_Throws_WithTheWayToChangeIt()
    {
        var ex = Should.Throw<InvalidRequestException>(
            () => MacroAssistantGuard.EnsureMayUpdate(MacroWith(MacroOrigin.AssistantExtension), changesScript: true));

        ex.Message.ShouldContain("Sunday rate");
        ex.Message.ShouldContain("new extend_macro on the original macro");
        ex.Message.ShouldContain("name and the description");
    }

    [Test]
    public void AssistantExtension_RenameAndDelete_Pass()
    {
        var macro = MacroWith(MacroOrigin.AssistantExtension);

        Should.NotThrow(() => MacroAssistantGuard.EnsureMayUpdate(macro, changesScript: false));
        Should.NotThrow(() => MacroAssistantGuard.EnsureMayDelete(macro));
    }

    [TestCase(MacroOrigin.Assistant)]
    [TestCase(MacroOrigin.AssistantExtension)]
    public void FindAssignmentRefusal_AssistantOwned_Refuses(MacroOrigin origin)
    {
        var macro = MacroWith(origin);

        var refusal = MacroAssistantGuard.FindAssignmentRefusal(macro);

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("Sunday rate");
        refusal.ShouldContain(macro.Id.ToString());
    }

    [TestCase(MacroOrigin.Seed)]
    [TestCase(MacroOrigin.Import)]
    [TestCase(MacroOrigin.User)]
    public void FindAssignmentRefusal_NotAssistantOwned_AllowsIt(MacroOrigin origin)
    {
        MacroAssistantGuard.FindAssignmentRefusal(MacroWith(origin)).ShouldBeNull();
    }

    [Test]
    public void MacroOrigin_PersistedValues_AreFrozen()
    {
        ((int)MacroOrigin.User).ShouldBe(0);
        ((int)MacroOrigin.Seed).ShouldBe(1);
        ((int)MacroOrigin.Import).ShouldBe(2);
        ((int)MacroOrigin.Assistant).ShouldBe(3);
        ((int)MacroOrigin.AssistantExtension).ShouldBe(4);
        Enum.GetValues<MacroOrigin>().Length.ShouldBe(5);
    }
}

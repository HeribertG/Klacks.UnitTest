// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UnattendedSkillPolicy — the fail-closed gate in front of every background skill run.
/// It runs against the REAL SkillRiskClassifier so a skill moving in or out of the sensitive list is
/// covered here too, and only the registry is substituted.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Scheduling;
using Klacks.Api.Application.Skills.Meta;

namespace Klacks.UnitTest.Application.Assistant.Scheduling;

[TestFixture]
public class UnattendedSkillPolicyTests
{
    private ISkillRegistry _registry = null!;
    private UnattendedSkillPolicy _policy = null!;

    private static readonly IReadOnlyList<string> OwnerPermissions =
        new[] { "CanViewClients", "CanEditClients" };

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _policy = new UnattendedSkillPolicy(_registry, new SkillRiskClassifier());
    }

    private void Known(string name, SkillCategory category = SkillCategory.Crud)
    {
        _registry.GetSkillByName(name).Returns(new SkillDescriptor(
            name,
            "test skill",
            category,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null));
    }

    [Test]
    public void Decide_EmptyOwnerPermissions_IsDenied()
    {
        Known("update_client");

        var decision = _policy.Decide("update_client", Array.Empty<string>());

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldContain("never frozen");
    }

    [Test]
    public void Decide_EmptyOwnerPermissions_IsDeniedBeforeTheRegistryIsAsked()
    {
        _policy.Decide("update_client", Array.Empty<string>());

        _registry.DidNotReceive().GetSkillByName(Arg.Any<string>());
    }

    [Test]
    public void Decide_UnknownSkill_IsDenied()
    {
        _registry.GetSkillByName("vanished_skill").Returns((SkillDescriptor?)null);

        var decision = _policy.Decide("vanished_skill", OwnerPermissions);

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldContain("no longer exists");
    }

    [Test]
    public void Decide_SensitiveSkill_IsDenied()
    {
        Known("delete_client");

        var decision = _policy.Decide("delete_client", OwnerPermissions);

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldContain("sensitive");
    }

    [Test]
    public void Decide_IrreversibleSkill_IsAllowed()
    {
        Known("update_client");

        var decision = _policy.Decide("update_client", OwnerPermissions);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldBeNull();
    }

    [Test]
    public void Decide_ReadOnlySkill_IsAllowed()
    {
        Known("list_clients", SkillCategory.Query);

        var decision = _policy.Decide("list_clients", OwnerPermissions);

        decision.Allowed.ShouldBeTrue();
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins that no unattended path (scheduled task, proactive heartbeat, inbound mail or messenger automation)
/// may run a macro write skill, at any autonomy level and with or without the irreversible opt-in. Runs
/// against the real SkillRiskClassifier, so moving a macro skill out of the sensitive list fails here.
/// apply_company_rule is included because its CustomMacro draft kind also creates a macro, and the macro assignment
/// skills of stage 2 because they switch the macro a shift order or an absence type calculates with.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Scheduling;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Assistant.Scheduling;

[TestFixture]
public class UnattendedMacroSkillPolicyTests
{
    private const string OwnerPermission = "CanEditSettings";

    private static readonly IReadOnlyList<string> AdminPermissions = new[] { OwnerPermission };

    private ISkillRegistry _registry = null!;
    private UnattendedSkillPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _policy = new UnattendedSkillPolicy(_registry, new SkillRiskClassifier());
    }

    private void Known(string name)
    {
        _registry.GetSkillByName(name).Returns(new SkillDescriptor(
            name,
            "test skill",
            SkillCategory.Crud,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null));
    }

    [TestCase("create_macro")]
    [TestCase("update_macro")]
    [TestCase("delete_macro")]
    [TestCase("extend_macro")]
    [TestCase("assign_macro_to_shift")]
    [TestCase("assign_macro_to_absence_type")]
    [TestCase("revert_macro_assignment")]
    [TestCase("apply_company_rule")]
    public void MacroWriteSkill_IsDeniedOnEveryUnattendedPath(string skillName)
    {
        Known(skillName);

        foreach (var kind in Enum.GetValues<UnattendedExecutionKind>())
        {
            foreach (var level in Enum.GetValues<AutonomyLevel>())
            {
                foreach (var optIn in new[] { false, true })
                {
                    var decision = _policy.Decide(new UnattendedSkillRequest(skillName, AdminPermissions, level, kind, optIn));

                    decision.Allowed.ShouldBeFalse($"{skillName} {kind} {level} optIn={optIn}");
                    decision.DenyReason.ShouldBe(UnattendedDenyReason.SensitiveSkill, $"{skillName} {kind} {level} optIn={optIn}");
                }
            }
        }
    }
}

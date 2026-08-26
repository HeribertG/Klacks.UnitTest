// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard keeping UnattendedSkillPolicy's risk-class matrix total. The policy is the
/// fail-closed gate in front of every background skill run, so a SkillRiskClass added later must not be
/// able to slip through an unhandled switch arm. The guard walks the full cross product of risk class,
/// autonomy level, execution kind and opt-in flag and demands two things: every DEFINED risk class is
/// answered by an explicit arm (never by the default arm, which reports UnknownRiskClass), and every
/// verdict is internally consistent - allowed means no reason at all, denied means both a human-readable
/// text and a machine-readable cause.
///
/// The risk classifier is substituted rather than used for real: the undefined-enum case that proves the
/// default arm exists can only be produced by a stub, since the real classifier never returns one.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Scheduling;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UnattendedSkillPolicyMatrixGuardTests
{
    private const string SkillName = "guarded_skill";
    private const SkillRiskClass UnrecognisedRiskClass = (SkillRiskClass)int.MaxValue;

    private static readonly IReadOnlyList<string> OwnerPermissions = new[] { "CanViewClients" };

    private ISkillRegistry _registry = null!;
    private ISkillRiskClassifier _classifier = null!;
    private UnattendedSkillPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _classifier = Substitute.For<ISkillRiskClassifier>();
        _registry.GetSkillByName(SkillName).Returns(new SkillDescriptor(
            SkillName,
            "guarded skill",
            SkillCategory.Crud,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null));

        _policy = new UnattendedSkillPolicy(_registry, _classifier);
    }

    [Test]
    public void Decide_AnswersEveryDefinedRiskClass_WithAnExplicitVerdict()
    {
        foreach (var riskClass in Enum.GetValues<SkillRiskClass>())
        {
            _classifier.Classify(Arg.Any<SkillDescriptor>()).Returns(riskClass);

            foreach (var request in RequestsFor(riskClass))
            {
                var decision = _policy.Decide(request);

                decision.ShouldNotBeNull();
                decision.DenyReason.ShouldNotBe(
                    UnattendedDenyReason.UnknownRiskClass,
                    $"Risk class {riskClass} fell through to the default arm of UnattendedSkillPolicy. " +
                    "Add an explicit arm for it instead of letting the fail-closed default catch it.");

                AssertVerdictIsConsistent(decision, riskClass, request);
            }
        }
    }

    [Test]
    public void Decide_UnrecognisedRiskClass_IsDeniedInsteadOfWavedThrough()
    {
        _classifier.Classify(Arg.Any<SkillDescriptor>()).Returns(UnrecognisedRiskClass);

        foreach (var request in RequestsFor(UnrecognisedRiskClass))
        {
            var decision = _policy.Decide(request);

            decision.Allowed.ShouldBeFalse();
            decision.DenyReason.ShouldBe(UnattendedDenyReason.UnknownRiskClass);
            decision.Reason.ShouldNotBeNullOrWhiteSpace();
        }
    }

    private static IEnumerable<UnattendedSkillRequest> RequestsFor(SkillRiskClass riskClass)
    {
        foreach (var level in Enum.GetValues<AutonomyLevel>())
        {
            foreach (var kind in Enum.GetValues<UnattendedExecutionKind>())
            {
                foreach (var optIn in new[] { false, true })
                {
                    yield return new UnattendedSkillRequest(SkillName, OwnerPermissions, level, kind, optIn);
                }
            }
        }
    }

    private static void AssertVerdictIsConsistent(
        UnattendedSkillDecision decision, SkillRiskClass riskClass, UnattendedSkillRequest request)
    {
        var caseName = $"{riskClass} / {request.AutonomyLevel} / {request.ExecutionKind} / " +
                       $"optIn={request.AllowIrreversibleUnattended}";

        if (decision.Allowed)
        {
            decision.Reason.ShouldBeNull(caseName);
            decision.DenyReason.ShouldBe(UnattendedDenyReason.None, caseName);
            return;
        }

        decision.Reason.ShouldNotBeNullOrWhiteSpace(caseName);
        decision.DenyReason.ShouldNotBe(UnattendedDenyReason.None, caseName);
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard: every skill a confirmation preview provider supports is classified Sensitive. The autonomy gate
/// builds the preview, stores its binding with the token and refuses the direct-token path only for a held Sensitive
/// skill, which no autonomy level ever lets through; a provider for a skill of another risk class could be bypassed by
/// the level check and would run with no preview, no binding and no token. The skills are found by name, from the seed
/// definitions and from the skill implementation attributes of Klacks.Api, and are classified with the category they are
/// seeded with; each provider type found by reflection is built with substitutes for its dependencies (see
/// ConfirmationPreviewProviderFixtures). Also guards that every provider supports at least one skill (so a mistyped
/// name cannot make the check vacuous) and that every provider type is registered by the real composition root: the guard
/// runs the real AddDomainServices on an empty configuration and reads the service descriptors.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ConfirmationPreviewProviderSensitiveGuardTests
{
    [Test]
    public void EverySkillAProviderSupports_IsClassifiedSensitive_WithItsSeededCategory()
    {
        var classifier = new SkillRiskClassifier();
        var notSensitive = new List<string>();

        foreach (var (provider, name, category) in ConfirmationPreviewProviderFixtures.SupportedSkills())
        {
            category.ShouldNotBeNull($"'{name}' has an implementation but no seed entry, so its category is unknown.");
            var descriptor = new SkillDescriptor(name, "guard", category.Value, [], [], [], null);
            if (classifier.Classify(descriptor) != SkillRiskClass.Sensitive)
            {
                notSensitive.Add($"{provider.GetType().Name} supports '{name}' ({category})");
            }
        }

        notSensitive.ShouldBeEmpty(
            "A preview provider is only reached for a held Sensitive skill: " + string.Join("; ", notSensitive));
    }

    [Test]
    public void EveryProvider_SupportsAtLeastOneKnownSkill()
    {
        var names = ConfirmationPreviewProviderFixtures.CandidateSkills().Keys;
        var providers = ConfirmationPreviewProviderFixtures.Providers();

        providers.ShouldNotBeEmpty();
        foreach (var provider in providers)
        {
            names.Any(provider.Supports).ShouldBeTrue(
                $"{provider.GetType().Name} supports no seeded or implemented skill; its Supports names are probably mistyped.");
        }
    }

    [Test]
    public void TheBoundSkills_AreSupportedByAProvider()
    {
        var providers = ConfirmationPreviewProviderFixtures.Providers();
        string[] bound =
        [
            Klacks.Plugin.Messaging.Application.Constants.MessagingConstants.SkillSendMessage,
            BulkDeleteWorksSkill.SkillName,
            CutShiftSkill.SkillName,
            MacroAssignmentSkillNames.AssignToShift,
            MacroAssignmentSkillNames.AssignToAbsenceType,
            MacroAssignmentSkillNames.Revert,
            SealOpenOrdersSkill.SkillName,
            SealShiftSkill.SkillName
        ];

        foreach (var name in bound)
        {
            providers.Any(provider => provider.Supports(name)).ShouldBeTrue($"No provider supports '{name}'.");
        }
    }

    [Test]
    public void EveryProviderType_IsRegisteredByTheRealCompositionRoot()
    {
        var registered = ConfirmationPreviewProviderFixtures.RegisteredProviderTypes();

        registered.ShouldNotBeEmpty("The composition root registers no ISkillConfirmationPreviewProvider at all.");
        foreach (var type in ConfirmationPreviewProviderFixtures.ProviderTypes())
        {
            registered.ShouldContain(type, $"{type.Name} is not registered as ISkillConfirmationPreviewProvider.");
        }
    }
}

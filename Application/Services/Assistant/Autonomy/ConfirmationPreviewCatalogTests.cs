// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ConfirmationPreviewCatalog: a skill has a server preview when any registered preview provider supports
/// it (compared without regard to case); with no providers, or for a skill none of them supports, it has none.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant.Autonomy;

[TestFixture]
public class ConfirmationPreviewCatalogTests
{
    private static ISkillConfirmationPreviewProvider ProviderFor(params string[] skills)
    {
        var provider = Substitute.For<ISkillConfirmationPreviewProvider>();
        provider.Supports(Arg.Any<string>()).Returns(call =>
            skills.Contains(call.Arg<string>(), StringComparer.OrdinalIgnoreCase));
        return provider;
    }

    [Test]
    public void ASkillSupportedByAnyProvider_HasAPreview()
    {
        var catalog = new ConfirmationPreviewCatalog([ProviderFor("a"), ProviderFor("b", "c")]);

        catalog.HasPreview("a").ShouldBeTrue();
        catalog.HasPreview("c").ShouldBeTrue();
    }

    [Test]
    public void ASkillNoProviderSupports_HasNoPreview()
    {
        var catalog = new ConfirmationPreviewCatalog([ProviderFor("a")]);

        catalog.HasPreview("z").ShouldBeFalse();
    }

    [Test]
    public void WithoutProviders_NoSkillHasAPreview()
    {
        new ConfirmationPreviewCatalog([]).HasPreview("a").ShouldBeFalse();
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Betriebsphase: locks the entity-name hallucination guard of the group resolver. An unknown or
/// invented group name must produce a helpful error that lists the real group names — it must never
/// resolve silently or invent a group.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class GroupResolverGuardTests
{
    private static Group Group(string name) => new() { Name = name };

    [Test]
    public void Resolve_UnknownGroup_ReturnsErrorListingRealNames()
    {
        var groups = new List<Group> { Group("Deutschschweiz Zürich"), Group("Bern") };

        var (group, error) = GroupResolver.Resolve(groups, "unsere gruppe");

        group.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("not found");
        error.ShouldContain("Deutschschweiz Zürich");
        error.ShouldContain("Bern");
        error.ShouldContain("do not invent groups");
    }

    [Test]
    public void Resolve_ExactName_Resolves()
    {
        var groups = new List<Group> { Group("Deutschschweiz Zürich"), Group("Bern") };

        var (group, error) = GroupResolver.Resolve(groups, "Bern");

        group.ShouldNotBeNull();
        group.Name.ShouldBe("Bern");
        error.ShouldBeNull();
    }

    [Test]
    public void Resolve_AmbiguousPartial_ReturnsDisambiguationError()
    {
        var groups = new List<Group> { Group("Bern Nord"), Group("Bern Süd") };

        var (group, error) = GroupResolver.Resolve(groups, "Bern");

        group.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("ambiguous");
        error.ShouldContain("Bern Nord");
        error.ShouldContain("Bern Süd");
    }
}

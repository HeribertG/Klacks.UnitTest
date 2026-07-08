// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupResolver: an exact name wins over a longer group that merely contains it
/// (the "Bern" vs "Bern-wöchentlich" regression), a unique partial name still resolves, an
/// ambiguous partial name returns a disambiguation error instead of silently picking one, an
/// unknown name lists the real groups, and soft-deleted groups are ignored. Also covers the
/// 2026-07-07 failure classes: a label-prefixed query ("Gruppe Deutschschweiz Zürich") resolves
/// to the most specific group, accent-damaged input (mojibake, missing umlauts, single typos)
/// still resolves, and a short group code is never hijacked by a label word.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupResolverTests
{
    private static readonly Guid BernId = Guid.NewGuid();
    private static readonly Guid BernWeeklyId = Guid.NewGuid();
    private static readonly Guid ZurichId = Guid.NewGuid();

    private static List<Group> TwoBernGroups() => new()
    {
        new() { Id = BernWeeklyId, Name = "Bern-wöchentlich" },
        new() { Id = BernId, Name = "Bern" },
        new() { Id = ZurichId, Name = "Zürich" }
    };

    [Test]
    public void ExactNameWins_OverLongerContainingGroup()
    {
        var (group, error) = GroupResolver.Resolve(TwoBernGroups(), "Bern");

        Assert.That(error, Is.Null);
        Assert.That(group, Is.Not.Null);
        Assert.That(group!.Id, Is.EqualTo(BernId));
    }

    [Test]
    public void ExactMatch_IsCaseInsensitiveAndTrimmed()
    {
        var (group, error) = GroupResolver.Resolve(TwoBernGroups(), "  bErN  ");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(BernId));
    }

    [Test]
    public void UniquePartialName_StillResolves()
    {
        var (group, error) = GroupResolver.Resolve(TwoBernGroups(), "wöchentlich");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(BernWeeklyId));
    }

    [Test]
    public void AmbiguousPartialName_ReturnsDisambiguationError_AndNoGroup()
    {
        var (group, error) = GroupResolver.Resolve(TwoBernGroups(), "ern");

        Assert.That(group, Is.Null);
        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("ambiguous"));
        Assert.That(error, Does.Contain("Bern"));
        Assert.That(error, Does.Contain("Bern-wöchentlich"));
        Assert.That(error, Does.Contain("do not guess"));
    }

    [Test]
    public void UnknownName_ReturnsNotFound_ListingRealGroups()
    {
        var (group, error) = GroupResolver.Resolve(TwoBernGroups(), "Administration");

        Assert.That(group, Is.Null);
        Assert.That(error, Does.Contain("not found"));
        Assert.That(error, Does.Contain("Bern"));
        Assert.That(error, Does.Contain("Zürich"));
    }

    [Test]
    public void SoftDeletedGroups_AreIgnored()
    {
        var groups = new List<Group>
        {
            new() { Id = BernId, Name = "Bern", IsDeleted = true },
            new() { Id = ZurichId, Name = "Zürich" }
        };

        var (group, error) = GroupResolver.Resolve(groups, "Bern");

        Assert.That(group, Is.Null);
        Assert.That(error, Does.Contain("not found"));
    }

    [Test]
    public void NoGroupsAtAll_ReturnsThereAreNoGroupsYet()
    {
        var (group, error) = GroupResolver.Resolve(new List<Group>(), "Bern");

        Assert.That(group, Is.Null);
        Assert.That(error, Does.Contain("There are no groups yet."));
    }

    private static readonly Guid GrId = Guid.NewGuid();
    private static readonly Guid DeutschschweizZurichId = Guid.NewGuid();

    private static List<Group> SwissGroups() => new()
    {
        new() { Id = GrId, Name = "GR" },
        new() { Id = ZurichId, Name = "Zürich" },
        new() { Id = DeutschschweizZurichId, Name = "Deutschschweiz Zürich" }
    };

    [Test]
    public void LabelPrefixedQuery_ResolvesMostSpecificCoveredGroup()
    {
        var (group, error) = GroupResolver.Resolve(SwissGroups(), "Gruppe Deutschschweiz Zürich");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(DeutschschweizZurichId));
    }

    [Test]
    public void MojibakeDamagedUmlaut_StillResolvesCorrectGroup_NotTheShortCode()
    {
        var (group, error) = GroupResolver.Resolve(SwissGroups(), "Gruppe Deutschschweiz Z├╝rich");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(DeutschschweizZurichId));
    }

    [Test]
    public void AccentlessQuery_ResolvesAccentedGroup()
    {
        var (group, error) = GroupResolver.Resolve(SwissGroups(), "Zurich");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(ZurichId));
    }

    [Test]
    public void SingleTypoInGroupName_StillResolves()
    {
        var (group, error) = GroupResolver.Resolve(SwissGroups(), "Zürih");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(ZurichId));
    }

    [Test]
    public void ShortGroupCode_IsNeverHijackedByLabelWord()
    {
        var groups = new List<Group>
        {
            new() { Id = GrId, Name = "GR" },
            new() { Id = BernId, Name = "Bern" }
        };

        var (group, error) = GroupResolver.Resolve(groups, "Gruppe Deutschschweiz Zürich");

        Assert.That(group, Is.Null);
        Assert.That(error, Does.Contain("not found"));
    }

    [Test]
    public void NotFound_TellsModelNotToRetryWithTheSameName()
    {
        var (group, error) = GroupResolver.Resolve(TwoBernGroups(), "Administration");

        Assert.That(group, Is.Null);
        Assert.That(error, Does.Contain("Do not call this skill again with the same group name"));
        Assert.That(error, Does.Contain("do not invent groups"));
    }
}

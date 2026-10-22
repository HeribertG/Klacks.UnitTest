// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GroupResolver: an exact name wins over a longer group that merely contains it
/// (the "Bern" vs "Bern-wöchentlich" regression), a unique partial name still resolves, an
/// ambiguous partial name returns a disambiguation error instead of silently picking one, an
/// unknown name lists the real groups, and soft-deleted groups are ignored. Also covers the
/// 2026-07-07 failure classes: a label-prefixed query ("Gruppe Deutschschweiz Zürich") resolves
/// to the most specific group, missing umlauts resolve under both policies, damaged input (mojibake,
/// single typos, misheard names) resolves only under the Lenient policy of read-only skills while the
/// default Strict policy answers with the "Did the user mean ...?" ask-back, and a short group code is
/// never hijacked by a label word.
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
    public void MisheardSpokenName_ResolvesPhonetically_OnlyWhenLenient()
    {
        var groups = new List<Group>
        {
            new() { Id = BernId, Name = "Meier" },
            new() { Id = ZurichId, Name = "Zürich" }
        };

        var (group, error) = GroupResolver.Resolve(groups, "Mayer", NameMatchPolicy.Lenient);
        var (strictGroup, strictError) = GroupResolver.Resolve(groups, "Mayer");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(BernId));
        Assert.That(strictGroup, Is.Null);
        Assert.That(strictError, Is.EqualTo(NameResolutionMessages.SuggestedName("Meier")));
    }

    [Test]
    public void LabelDecoratedCamelCaseName_ResolvesViaCompactStage()
    {
        var camelId = Guid.NewGuid();
        var groups = new List<Group>
        {
            new() { Id = camelId, Name = "NordOst" },
            new() { Id = ZurichId, Name = "Zürich" }
        };

        var (group, error) = GroupResolver.Resolve(groups, "Gruppe Nord-Ost");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(camelId));
    }

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
    public void IdenticalNamesUnderDifferentRoots_AmbiguityNamesParentRootAndId()
    {
        var zurich = new Group { Id = Guid.NewGuid(), Name = "Zürich" };
        var bern = new Group { Id = Guid.NewGuid(), Name = "Bern" };
        var bernWest = new Group { Id = Guid.NewGuid(), Name = "Bern West", Parent = bern.Id, Root = bern.Id };
        var pflegeZurich = new Group { Id = Guid.NewGuid(), Name = "Pflege", Parent = zurich.Id, Root = zurich.Id };
        var pflegeBern = new Group { Id = Guid.NewGuid(), Name = "Pflege", Parent = bernWest.Id, Root = bern.Id };

        var (group, error) = GroupResolver.Resolve(
            new List<Group> { zurich, bern, bernWest, pflegeZurich, pflegeBern }, "Pflege");

        Assert.That(group, Is.Null);
        Assert.That(error, Does.Contain("ambiguous"));
        Assert.That(error, Does.Contain($"'Pflege' in 'Zürich', id {pflegeZurich.Id}"));
        Assert.That(error, Does.Contain($"'Pflege' in 'Bern West' under 'Bern', id {pflegeBern.Id}"));
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
    public void MojibakeDamagedUmlaut_ResolvesCorrectGroupWhenLenient_AndIsSuggestedWhenStrict()
    {
        var (group, error) = GroupResolver.Resolve(
            SwissGroups(), "Gruppe Deutschschweiz Z├╝rich", NameMatchPolicy.Lenient);
        var (strictGroup, strictError) = GroupResolver.Resolve(SwissGroups(), "Gruppe Deutschschweiz Z├╝rich");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(DeutschschweizZurichId));
        Assert.That(strictGroup, Is.Null);
        Assert.That(strictError, Is.EqualTo(NameResolutionMessages.SuggestedName("Deutschschweiz Zürich")));
    }

    [Test]
    public void AccentlessQuery_ResolvesAccentedGroup()
    {
        var (group, error) = GroupResolver.Resolve(SwissGroups(), "Zurich");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(ZurichId));
    }

    [Test]
    public void SingleTypoInGroupName_ResolvesWhenLenient_AndIsSuggestedWhenStrict()
    {
        var (group, error) = GroupResolver.Resolve(SwissGroups(), "Zürih", NameMatchPolicy.Lenient);
        var (strictGroup, strictError) = GroupResolver.Resolve(SwissGroups(), "Zürih");

        Assert.That(error, Is.Null);
        Assert.That(group!.Id, Is.EqualTo(ZurichId));
        Assert.That(strictGroup, Is.Null);
        Assert.That(strictError, Is.EqualTo(NameResolutionMessages.SuggestedName("Zürich")));
    }

    [Test]
    public void RegionSouth_IsNeverSilentlyResolvedToRegionNorth_UnderTheDefaultPolicy()
    {
        var northId = Guid.NewGuid();
        var groups = new List<Group>
        {
            new() { Id = northId, Name = "Region North" },
            new() { Id = ZurichId, Name = "Zürich" }
        };

        var (strictGroup, strictError) = GroupResolver.Resolve(groups, "Region South");
        var (lenientGroup, lenientError) = GroupResolver.Resolve(groups, "Region South", NameMatchPolicy.Lenient);

        Assert.That(strictGroup, Is.Null);
        Assert.That(strictError, Is.EqualTo(NameResolutionMessages.SuggestedName("Region North")));
        Assert.That(strictError, Does.Contain("Nothing was changed"));
        Assert.That(lenientError, Is.Null);
        Assert.That(lenientGroup!.Id, Is.EqualTo(northId));
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

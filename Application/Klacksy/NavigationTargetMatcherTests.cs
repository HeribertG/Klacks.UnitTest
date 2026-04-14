// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using FluentAssertions;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
public class NavigationTargetMatcherTests
{
    private INavigationTargetCacheService _cache = null!;
    private NavigationTargetMatcher _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Substitute.For<INavigationTargetCacheService>();
        _sut = new NavigationTargetMatcher(_cache);
    }

    [Test]
    public void Match_returns_fast_path_on_exact_synonym_hit()
    {
        var target = new NavigationTarget { TargetId = "llm-provider", Route = "/settings", LabelKey = "x" };
        _cache.FindBySynonym("llm provider", "de").Returns(new[] { target });

        var result = _sut.Match("llm provider", "de", Array.Empty<string>());

        result.TargetId.Should().Be("llm-provider");
        result.Score.Should().Be(1.0);
        result.IsFastPath.Should().BeTrue();
    }

    [Test]
    public void Match_skips_permission_gated_target_when_user_lacks_right()
    {
        var target = new NavigationTarget { TargetId = "admin-only", Route = "/settings", LabelKey = "x", RequiredPermission = "Admin" };
        _cache.FindBySynonym("admin", "de").Returns(new[] { target });

        var result = _sut.Match("admin", "de", Array.Empty<string>());

        result.TargetId.Should().BeNull();
        result.Candidates.Should().BeEmpty();
    }

    [Test]
    public void Match_returns_null_when_no_synonyms_hit_any_token()
    {
        _cache.FindBySynonym(Arg.Any<string>(), Arg.Any<string>()).Returns(Array.Empty<NavigationTarget>());

        var result = _sut.Match("unknown query here", "de", Array.Empty<string>());

        result.TargetId.Should().BeNull();
        result.Candidates.Should().BeEmpty();
    }

    [Test]
    public void Match_accumulates_token_overlap_score_and_picks_top_candidate()
    {
        var target = new NavigationTarget { TargetId = "t1", Route = "/t1", LabelKey = "x" };
        _cache.FindBySynonym("foo", "de").Returns(new[] { target });
        _cache.FindBySynonym("bar", "de").Returns(new[] { target });

        var result = _sut.Match("foo bar", "de", Array.Empty<string>());

        result.TargetId.Should().Be("t1");
        result.Score.Should().Be(0.85);
        result.Candidates.Should().HaveCount(1);
    }

    [Test]
    public void Match_returns_null_when_top_score_below_threshold()
    {
        var target = new NavigationTarget { TargetId = "t1", Route = "/t1", LabelKey = "x" };
        _cache.FindBySynonym("foo", "de").Returns(new[] { target });
        _cache.FindBySynonym(Arg.Is<string>(s => s != "foo"), "de").Returns(Array.Empty<NavigationTarget>());

        var result = _sut.Match("foo bar baz qux", "de", Array.Empty<string>());

        result.TargetId.Should().BeNull();
        result.Candidates.Should().HaveCount(1);
        result.Score.Should().BeApproximately(0.25, 0.001);
    }
}

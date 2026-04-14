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
        result.Score.Should().BeGreaterThan(0.85);
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
    public void Match_returns_candidates_for_partial_match()
    {
        var t1 = new NavigationTarget { TargetId = "a", Route = "/a", LabelKey = "x" };
        var t2 = new NavigationTarget { TargetId = "b", Route = "/b", LabelKey = "y" };
        _cache.All.Returns(new[] { t1, t2 });
        _cache.FindBySynonym(Arg.Any<string>(), Arg.Any<string>()).Returns(Array.Empty<NavigationTarget>());

        var result = _sut.Match("unknown query here", "de", Array.Empty<string>());

        result.TargetId.Should().BeNull();
    }
}

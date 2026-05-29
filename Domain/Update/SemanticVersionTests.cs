// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Domain.Update;

using Klacks.Api.Domain.Models.Update;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SemanticVersionTests
{
    [Test]
    public void Parse_valid_version_returns_components()
    {
        var version = SemanticVersion.Parse("1.4.2");

        version.Major.ShouldBe(1);
        version.Minor.ShouldBe(4);
        version.Patch.ShouldBe(2);
    }

    [TestCase("v1.4.2")]
    [TestCase("V1.4.2")]
    [TestCase(" 1.4.2 ")]
    public void TryParse_accepts_prefix_and_whitespace(string input)
    {
        SemanticVersion.TryParse(input, out var version).ShouldBeTrue();
        version.ToString().ShouldBe("1.4.2");
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("1.2")]
    [TestCase("1.2.3.4")]
    [TestCase("a.b.c")]
    [TestCase("-1.2.3")]
    [TestCase("1.2.x")]
    public void TryParse_rejects_invalid_input(string? input)
    {
        SemanticVersion.TryParse(input, out _).ShouldBeFalse();
    }

    [Test]
    public void Parse_throws_on_invalid_input()
    {
        Should.Throw<FormatException>(() => SemanticVersion.Parse("not-a-version"));
    }

    [Test]
    public void Comparison_orders_by_major_then_minor_then_patch()
    {
        (SemanticVersion.Parse("2.0.0") > SemanticVersion.Parse("1.9.9")).ShouldBeTrue();
        (SemanticVersion.Parse("1.4.2") > SemanticVersion.Parse("1.4.1")).ShouldBeTrue();
        (SemanticVersion.Parse("1.4.0") < SemanticVersion.Parse("1.5.0")).ShouldBeTrue();
        (SemanticVersion.Parse("1.0.0") <= SemanticVersion.Parse("1.0.0")).ShouldBeTrue();
        (SemanticVersion.Parse("1.0.0") >= SemanticVersion.Parse("1.0.0")).ShouldBeTrue();
    }

    [Test]
    public void Equality_holds_for_identical_versions()
    {
        SemanticVersion.Parse("3.2.1").ShouldBe(SemanticVersion.Parse("3.2.1"));
    }

    [Test]
    public void ToString_renders_dotted_form()
    {
        new SemanticVersion(10, 0, 5).ToString().ShouldBe("10.0.5");
    }
}

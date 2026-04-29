// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Fuzzy;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Auction.Fuzzy;

[TestFixture]
public class RuleBaseLoaderTests
{
    [Test]
    public void LoadDefault_ParsesEmbeddedRules()
    {
        var rules = RuleBaseLoader.LoadDefault();
        rules.Should().NotBeEmpty();
        rules.Should().HaveCountGreaterThanOrEqualTo(30);
        rules.Should().AllSatisfy(r =>
        {
            r.Name.Should().NotBeNullOrEmpty();
            r.Antecedents.Should().NotBeEmpty();
            r.ConsequentVariable.Should().Be("BidScore");
        });
    }

    [Test]
    public void Parse_EmptyRule_Throws()
    {
        var json = "[{\"name\":\"R\",\"if\":[],\"then\":{\"var\":\"BidScore\",\"is\":\"Low\"}}]";
        Action act = () => RuleBaseLoader.Parse(json);
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Parse_MissingName_Throws()
    {
        var json = "[{\"if\":[{\"var\":\"X\",\"is\":\"Low\"}],\"then\":{\"var\":\"BidScore\",\"is\":\"Low\"}}]";
        Action act = () => RuleBaseLoader.Parse(json);
        act.Should().Throw<InvalidOperationException>();
    }
}

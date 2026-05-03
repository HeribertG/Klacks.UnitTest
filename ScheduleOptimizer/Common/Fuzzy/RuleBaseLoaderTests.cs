// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Common.Fuzzy;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Common.Fuzzy;

[TestFixture]
public class RuleBaseLoaderTests
{
    [Test]
    public void LoadDefault_ParsesEmbeddedRules()
    {
        var rules = RuleBaseLoader.LoadDefault();
        rules.ShouldNotBeEmpty();
        rules.Count().ShouldBeGreaterThanOrEqualTo(30);
        foreach (var r in rules)
        {
            r.Name.ShouldNotBeNullOrEmpty();
            r.Antecedents.ShouldNotBeEmpty();
            r.ConsequentVariable.ShouldBe("BidScore");
        }
    }

    [Test]
    public void Parse_EmptyRule_Throws()
    {
        var json = "[{\"name\":\"R\",\"if\":[],\"then\":{\"var\":\"BidScore\",\"is\":\"Low\"}}]";
        Action act = () => RuleBaseLoader.Parse(json);
        act.ShouldThrow<InvalidOperationException>();
    }

    [Test]
    public void Parse_MissingName_Throws()
    {
        var json = "[{\"if\":[{\"var\":\"X\",\"is\":\"Low\"}],\"then\":{\"var\":\"BidScore\",\"is\":\"Low\"}}]";
        Action act = () => RuleBaseLoader.Parse(json);
        act.ShouldThrow<InvalidOperationException>();
    }
}

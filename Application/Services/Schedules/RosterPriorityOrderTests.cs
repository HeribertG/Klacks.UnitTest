// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The one roster priority order of all wizard stages: user order verbatim, otherwise guaranteed hours descending, then
/// full-time hours descending; stand-ins last; stable on ties.
/// </summary>
[TestFixture]
public class RosterPriorityOrderTests
{
    [Test]
    public void UserDefined_KeepsTheIncomingOrder()
    {
        var agents = new[] { Agent("low", 10, 20), Agent("high", 40, 40), Agent("mid", 25, 30) };

        var result = Rank(agents, userDefined: true);

        result.ShouldBe(["low", "high", "mid"]);
    }

    [Test]
    public void Automatic_SortsByGuaranteedHoursDescending()
    {
        var agents = new[] { Agent("low", 10, 40), Agent("high", 40, 10), Agent("mid", 25, 20) };

        var result = Rank(agents, userDefined: false);

        result.ShouldBe(["high", "mid", "low"]);
    }

    [Test]
    public void Automatic_WithoutGuarantee_SortsByFullTimeDescending_BelowEveryGuarantee()
    {
        var agents = new[]
        {
            Agent("noGuaranteeSmall", 0, 10),
            Agent("guaranteed", 5, 0),
            Agent("noGuaranteeLarge", 0, 42),
            Agent("noGuaranteeMid", 0, 20),
        };

        var result = Rank(agents, userDefined: false);

        result.ShouldBe(["guaranteed", "noGuaranteeLarge", "noGuaranteeMid", "noGuaranteeSmall"]);
    }

    [Test]
    public void Automatic_IsStable_OnTies()
    {
        var agents = new[] { Agent("zeta", 20, 40), Agent("alpha", 20, 40), Agent("mid", 20, 40) };

        var result = Rank(agents, userDefined: false);

        result.ShouldBe(["zeta", "alpha", "mid"]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void StandIns_EndLast_InTheirOwnRelativeOrder(bool userDefined)
    {
        var agents = new[]
        {
            Agent("standinHigh", 50, 50, isStandIn: true),
            Agent("person", 10, 40),
            Agent("standinLow", 0, 0, isStandIn: true),
            Agent("personWithoutHours", 0, 0),
        };

        var result = Rank(agents, userDefined);

        result.ShouldBe(["person", "personWithoutHours", "standinHigh", "standinLow"]);
    }

    private static (string Id, double Guaranteed, double FullTime, bool IsStandIn) Agent(
        string id, double guaranteed, double fullTime, bool isStandIn = false)
        => (id, guaranteed, fullTime, isStandIn);

    private static IEnumerable<string> Rank(
        IReadOnlyList<(string Id, double Guaranteed, double FullTime, bool IsStandIn)> agents, bool userDefined)
        => RosterPriorityOrder.Apply(agents, userDefined, a => a.Guaranteed, a => a.FullTime, a => a.IsStandIn)
            .Select(a => a.Id);
}

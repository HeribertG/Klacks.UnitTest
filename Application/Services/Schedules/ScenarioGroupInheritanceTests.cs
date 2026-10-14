// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The group of a scenario a run produces: the requested group wins, otherwise the source scenario's group is
/// inherited, and a run on the real schedule without a requested group has none.
/// </summary>
[TestFixture]
public sealed class ScenarioGroupInheritanceTests
{
    private static readonly Guid RequestedGroup = Guid.NewGuid();
    private static readonly Guid SourceGroup = Guid.NewGuid();

    [Test]
    public void RequestedGroup_Wins_OverTheSourceScenariosGroup()
    {
        ScenarioGroupInheritance.Resolve(RequestedGroup, new AnalyseScenario { GroupId = SourceGroup })
            .ShouldBe(RequestedGroup);
    }

    [Test]
    public void WithoutRequestedGroup_TheSourceScenariosGroupIsInherited()
    {
        ScenarioGroupInheritance.Resolve(null, new AnalyseScenario { GroupId = SourceGroup }).ShouldBe(SourceGroup);
    }

    [Test]
    public void WithoutRequestedGroupOnTheRealSchedule_ThereIsNoGroup()
    {
        ScenarioGroupInheritance.Resolve(null, sourceScenario: null).ShouldBeNull();
    }
}

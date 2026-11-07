// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ClientTypeRules: every client type has an explicit decision (a new enum member without one fails),
/// a stand-in is plannable but no replacement candidate and has no target hours, and the replacement rule delegates
/// to the same decision.
/// </summary>

using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Clients;

[TestFixture]
public class ClientTypeRulesTests
{
    private static readonly Dictionary<EntityTypeEnum, (bool Plannable, bool ReplacementCandidate, bool WithoutTargetHours)> Expected = new()
    {
        [EntityTypeEnum.Employee] = (true, true, false),
        [EntityTypeEnum.ExternEmp] = (true, true, false),
        [EntityTypeEnum.Customer] = (false, false, false),
        [EntityTypeEnum.StandIn] = (true, false, true),
    };

    [Test]
    public void EveryEntityType_HasAnExplicitDecision()
    {
        Expected.Keys.OrderBy(type => type).ShouldBe(Enum.GetValues<EntityTypeEnum>().OrderBy(type => type));
    }

    [TestCaseSource(nameof(AllTypes))]
    public void Predicates_And_TypeLists_Agree_With_The_Decision(EntityTypeEnum type)
    {
        var expected = Expected[type];

        ClientTypeRules.IsPlannable(type).ShouldBe(expected.Plannable);
        ClientTypeRules.PlannableTypes.Contains(type).ShouldBe(expected.Plannable);
        ClientTypeRules.IsReplacementCandidate(type).ShouldBe(expected.ReplacementCandidate);
        ClientTypeRules.ReplacementCandidateTypes.Contains(type).ShouldBe(expected.ReplacementCandidate);
        ClientTypeRules.WithoutTargetHoursTypes.Contains(type).ShouldBe(expected.WithoutTargetHours);
        ReplacementPersonRule.IsEligible(type).ShouldBe(expected.ReplacementCandidate);
    }

    [Test]
    public void StandIn_IsPlannable_ButNotAReplacementCandidate_AndHasNoTargetHours()
    {
        ClientTypeRules.IsPlannable(EntityTypeEnum.StandIn).ShouldBeTrue();
        ClientTypeRules.IsReplacementCandidate(EntityTypeEnum.StandIn).ShouldBeFalse();
        ClientTypeRules.WithoutTargetHoursTypes.ShouldBe([EntityTypeEnum.StandIn]);
    }

    private static IEnumerable<EntityTypeEnum> AllTypes() => Enum.GetValues<EntityTypeEnum>();
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ClientTypeRules: every client type has an explicit decision (a new enum member without one fails),
/// a stand-in is plannable but no replacement candidate, has no target hours, is not importable and carries no master
/// data, the replacement rule delegates to the same decision, no type changes to or from a stand-in in place, and a type
/// name is read exactly (no number, no combined value, no stand-in).
/// </summary>

using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Clients;

[TestFixture]
public class ClientTypeRulesTests
{
    private static readonly Dictionary<EntityTypeEnum, (bool Plannable, bool ReplacementCandidate, bool WithoutTargetHours, bool Importable, bool StandIn, bool MasterData)> Expected = new()
    {
        [EntityTypeEnum.Employee] = (true, true, false, true, false, true),
        [EntityTypeEnum.ExternEmp] = (true, true, false, true, false, true),
        [EntityTypeEnum.Customer] = (false, false, false, true, false, true),
        [EntityTypeEnum.StandIn] = (true, false, true, false, true, false),
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
        ClientTypeRules.IsImportable(type).ShouldBe(expected.Importable);
        ClientTypeRules.ImportableTypes.Contains(type).ShouldBe(expected.Importable);
        ReplacementPersonRule.IsEligible(type).ShouldBe(expected.ReplacementCandidate);
        ClientTypeRules.IsStandIn(type).ShouldBe(expected.StandIn);
        ClientTypeRules.StandInTypes.Contains(type).ShouldBe(expected.StandIn);
        ClientTypeRules.MasterDataTypes.Contains(type).ShouldBe(expected.MasterData);
    }

    [TestCaseSource(nameof(AllTypePairs))]
    public void CanChangeType_KeepsTheType_OrChangesBetweenMasterDataTypes_NeverToOrFromAStandIn(EntityTypeEnum from, EntityTypeEnum to)
    {
        var expected = from == to || (from != EntityTypeEnum.StandIn && to != EntityTypeEnum.StandIn);

        ClientTypeRules.CanChangeType(from, to).ShouldBe(expected);
    }

    [Test]
    public void CanChangeType_EmployeeToStandIn_And_StandInToEmployee_AreRefused()
    {
        ClientTypeRules.CanChangeType(EntityTypeEnum.Employee, EntityTypeEnum.StandIn).ShouldBeFalse();
        ClientTypeRules.CanChangeType(EntityTypeEnum.StandIn, EntityTypeEnum.Employee).ShouldBeFalse();
        ClientTypeRules.CanChangeType(EntityTypeEnum.StandIn, EntityTypeEnum.ExternEmp).ShouldBeFalse();
        ClientTypeRules.CanChangeType(EntityTypeEnum.Employee, (EntityTypeEnum)99).ShouldBeFalse();
    }

    [TestCase("Employee", EntityTypeEnum.Employee)]
    [TestCase("externemp", EntityTypeEnum.ExternEmp)]
    [TestCase(" Customer ", EntityTypeEnum.Customer)]
    public void TryParseMasterDataType_ReadsTheExactName(string name, EntityTypeEnum expected)
    {
        ClientTypeRules.TryParseMasterDataType(name, out var type).ShouldBeTrue();
        type.ShouldBe(expected);
    }

    [TestCase("StandIn")]
    [TestCase("3")]
    [TestCase("0")]
    [TestCase("Employee,Customer")]
    [TestCase("Placeholder")]
    [TestCase("")]
    [TestCase(null)]
    public void TryParseMasterDataType_RefusesNumbersCombinationsAndTheStandIn(string? name)
    {
        ClientTypeRules.TryParseMasterDataType(name, out _).ShouldBeFalse();
    }

    [Test]
    public void StandIn_IsPlannable_ButNotAReplacementCandidate_AndHasNoTargetHours()
    {
        ClientTypeRules.IsPlannable(EntityTypeEnum.StandIn).ShouldBeTrue();
        ClientTypeRules.IsReplacementCandidate(EntityTypeEnum.StandIn).ShouldBeFalse();
        ClientTypeRules.WithoutTargetHoursTypes.ShouldBe([EntityTypeEnum.StandIn]);
    }

    private static IEnumerable<EntityTypeEnum> AllTypes() => Enum.GetValues<EntityTypeEnum>();

    private static IEnumerable<object[]> AllTypePairs() =>
        from source in Enum.GetValues<EntityTypeEnum>()
        from target in Enum.GetValues<EntityTypeEnum>()
        select new object[] { source, target };
}

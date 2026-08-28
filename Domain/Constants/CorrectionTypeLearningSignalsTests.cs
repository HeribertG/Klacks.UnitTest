// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard over the map that decides which correction type becomes a learning case. The map replaced a
/// membership test against SkillLearningSignals.All, which only looked right: that list carries refusal,
/// a signal no correction type can ever hold, and a name match would let any future signal named like a
/// correction type slip into the learning loop. The test reflects over CorrectionTypes so a newly added
/// type fails here instead of being silently ignored at runtime.
/// </summary>
namespace Klacks.UnitTest.Domain.Constants;

using System.Reflection;
using Klacks.Api.Domain.Constants;

[TestFixture]
public class CorrectionTypeLearningSignalsTests
{
    private static IReadOnlyList<string> AllCorrectionTypes() =>
        typeof(CorrectionTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Test]
    public void EveryCorrectionType_HasExactlyOneEntry()
    {
        var types = AllCorrectionTypes();

        foreach (var type in types)
        {
            CorrectionTypeLearningSignals.ByCorrectionType.ContainsKey(type).ShouldBeTrue(
                $"Correction type '{type}' has no entry in CorrectionTypeLearningSignals. Add it with an " +
                "explicit signal, or with null when it says nothing about which capability was missing.");
        }

        CorrectionTypeLearningSignals.ByCorrectionType.Count.ShouldBe(types.Count);
    }

    [Test]
    public void EveryMappedSignal_IsAKnownLearningSignal()
    {
        foreach (var entry in CorrectionTypeLearningSignals.ByCorrectionType.Where(e => e.Value != null))
        {
            SkillLearningSignals.All.ShouldContain(entry.Value!);
        }
    }

    [Test]
    public void NoCorrectionType_MapsToRefusal()
    {
        CorrectionTypeLearningSignals.ByCorrectionType.Values.ShouldNotContain(SkillLearningSignals.Refusal);
    }

    [Test]
    public void OnlyRoutingCorrections_ProduceASignal()
    {
        CorrectionTypeLearningSignals.Resolve(CorrectionTypes.WrongSkill).ShouldBe(SkillLearningSignals.WrongSkill);
        CorrectionTypeLearningSignals.Resolve(CorrectionTypes.NoneNeeded).ShouldBe(SkillLearningSignals.NoneNeeded);
        CorrectionTypeLearningSignals.Resolve(CorrectionTypes.Implicit).ShouldBe(SkillLearningSignals.Implicit);
        CorrectionTypeLearningSignals.Resolve(CorrectionTypes.WrongParam).ShouldBeNull();
        CorrectionTypeLearningSignals.Resolve(CorrectionTypes.RepeatedRequest).ShouldBeNull();
        CorrectionTypeLearningSignals.Resolve(CorrectionTypes.None).ShouldBeNull();
    }

    [Test]
    public void AnUnknownCorrectionType_ResolvesToNoSignal()
    {
        CorrectionTypeLearningSignals.Resolve("something_new").ShouldBeNull();
        CorrectionTypeLearningSignals.Resolve(null).ShouldBeNull();
    }
}

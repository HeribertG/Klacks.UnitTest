// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pool builder and coverage semantics: numbers/dates/UUIDs from DataJson, array counts and
/// column sums, rounding and pair-arithmetic tolerance, context texts as legitimate sources,
/// the EmptyDataDespiteSuccess visibility-trap label, and the rule that only successful
/// Data-kind calls contribute.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant.Grounding;

[TestFixture]
public class ToolResultGroundingPoolBuilderTests
{
    private static LLMFunctionCall DataCall(params string[] fragments) => new()
    {
        FunctionName = "some_skill",
        Success = true,
        ResultKind = LLMFunctionResultKind.Data,
        DataJson = fragments.ToList()
    };

    private static GroundingPool Build(LLMFunctionCall[] calls, params string?[] contextTexts)
        => ToolResultGroundingPoolBuilder.Build(calls, contextTexts, "de");

    private static AnswerClaim Number(params string[] readings)
        => new(AnswerClaimKind.Number, readings[0], readings);

    [Test]
    public void DataJson_NumbersColumnSumsAndCounts_AreCovered()
    {
        var pool = Build([DataCall("""{"Count":2,"Results":[{"Hours":8.5},{"Hours":9.0}]}""")]);

        pool.Covers(Number("8.5")).ShouldBeTrue();
        pool.Covers(Number("17.5")).ShouldBeTrue("column sum over Hours");
        pool.Covers(Number("2")).ShouldBeTrue("array length / count");
    }

    [Test]
    public void RoundingTolerance_CoversZeroToTwoDecimals()
    {
        var pool = Build([DataCall("""{"Value":173.456}""")]);

        pool.Covers(Number("173.46")).ShouldBeTrue();
        pool.Covers(Number("173.5")).ShouldBeTrue();
        pool.Covers(Number("173")).ShouldBeTrue();
        pool.Covers(Number("174")).ShouldBeFalse();
    }

    [Test]
    public void PairArithmetic_CoversSumAndDifference()
    {
        var pool = Build([DataCall("""{"A":10.5,"B":7}""")]);

        pool.Covers(Number("17.5")).ShouldBeTrue();
        pool.Covers(Number("3.5")).ShouldBeTrue();
        pool.Covers(Number("73.5")).ShouldBeFalse();
    }

    [Test]
    public void UuidAndIsoDateStrings_AreCovered()
    {
        var uuid = Guid.NewGuid();
        var pool = Build([DataCall($$"""{"Id":"{{uuid}}","Date":"2026-08-05"}""")]);

        pool.Covers(new AnswerClaim(AnswerClaimKind.Uuid, uuid.ToString(), [uuid.ToString().ToLowerInvariant()])).ShouldBeTrue();
        pool.Covers(new AnswerClaim(AnswerClaimKind.Date, "05.08.2026", ["2026-08-05"])).ShouldBeTrue();
    }

    [Test]
    public void ContextTexts_AreLegitimateSources()
    {
        var pool = Build([], "Stimmt es, dass wir 1.234,50 CHF Budget haben?");

        pool.Covers(Number("1234.5")).ShouldBeTrue("user-stated number is grounded");
        pool.TextCorpus.ShouldContain("budget");
    }

    [Test]
    public void EmptyResults_SetTheVisibilityTrapLabel_AndCoverNothing()
    {
        var pool = Build([DataCall("""{"Results":[],"Count":0,"TotalCount":0}""")]);

        pool.EmptyDataDespiteSuccess.ShouldBeTrue();
        pool.Covers(Number("5")).ShouldBeFalse();
    }

    [Test]
    public void MeaningfulData_DoesNotSetTheLabel()
    {
        var pool = Build([DataCall("""{"Results":[{"Name":"Meier","Hours":8.5}],"Count":1}""")]);

        pool.EmptyDataDespiteSuccess.ShouldBeFalse();
    }

    [Test]
    public void NonDataCalls_DoNotContribute()
    {
        var errorCall = new LLMFunctionCall
        {
            FunctionName = "failing_skill",
            Success = false,
            ResultKind = LLMFunctionResultKind.Error,
            DataJson = ["""{"Secret":999}"""]
        };

        var pool = Build([errorCall]);

        pool.Covers(Number("999")).ShouldBeFalse();
    }

    [Test]
    public void NameStrings_LandLowercasedInTheCorpus()
    {
        var pool = Build([DataCall("""{"Results":[{"FirstName":"Anna","LastName":"Meier"}]}""")]);

        pool.TextCorpus.ShouldContain("anna");
        pool.TextCorpus.ShouldContain("meier");
    }

    [Test]
    public void NumberCapDisablesDerivations_ButKeepsExactCoverage()
    {
        var fragment = "{" + string.Join(",", Enumerable.Range(1, 220).Select(i => $"\"V{i}\":{i * 1000}")) + "}";
        var pool = Build([DataCall(fragment)]);

        pool.DerivationsDisabled.ShouldBeTrue();
        pool.Covers(Number("1000")).ShouldBeTrue();
        pool.Covers(Number("3000")).ShouldBeTrue("exact value exists");
    }
}

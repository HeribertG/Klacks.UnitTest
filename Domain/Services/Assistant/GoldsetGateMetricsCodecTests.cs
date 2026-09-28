// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Contract of proposed_skill_changes.gate_metrics_json. The export script reads these camelCase property names
/// from the stored JSON, so renaming a property of GoldsetGateMetrics must fail here before it silently breaks
/// the script.
/// </summary>
namespace Klacks.UnitTest.Domain.Services.Assistant;

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetGateMetricsCodecTests
{
    private static readonly string[] ExpectedPropertyNames =
    [
        "referenceEvalRunId", "model", "scorerVersion", "holdoutReplays", "holdoutMeasured", "holdoutRegressions",
        "holdoutFixed", "trainMissesReplayed", "trainMeasured", "trainMissesFixed", "trainRegressions",
        "goldenCaseRegressions", "netGain", "minNetGain", "verdict", "isCalibration", "measuredAtUtc",
        "unmeasuredAttempts"
    ];

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[\"legacy\"]")]
    public void ReadUnmeasuredAttempts_WithoutStoredMetrics_IsZero(string? json)
    {
        GoldsetGateMetricsCodec.ReadUnmeasuredAttempts(json).ShouldBe(0);
    }

    [Test]
    public void ReadUnmeasuredAttempts_OfAMeasuredVerdict_IsZero()
    {
        var json = GoldsetGateMetricsCodec.Serialize(SampleMetrics() with { UnmeasuredAttempts = 1 });

        GoldsetGateMetricsCodec.ReadUnmeasuredAttempts(json).ShouldBe(0);
    }

    [Test]
    public void ReadUnmeasuredAttempts_OfANotMeasuredVerdict_IsTheStoredCount()
    {
        var json = GoldsetGateMetricsCodec.Serialize(
            SampleMetrics() with { Verdict = GoldsetGateVerdicts.NotMeasured, UnmeasuredAttempts = 1 });

        GoldsetGateMetricsCodec.ReadUnmeasuredAttempts(json).ShouldBe(1);
    }

    [Test]
    public void ReadUnmeasuredAttempts_OfANotMeasuredVerdictWithoutACount_IsOne()
    {
        GoldsetGateMetricsCodec.ReadUnmeasuredAttempts($"{{\"verdict\":\"{GoldsetGateVerdicts.NotMeasured}\"}}").ShouldBe(1);
    }

    [Test]
    public void TheSerialisedMetrics_CarryExactlyTheContractPropertyNames()
    {
        using var document = JsonDocument.Parse(GoldsetGateMetricsCodec.Serialize(SampleMetrics()));

        document.RootElement.EnumerateObject().Select(property => property.Name)
            .ShouldBe(ExpectedPropertyNames, ignoreOrder: true);
    }

    [Test]
    public void TheSerialisedMetrics_CarryTheValues()
    {
        var metrics = SampleMetrics();

        using var document = JsonDocument.Parse(GoldsetGateMetricsCodec.Serialize(metrics));
        var root = document.RootElement;

        root.GetProperty("referenceEvalRunId").GetGuid().ShouldBe(metrics.ReferenceEvalRunId);
        root.GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.Passed);
        root.GetProperty("netGain").GetInt32().ShouldBe(2);
        root.GetProperty("isCalibration").GetBoolean().ShouldBeFalse();
        root.GetProperty("trainMissesFixed").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["ts-t", "para-ts-t-1"]);
    }

    private static GoldsetGateMetrics SampleMetrics() => new(
        Guid.NewGuid(),
        "deepseek-v4-pro",
        6,
        HoldoutReplays: 1,
        HoldoutMeasured: 1,
        HoldoutRegressions: [],
        HoldoutFixed: [],
        TrainMissesReplayed: 2,
        TrainMeasured: 2,
        TrainMissesFixed: ["ts-t", "para-ts-t-1"],
        TrainRegressions: [],
        GoldenCaseRegressions: [],
        NetGain: 2,
        MinNetGain: 1,
        Verdict: GoldsetGateVerdicts.Passed,
        IsCalibration: false,
        MeasuredAtUtc: new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc));
}

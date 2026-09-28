// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The evidence of a goldset-born proposal must carry the eval item ids the gate replays, and every row
/// written before this format (a bare array of messages) must still parse - as evidence without items.
/// </summary>
namespace Klacks.UnitTest.Domain.Services.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetMissEvidenceCodecTests
{
    [Test]
    public void Evidence_SurvivesARoundTrip()
    {
        var evidence = new GoldsetMissEvidence(
            ["Zeige den Umsatz pro Kunde"],
            [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, "ts-011"), new GoldsetItemRef(TurnEvalDefaults.ParaphraseGoldset, "para-ts-011-2")]);

        var parsed = GoldsetMissEvidenceCodec.Parse(GoldsetMissEvidenceCodec.Serialize(evidence));

        parsed.Examples.ShouldHaveSingleItem().ShouldBe("Zeige den Umsatz pro Kunde");
        parsed.Items.Count.ShouldBe(2);
        parsed.Items[1].ShouldBe(new GoldsetItemRef(TurnEvalDefaults.ParaphraseGoldset, "para-ts-011-2"));
    }

    [Test]
    public void TheSerialisedForm_IsCamelCase()
    {
        var json = GoldsetMissEvidenceCodec.Serialize(
            new GoldsetMissEvidence([], [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, "ts-011")]));

        json.ShouldContain("\"items\"");
        json.ShouldContain("\"itemId\":\"ts-011\"");
    }

    [TestCase("[\"a correction excerpt\"]")]
    [TestCase("{\"clusterId\":\"x\"}")]
    [TestCase("not json")]
    [TestCase("")]
    public void LegacyOrForeignEvidence_ParsesToNoItems(string json)
    {
        GoldsetMissEvidenceCodec.Parse(json).Items.ShouldBeEmpty();
    }
}

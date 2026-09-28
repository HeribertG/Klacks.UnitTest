// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Exercises TurnGoldsetParaphraseQualityChecks directly against small in-memory fixtures, so the checking
/// logic itself is proven correct without waiting for turn-selection-v1-paraphrases.json to be generated (the
/// file-backed TurnGoldsetParaphraseQualityTests stays Ignored until then). Covers both the positive case and
/// the negative fixtures a spec review asked for: too many paraphrases per source, a suffix outside the
/// allowed range, a paraphrase of a holdout source, a duplicate id, and a message repeating the holdout or the
/// base item.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnGoldsetParaphraseQualityChecksTests
{
    private const string TrainMessage = "how many clients do we have";
    private const string HoldoutMessage = "show me the schedule for today";
    private const string DefaultExpectedTool = "list_clients";

    private static readonly string TrainSourceId = FindId(GoldsetPartitioner.IsTrain, "fixture-train");
    private static readonly string HoldoutSourceId = FindId(GoldsetPartitioner.IsHoldout, "fixture-holdout");

    [Test]
    public void AValidSetOfParaphrases_HasNoViolationsInAnyCheck()
    {
        var baseById = GivenBaseItems();
        var paraphrases = new List<TurnGoldsetItem>
        {
            Paraphrase(TrainSourceId, 1, "how many clients are there"),
            Paraphrase(TrainSourceId, 2, "count of clients please"),
            Paraphrase(TrainSourceId, 3, "how many client records exist"),
        };

        TurnGoldsetParaphraseQualityChecks.ValidateIdsAndPartitions(paraphrases, baseById).ShouldBeEmpty();
        TurnGoldsetParaphraseQualityChecks.ValidateGroupSizeAndSuffixRange(paraphrases).ShouldBeEmpty();
        TurnGoldsetParaphraseQualityChecks.ValidateExpectationInheritance(paraphrases, baseById).ShouldBeEmpty();
        TurnGoldsetParaphraseQualityChecks.FindMessagesRepeatingTheDefaultGoldset(paraphrases, baseById).ShouldBeEmpty();
        TurnGoldsetParaphraseQualityChecks.FindMessagesRepeatingAHoldoutItem(paraphrases, baseById).ShouldBeEmpty();
        TurnGoldsetParaphraseQualityChecks.FindDuplicateMessagesAmongParaphrases(paraphrases).ShouldBeEmpty();
    }

    [Test]
    public void TooManyParaphrasesFromOneSource_IsFlagged()
    {
        var paraphrases = new List<TurnGoldsetItem>
        {
            Paraphrase(TrainSourceId, 1, "wording one"),
            Paraphrase(TrainSourceId, 2, "wording two"),
            Paraphrase(TrainSourceId, 3, "wording three"),
            Paraphrase(TrainSourceId, 4, "wording four"),
        };

        var violations = TurnGoldsetParaphraseQualityChecks.ValidateGroupSizeAndSuffixRange(paraphrases);

        violations.ShouldContain(violation => violation.Contains("more than", StringComparison.Ordinal));
    }

    [Test]
    public void ASuffixOutsideRange_IsFlaggedEvenWhenTheGroupSizeIsFine()
    {
        var paraphrases = new List<TurnGoldsetItem> { Paraphrase(TrainSourceId, 4, "a lone out-of-range paraphrase") };

        var violations = TurnGoldsetParaphraseQualityChecks.ValidateGroupSizeAndSuffixRange(paraphrases);

        violations.ShouldContain(violation => violation.Contains("outside 1..", StringComparison.Ordinal));
        violations.ShouldNotContain(violation => violation.Contains("more than", StringComparison.Ordinal));
    }

    [Test]
    public void AParaphraseOfAHoldoutSource_IsFlagged()
    {
        var baseById = GivenBaseItems();
        var paraphrases = new List<TurnGoldsetItem> { Paraphrase(HoldoutSourceId, 1, "a paraphrase of a holdout item") };

        var violations = TurnGoldsetParaphraseQualityChecks.ValidateIdsAndPartitions(paraphrases, baseById);

        violations.ShouldContain(violation => violation.Contains("holdout item", StringComparison.Ordinal));
    }

    [Test]
    public void ADuplicateId_IsFlagged()
    {
        var baseById = GivenBaseItems();
        var paraphrases = new List<TurnGoldsetItem>
        {
            Paraphrase(TrainSourceId, 1, "first wording"),
            Paraphrase(TrainSourceId, 1, "second wording"),
        };

        var violations = TurnGoldsetParaphraseQualityChecks.ValidateIdsAndPartitions(paraphrases, baseById);

        violations.ShouldContain(violation => violation.Contains("duplicate id", StringComparison.Ordinal));
    }

    [Test]
    public void AParaphraseRepeatingTheBaseTrainMessage_IsFlagged()
    {
        var baseById = GivenBaseItems();
        var paraphrases = new List<TurnGoldsetItem> { Paraphrase(TrainSourceId, 1, TrainMessage.ToUpperInvariant()) };

        TurnGoldsetParaphraseQualityChecks.FindMessagesRepeatingTheDefaultGoldset(paraphrases, baseById)
            .ShouldHaveSingleItem().ShouldBe(paraphrases[0].Id);
    }

    [Test]
    public void AParaphraseRepeatingAHoldoutMessage_IsFlagged()
    {
        var baseById = GivenBaseItems();
        var paraphrases = new List<TurnGoldsetItem> { Paraphrase(TrainSourceId, 1, HoldoutMessage) };

        TurnGoldsetParaphraseQualityChecks.FindMessagesRepeatingAHoldoutItem(paraphrases, baseById)
            .ShouldHaveSingleItem().ShouldBe(paraphrases[0].Id);
    }

    private static Dictionary<string, TurnGoldsetItem> GivenBaseItems() => new(StringComparer.Ordinal)
    {
        [TrainSourceId] = NewItem(TrainSourceId, TrainMessage),
        [HoldoutSourceId] = NewItem(HoldoutSourceId, HoldoutMessage),
    };

    private static TurnGoldsetItem NewItem(string id, string message) => new()
    {
        Id = id,
        Message = message,
        Locale = "en",
        ExpectedTool = DefaultExpectedTool,
    };

    private static TurnGoldsetItem Paraphrase(string sourceId, int suffix, string message) => new()
    {
        Id = $"para-{sourceId}-{suffix}",
        Message = message,
        Locale = "en",
        ExpectedTool = DefaultExpectedTool,
    };

    private static string FindId(Func<string, bool> matchesPartition, string prefix)
    {
        for (var i = 0; i < 1000; i++)
        {
            var candidate = $"{prefix}-{i}";
            if (matchesPartition(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No id under prefix '{prefix}' landed in the wanted partition within 1000 tries.");
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard of the shipped turn-intent head (turn-intent-head.v1.json): it is structurally valid, bound to the local
/// e5 embedding space, trained on the CURRENT labelset (a changed labelset must be retrained with
/// scripts/train-turn-intent-head.ps1) and on the CURRENT hand-feature sources, and every per-locale flip is backed by its own recorded evidence - no locale
/// is switched on whose out-of-fold counts do not clear TurnIntentLocaleGate, and none is switched off that does.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

[TestFixture]
public class TurnIntentHeadFileGuardTests
{
    private static readonly string[] HeadPath = ["Klacks.Api", "Application", "Klacksy", "turn-intent-head.v1.json"];

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    private TurnIntentHeadWeights _head = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var path = RepositoryRootLocator.RequireFile(HeadPath);
        _head = JsonSerializer.Deserialize<TurnIntentHeadWeights>(File.ReadAllText(path), SerializerOptions)
                ?? throw new InvalidDataException("turn-intent-head.v1.json is empty.");
    }

    [Test]
    public void TheHead_IsStructurallyValid()
    {
        FileTurnIntentHeadProvider.Validate(_head).ShouldBeNull();
        _head.Means.Length.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension + _head.HandFeatureNames.Length);
        _head.AskThreshold.ShouldBe(TurnIntentDefaults.AskConfidenceThreshold);
    }

    [Test]
    public void TheHead_IsBoundToTheLocalE5EmbeddingSpace()
    {
        _head.EmbeddingSpaceId.ShouldBe(
            $"{KnowledgeIndexConstants.LocalEmbeddingSpacePrefix}{KnowledgeIndexConstants.EmbeddingModelName}@{KnowledgeIndexConstants.EmbeddingDimension}");
    }

    [Test]
    public void TheHead_WasTrainedOnTheCurrentLabelset()
    {
        var labelset = File.ReadAllText(RepositoryRootLocator.RequireFile(
            ["Klacks.Api", "Application", "Skills", "Goldsets", TurnIntentGoldsetReader.TurnIntentFileName]));

        _head.DatasetHash.ShouldBe(
            TurnIntentLabelsetFingerprint.Compute(labelset),
            "turn-intent-v1.json changed after training; run scripts/train-turn-intent-head.ps1");
    }

    [Test]
    public void TheHead_WasTrainedOnTheCurrentHandFeatureSources()
    {
        var apiDirectory = Path.GetDirectoryName(RepositoryRootLocator.RequireFile(["Klacks.Api", "Klacks.Api.csproj"]))!;

        _head.FeatureSourceHash.ShouldBe(
            TurnIntentFeatureSourceFingerprint.Compute(apiDirectory),
            "MutationIntentDetector, TurnIntentHandFeatures or a mutation-intent.json changed after training; run scripts/train-turn-intent-head.ps1");
    }

    [Test]
    public void EveryLocaleFlip_MatchesItsRecordedEvidence()
    {
        var violations = _head.Locales
            .Where(verdict => verdict.Enabled != TurnIntentLocaleGate.ShowsImprovement(verdict))
            .Select(verdict => $"{verdict.Locale}: enabled={verdict.Enabled}")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Test]
    public void EveryLabelsetLocale_HasAVerdict()
    {
        var labelsetLocales = TurnIntentGoldsetReader.LoadTrainable().Select(item => item.Locale).Distinct().Order();

        _head.Locales.Select(verdict => verdict.Locale).Order().ShouldBe(labelsetLocales);
    }
}

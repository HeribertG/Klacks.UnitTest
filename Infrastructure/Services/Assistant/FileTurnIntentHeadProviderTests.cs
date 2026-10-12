// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Application.Services.Assistant.TurnIntent;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class FileTurnIntentHeadProviderTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "turn-intent-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void MissingFile_YieldsNoHead()
    {
        Create(Path.Combine(_directory, "absent.json")).Current.ShouldBeNull();
    }

    [Test]
    public void ValidFile_IsLoaded()
    {
        var path = Write(JsonSerializer.Serialize(TurnIntentHeadTestFactory.Create(), CamelCase));

        var head = Create(path).Current;

        head.ShouldNotBeNull();
        head.EmbeddingSpaceId.ShouldBe(TurnIntentHeadTestFactory.SpaceId);
    }

    [Test]
    public void LocaleVerdicts_RoundTripThroughTheFile()
    {
        var path = Write(JsonSerializer.Serialize(
            TurnIntentHeadTestFactory.Create(TurnIntentHeadTestFactory.Verdict("zh-TW", true)), CamelCase));

        var verdict = Create(path).Current!.Locales.Single();

        verdict.ShouldBe(TurnIntentHeadTestFactory.Verdict("zh-TW", true));
    }

    [Test]
    public void NullArrays_YieldNoHead()
    {
        Create(Write("{\"version\":1,\"classes\":null,\"locales\":null}")).Current.ShouldBeNull();
    }

    [Test]
    public void BrokenJson_YieldsNoHead()
    {
        Create(Write("{ not json")).Current.ShouldBeNull();
    }

    [Test]
    public void InconsistentHead_YieldsNoHead()
    {
        var head = TurnIntentHeadTestFactory.Create();
        var broken = new Klacks.Api.Domain.Models.Assistant.TurnIntent.TurnIntentHeadWeights
        {
            Version = head.Version,
            EmbeddingSpaceId = head.EmbeddingSpaceId,
            Classes = head.Classes,
            HandFeatureNames = head.HandFeatureNames,
            Means = head.Means,
            Scales = head.Scales,
            Weights = [head.Weights[0], head.Weights[1]],
            Bias = head.Bias,
            Temperature = head.Temperature,
        };

        Create(Write(JsonSerializer.Serialize(broken, CamelCase))).Current.ShouldBeNull();
    }

    private string Write(string content)
    {
        var path = Path.Combine(_directory, "head.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static FileTurnIntentHeadProvider Create(string path) =>
        new(path, Substitute.For<ILogger<FileTurnIntentHeadProvider>>());
}

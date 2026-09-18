// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The synthetic lookup result must be unambiguous (exactly one hit), stable across runs, must echo
/// what the model itself searched for, and must carry the same "{Message}\nData: {json}" shape every
/// real skill result carries, so the second replay step measures the model's next choice and not its
/// reaction to an unfamiliar payload shape.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class SyntheticLookupResultFactoryTests
{
    private const string SearchedName = "Amstutz";
    private const string DataLabel = "\nData: ";

    private static JsonElement ParseData(string result)
    {
        var dataIndex = result.IndexOf(DataLabel, StringComparison.Ordinal);
        dataIndex.ShouldBeGreaterThanOrEqualTo(0);
        using var document = JsonDocument.Parse(result[(dataIndex + DataLabel.Length)..]);
        return document.RootElement.Clone();
    }

    [Test]
    public void EchoesTheFirstNonEmptyStringArgument_AsTheSingleHit()
    {
        var result = SyntheticLookupResultFactory.Build(new Dictionary<string, object>
        {
            ["includeInactive"] = false,
            ["searchTerm"] = SearchedName
        });

        var root = ParseData(result);
        root.GetProperty("success").GetBoolean().ShouldBeTrue();
        root.GetProperty("totalCount").GetInt32().ShouldBe(1);
        var items = root.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("name").GetString().ShouldBe(SearchedName);
        items[0].GetProperty("id").GetString().ShouldBe(TurnEvalDefaults.SyntheticLookupEntityId);
    }

    [Test]
    public void ReadsAStringArgumentThatArrivedAsJsonElement()
    {
        using var source = JsonDocument.Parse($"\"{SearchedName}\"");

        var result = SyntheticLookupResultFactory.Build(new Dictionary<string, object>
        {
            ["searchTerm"] = source.RootElement.Clone()
        });

        ParseData(result).GetProperty("items")[0].GetProperty("name").GetString().ShouldBe(SearchedName);
    }

    [Test]
    public void WithoutAnyStringArgument_FallsBackToTheNeutralName()
    {
        var result = SyntheticLookupResultFactory.Build(new Dictionary<string, object> { ["limit"] = 10 });

        ParseData(result).GetProperty("items")[0].GetProperty("name").GetString()
            .ShouldBe(TurnEvalDefaults.SyntheticLookupFallbackName);
    }

    [Test]
    public void IsDeterministic()
    {
        var parameters = new Dictionary<string, object> { ["searchTerm"] = SearchedName };

        SyntheticLookupResultFactory.Build(parameters).ShouldBe(SyntheticLookupResultFactory.Build(parameters));
    }

    [Test]
    public void CarriesAMessageLineBeforeTheDataLabel_MatchingTheProductionShape()
    {
        var result = SyntheticLookupResultFactory.Build(new Dictionary<string, object> { ["searchTerm"] = SearchedName });

        result.ShouldContain(DataLabel);
        var message = result[..result.IndexOf(DataLabel, StringComparison.Ordinal)];
        message.ShouldNotBeNullOrWhiteSpace();
        message.ShouldContain(SearchedName);
    }
}

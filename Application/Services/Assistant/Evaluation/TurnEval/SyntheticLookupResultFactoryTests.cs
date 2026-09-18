// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The synthetic lookup result must be unambiguous (exactly one hit), stable across runs and must echo
/// what the model itself searched for, so the second replay step measures the model's next choice and
/// not its reaction to implausible data.
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

    [Test]
    public void EchoesTheFirstNonEmptyStringArgument_AsTheSingleHit()
    {
        var json = SyntheticLookupResultFactory.Build(new Dictionary<string, object>
        {
            ["includeInactive"] = false,
            ["searchTerm"] = SearchedName
        });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
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

        var json = SyntheticLookupResultFactory.Build(new Dictionary<string, object>
        {
            ["searchTerm"] = source.RootElement.Clone()
        });

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("items")[0].GetProperty("name").GetString().ShouldBe(SearchedName);
    }

    [Test]
    public void WithoutAnyStringArgument_FallsBackToTheNeutralName()
    {
        var json = SyntheticLookupResultFactory.Build(new Dictionary<string, object> { ["limit"] = 10 });

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("items")[0].GetProperty("name").GetString()
            .ShouldBe(TurnEvalDefaults.SyntheticLookupFallbackName);
    }

    [Test]
    public void IsDeterministic()
    {
        var parameters = new Dictionary<string, object> { ["searchTerm"] = SearchedName };

        SyntheticLookupResultFactory.Build(parameters).ShouldBe(SyntheticLookupResultFactory.Build(parameters));
    }
}

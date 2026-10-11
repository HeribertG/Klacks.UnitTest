// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for SearchGeocodingQueryHandler: passes the trimmed query and language to IGeocodingService.SearchAsync,
/// copies every outcome onto the resource and pins the JSON contract (status as string, null coordinates present).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Handlers.Geocoding;
using Klacks.Api.Application.Queries.Geocoding;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Geocoding;

[TestFixture]
public class SearchGeocodingQueryHandlerTests
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private IGeocodingService _geocodingService = null!;
    private SearchGeocodingQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _geocodingService = Substitute.For<IGeocodingService>();
        _handler = new SearchGeocodingQueryHandler(_geocodingService);
    }

    [Test]
    public async Task Handle_Found_CopiesCoordinatesAndDisplayName()
    {
        _geocodingService.SearchAsync("Zürich", "de", Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResult(GeocodingSearchOutcome.Found, 47.37, 8.54, "Zürich, Schweiz"));

        var result = await _handler.Handle(new SearchGeocodingQuery("  Zürich ", " de "), CancellationToken.None);

        result.Status.ShouldBe(GeocodingSearchOutcome.Found);
        result.Latitude.ShouldBe(47.37);
        result.Longitude.ShouldBe(8.54);
        result.DisplayName.ShouldBe("Zürich, Schweiz");
        await _geocodingService.Received(1).SearchAsync("Zürich", "de", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_BlankLanguage_IsPassedAsNull()
    {
        _geocodingService.SearchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResult(GeocodingSearchOutcome.NotFound, null, null, null));

        await _handler.Handle(new SearchGeocodingQuery("Zürich", "  "), CancellationToken.None);

        await _geocodingService.Received(1).SearchAsync("Zürich", null, Arg.Any<CancellationToken>());
    }

    [TestCase(GeocodingSearchOutcome.NotFound)]
    [TestCase(GeocodingSearchOutcome.Disabled)]
    [TestCase(GeocodingSearchOutcome.Unavailable)]
    public async Task Handle_NonFoundOutcome_IsPassedThroughWithoutCoordinates(GeocodingSearchOutcome outcome)
    {
        _geocodingService.SearchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResult(outcome, null, null, null));

        var result = await _handler.Handle(new SearchGeocodingQuery("Zürich", null), CancellationToken.None);

        result.Status.ShouldBe(outcome);
        result.Latitude.ShouldBeNull();
        result.Longitude.ShouldBeNull();
        result.DisplayName.ShouldBeNull();
    }

    [Test]
    public async Task Serialized_Found_MatchesUiContract()
    {
        _geocodingService.SearchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResult(GeocodingSearchOutcome.Found, 47.37, 8.54, "Zürich, Schweiz"));

        var result = await _handler.Handle(new SearchGeocodingQuery("Zürich", "de"), CancellationToken.None);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJsonOptions));
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).ShouldBe(["status", "latitude", "longitude", "displayName"]);
        root.GetProperty("status").GetString().ShouldBe("Found");
        root.GetProperty("latitude").GetDouble().ShouldBe(47.37);
        root.GetProperty("longitude").GetDouble().ShouldBe(8.54);
        root.GetProperty("displayName").GetString().ShouldBe("Zürich, Schweiz");
    }

    [Test]
    public async Task Serialized_Disabled_KeepsNullFields()
    {
        _geocodingService.SearchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResult(GeocodingSearchOutcome.Disabled, null, null, null));

        var result = await _handler.Handle(new SearchGeocodingQuery("Zürich", null), CancellationToken.None);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJsonOptions));
        var root = document.RootElement;

        root.GetProperty("status").GetString().ShouldBe("Disabled");
        root.GetProperty("latitude").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("longitude").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("displayName").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}

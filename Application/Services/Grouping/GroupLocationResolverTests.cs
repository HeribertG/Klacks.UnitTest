// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupLocationResolver: a confident place is geocoded and persisted; a non-place, a
/// low-confidence verdict, a failed geocode, an already-located group and a missing group all leave the
/// group untouched (no write). The conservative gate matters because a wrong coordinate would feed a
/// wrong customer assignment downstream.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Interfaces.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupLocationResolverTests
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private IGroupRepository _groupRepository = null!;
    private IGroupPlaceClassifier _classifier = null!;
    private IGroupGeocoder _geocoder = null!;
    private GroupLocationResolver _resolver = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _classifier = Substitute.For<IGroupPlaceClassifier>();
        _geocoder = Substitute.For<IGroupGeocoder>();
        _resolver = new GroupLocationResolver(_groupRepository, _classifier, _geocoder);
        _groupRepository.GetPath(GroupId).Returns(new List<Group>());
        _groupRepository.SetCoordinatesAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private void HaveGroup(string name, double? lat = null, double? lon = null)
    {
        _groupRepository.Get(GroupId).Returns(new Group { Id = GroupId, Name = name, Latitude = lat, Longitude = lon });
    }

    [Test]
    public async Task Resolve_ConfidentPlace_GeocodesAndPersists()
    {
        HaveGroup("Biel/Bienne");
        _classifier.ClassifyAsync("Biel/Bienne", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GroupPlaceClassification(true, "Biel/Bienne", "BE", 0.95));
        _geocoder.GeocodeAsync("Biel/Bienne", Arg.Any<CancellationToken>())
            .Returns(((double?)47.13, (double?)7.25));

        var result = await _resolver.ResolveAsync(GroupId);

        result.Outcome.ShouldBe(GroupLocationResolveOutcome.Resolved);
        result.Latitude.ShouldBe(47.13);
        await _groupRepository.Received(1).SetCoordinatesAsync(GroupId, 47.13, 7.25, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolve_NotAPlace_LeavesUntouched()
    {
        HaveGroup("Pflege Level 3");
        _classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(GroupPlaceClassification.NotAPlace);

        var result = await _resolver.ResolveAsync(GroupId);

        result.Outcome.ShouldBe(GroupLocationResolveOutcome.NotAPlace);
        await _geocoder.DidNotReceive().GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _groupRepository.DidNotReceive().SetCoordinatesAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolve_LowConfidencePlace_LeavesUntouched()
    {
        HaveGroup("Bern-wöchentlich");
        _classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GroupPlaceClassification(true, "Bern", "BE", 0.6));

        var result = await _resolver.ResolveAsync(GroupId);

        result.Outcome.ShouldBe(GroupLocationResolveOutcome.NotAPlace);
        await _geocoder.DidNotReceive().GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _groupRepository.DidNotReceive().SetCoordinatesAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolve_GeocodeReturnsNothing_LeavesUntouched()
    {
        HaveGroup("Nirgendwo");
        _classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GroupPlaceClassification(true, "Nirgendwo", null, 0.9));
        _geocoder.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)null, (double?)null));

        var result = await _resolver.ResolveAsync(GroupId);

        result.Outcome.ShouldBe(GroupLocationResolveOutcome.GeocodeFailed);
        await _groupRepository.DidNotReceive().SetCoordinatesAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolve_AlreadyHasCoordinates_Skips()
    {
        HaveGroup("Zürich", 47.37, 8.54);

        var result = await _resolver.ResolveAsync(GroupId);

        result.Outcome.ShouldBe(GroupLocationResolveOutcome.AlreadySet);
        await _classifier.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolve_GroupNotFound_NoOp()
    {
        _groupRepository.Get(GroupId).Returns((Group?)null);

        var result = await _resolver.ResolveAsync(GroupId);

        result.Outcome.ShouldBe(GroupLocationResolveOutcome.NotFound);
        await _classifier.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}

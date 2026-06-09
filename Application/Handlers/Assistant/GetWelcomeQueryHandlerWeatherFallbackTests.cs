// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GetWelcomeQueryHandlerWeatherFallbackTests
{
    private ISuggestionsRanker _suggestionsRanker = null!;
    private IOpenMeteoClient _weatherClient = null!;
    private ICompanyLocationProvider _companyLocationProvider = null!;
    private GetWelcomeQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _suggestionsRanker = Substitute.For<ISuggestionsRanker>();
        _suggestionsRanker
            .RankAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new List<string>());
        _weatherClient = Substitute.For<IOpenMeteoClient>();
        _companyLocationProvider = Substitute.For<ICompanyLocationProvider>();
        _handler = new GetWelcomeQueryHandler(_suggestionsRanker, _weatherClient, _companyLocationProvider);
    }

    [Test]
    public async Task Handle_RequestHasBrowserCoordinates_UsesThemAndSkipsCompanyFallback()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(10.0, 20.0, Arg.Any<CancellationToken>()).Returns("weather.clear");

        var result = await _handler.Handle(request, CancellationToken.None);

        result.WeatherKey.ShouldBe("weather.clear");
        await _companyLocationProvider.DidNotReceive().GetCompanyLocationAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoBrowserCoordinates_FallsBackToCompanyLocation()
    {
        var request = BuildRequest(latitude: null, longitude: null);
        _companyLocationProvider.GetCompanyLocationAsync(Arg.Any<CancellationToken>()).Returns(((double Latitude, double Longitude)?)(47.0, 8.0));
        _weatherClient.GetWeatherKeyAsync(47.0, 8.0, Arg.Any<CancellationToken>()).Returns("weather.company");

        var result = await _handler.Handle(request, CancellationToken.None);

        result.WeatherKey.ShouldBe("weather.company");
    }

    [Test]
    public async Task Handle_NoBrowserCoordinatesAndNoCompanyLocation_LeavesWeatherKeyEmpty()
    {
        var request = BuildRequest(latitude: null, longitude: null);
        _companyLocationProvider.GetCompanyLocationAsync(Arg.Any<CancellationToken>()).Returns(((double Latitude, double Longitude)?)null);

        var result = await _handler.Handle(request, CancellationToken.None);

        result.WeatherKey.ShouldBe(string.Empty);
        await _weatherClient.DidNotReceive().GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    private static GetWelcomeQuery BuildRequest(double? latitude, double? longitude)
    {
        return new GetWelcomeQuery
        {
            Lang = "en",
            LocalHour = 9,
            Weekday = 1,
            IsReopen = false,
            Latitude = latitude,
            Longitude = longitude,
            UserId = Guid.NewGuid().ToString(),
        };
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GetWelcomeQueryHandlerWeatherFallbackTests
{
    private ISuggestionsRanker _suggestionsRanker = null!;
    private IOpenMeteoClient _weatherClient = null!;
    private ICompanyLocationProvider _companyLocationProvider = null!;
    private IOnboardingService _onboardingService = null!;
    private IPublicHolidayProvider _holidayProvider = null!;
    private IConfiguration _configuration = null!;
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
        _onboardingService = Substitute.For<IOnboardingService>();
        _holidayProvider = Substitute.For<IPublicHolidayProvider>();
        _holidayProvider.GetUpcomingHolidayAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UpcomingHoliday?)null);
        _configuration = new ConfigurationBuilder().Build();
        _handler = new GetWelcomeQueryHandler(_suggestionsRanker, _weatherClient, _companyLocationProvider, _onboardingService, _holidayProvider, _configuration);
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

    [Test]
    public async Task Handle_PopulatesOnboardingFromService()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");
        var onboarding = new OnboardingResource { ShouldOffer = true, ShowCard = true, Status = "pending" };
        _onboardingService.GetStateAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(onboarding);

        var result = await _handler.Handle(request, CancellationToken.None);

        result.Onboarding.ShouldNotBeNull();
        result.Onboarding!.ShouldOffer.ShouldBeTrue();
        result.Onboarding.Status.ShouldBe("pending");
    }

    [Test]
    public async Task Handle_HolidayTomorrow_SetsAmbientKeyAndName()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");
        _holidayProvider.GetUpcomingHolidayAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UpcomingHoliday("Auffahrt", IsToday: false));

        var result = await _handler.Handle(request, CancellationToken.None);

        result.AmbientKey.ShouldBe("klacksy.welcome.ambient.holiday_tomorrow");
        result.AmbientHolidayName.ShouldBe("Auffahrt");
    }

    [Test]
    public async Task Handle_NoHoliday_LeavesAmbientEmpty()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");

        var result = await _handler.Handle(request, CancellationToken.None);

        result.AmbientKey.ShouldBe(string.Empty);
        result.AmbientHolidayName.ShouldBe(string.Empty);
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

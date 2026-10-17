// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Which tour the welcome payload carries: the admin's setup tour while it is pending, in progress or snoozed (kind
/// "setup"); otherwise - non-admin, resolved setup tour, no setup marker - the caller's own intro tour (kind
/// "intro"), read for the caller's account id.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class GetWelcomeQueryHandlerOnboardingKindTests
{
    private const string CallerId = "caller-1";

    private IOnboardingService _onboardingService = null!;
    private IIntroTourStateService _introTourStateService = null!;
    private GetWelcomeQueryHandler _handler = null!;
    private readonly OnboardingResource _intro = new() { Kind = OnboardingKind.Intro, Status = IntroTourStatus.Pending, ShouldOffer = true };

    [SetUp]
    public void SetUp()
    {
        var ranker = Substitute.For<ISuggestionsRanker>();
        ranker.RankAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new List<string>());
        var holidays = Substitute.For<IPublicHolidayProvider>();
        holidays.GetUpcomingHolidayAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((UpcomingHoliday?)null);
        var countries = Substitute.For<ICountryResolver>();
        countries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns((Countries?)null);
        var focus = Substitute.For<IWelcomeFocusResolver>();
        focus.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((WelcomeFocusResource?)null);

        _onboardingService = Substitute.For<IOnboardingService>();
        _introTourStateService = Substitute.For<IIntroTourStateService>();
        _introTourStateService.GetStateAsync(CallerId, Arg.Any<CancellationToken>()).Returns(_intro);

        _handler = new GetWelcomeQueryHandler(
            ranker,
            Substitute.For<IOpenMeteoClient>(),
            Substitute.For<ICompanyLocationProvider>(),
            _onboardingService,
            _introTourStateService,
            holidays,
            Substitute.For<IGreetingComposer>(),
            new ConfigurationBuilder().Build(),
            focus,
            new FixedCompanyClock(new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc)),
            countries,
            NullLogger<GetWelcomeQueryHandler>.Instance);
    }

    private GetWelcomeQuery Request(params string[] rights) => new()
    {
        Lang = "en",
        LocalHour = 9,
        Weekday = 5,
        IsReopen = true,
        UserId = CallerId,
        UserRights = rights.ToList()
    };

    private void GivenSetup(string? status)
    {
        _onboardingService.GetStateAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(status == null ? null : new OnboardingResource { Kind = OnboardingKind.Setup, Status = status });
    }

    [TestCase(OnboardingStatus.Pending)]
    [TestCase(OnboardingStatus.InProgress)]
    [TestCase(OnboardingStatus.Snoozed)]
    public async Task AdminWithAnOpenSetupTour_GetsTheSetupKind(string status)
    {
        GivenSetup(status);

        var result = await _handler.Handle(Request(Roles.Admin), CancellationToken.None);

        result.Onboarding.ShouldNotBeNull();
        result.Onboarding!.Kind.ShouldBe(OnboardingKind.Setup);
        result.Onboarding.Status.ShouldBe(status);
        await _introTourStateService.DidNotReceiveWithAnyArgs().GetStateAsync(default!, default);
    }

    [TestCase(OnboardingStatus.Completed)]
    [TestCase(OnboardingStatus.Dismissed)]
    public async Task AdminWithAResolvedSetupTour_GetsTheIntroKind(string status)
    {
        GivenSetup(status);

        var result = await _handler.Handle(Request(Roles.Admin), CancellationToken.None);

        result.Onboarding.ShouldBeSameAs(_intro);
    }

    [Test]
    public async Task NonAdmin_GetsTheIntroKindForTheirOwnAccount()
    {
        GivenSetup(null);

        var result = await _handler.Handle(Request(Roles.Authorised), CancellationToken.None);

        result.Onboarding.ShouldBeSameAs(_intro);
        result.Onboarding!.Kind.ShouldBe(OnboardingKind.Intro);
        await _introTourStateService.Received(1).GetStateAsync(CallerId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACallerWithoutAnAccountId_GetsNoOnboarding()
    {
        GivenSetup(null);
        var request = Request();
        request.UserId = string.Empty;
        _introTourStateService.GetStateAsync(string.Empty, Arg.Any<CancellationToken>()).Returns((OnboardingResource?)null);

        var result = await _handler.Handle(request, CancellationToken.None);

        result.Onboarding.ShouldBeNull();
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Application.Services.Assistant;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

/// <summary>
/// The route signal of the welcome suggestions only fires when a RouteHints fragment is part of the route the
/// frontend really reports. These tests drive the ranker with real Angular routes, so a fragment pointing at a
/// page that does not exist (the former "/workplace/clients" and "/workplace/settings/llm") shows up as a
/// suggestion that never gets its route bonus. History is switched off with an empty user id.
/// </summary>
[TestFixture]
public class SuggestionsRankerRouteHintTests
{
    private const int MidWeekWeekday = 3;
    private const int MorningHour = 10;

    private SuggestionsRanker _ranker = null!;

    [SetUp]
    public void SetUp()
    {
        _ranker = new SuggestionsRanker(Substitute.For<ISkillUsageRepository>());
    }

    [Test]
    public async Task RankAsync_OnTheClientListRoute_LiftsThePersonSuggestionsAboveTheAdminDefaults()
    {
        var result = await _ranker.RankAsync(
            Guid.Empty, [Roles.Admin], "/workplace/client", MorningHour, MidWeekWeekday, 2);

        result.ShouldBe(
            [WelcomeI18nKeys.Suggestion.CreateEmployee, WelcomeI18nKeys.Suggestion.FindPerson],
            ignoreOrder: true);
    }

    [Test]
    public async Task RankAsync_OnTheSettingsRoute_LiftsManageProvidersNextToEditSettings()
    {
        var result = await _ranker.RankAsync(
            Guid.Empty, [], "/workplace/settings", MorningHour, MidWeekWeekday, 3);

        result.ShouldContain(WelcomeI18nKeys.Suggestion.ManageProviders);
        result.ShouldContain(WelcomeI18nKeys.Suggestion.EditSettings);
    }
}

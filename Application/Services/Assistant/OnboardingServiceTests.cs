// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class OnboardingServiceTests
{
    private ISettingsReader _settingsReader = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILLMRepository _llmRepository = null!;
    private OnboardingService _service = null!;

    private static readonly IReadOnlyList<string> AdminRights = new List<string> { Roles.Admin };
    private static readonly IReadOnlyList<string> UserRights = new List<string> { "User" };

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _llmRepository = Substitute.For<ILLMRepository>();
        _service = new OnboardingService(_settingsReader, _settingsRepository, _unitOfWork, _llmRepository);
    }

    [Test]
    public async Task GetStateAsync_NonAdmin_ReturnsNull()
    {
        SetState("pending");
        SetLlmLive(true);

        var result = await _service.GetStateAsync(UserRights);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetStateAsync_NoSettingRow_ReturnsNull()
    {
        _settingsReader.GetSetting(SettingsConstants.ONBOARDING_STATE).Returns((SettingsModel?)null);

        var result = await _service.GetStateAsync(AdminRights);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetStateAsync_PendingAndLlmLive_OffersAndShowsCard()
    {
        SetState("pending");
        SetLlmLive(true);

        var result = await _service.GetStateAsync(AdminRights);

        result.ShouldNotBeNull();
        result!.ShouldOffer.ShouldBeTrue();
        result.ShowCard.ShouldBeTrue();
        result.Status.ShouldBe(OnboardingStatus.Pending);
    }

    [Test]
    public async Task GetStateAsync_PendingButNoLlm_DoesNotOfferButShowsCard()
    {
        SetState("pending");
        SetLlmLive(false);

        var result = await _service.GetStateAsync(AdminRights);

        result.ShouldNotBeNull();
        result!.ShouldOffer.ShouldBeFalse();
        result.ShowCard.ShouldBeTrue();
    }

    [Test]
    public async Task GetStateAsync_Dismissed_HidesCardAndDoesNotOffer()
    {
        SetState(OnboardingStatus.Dismissed);
        SetLlmLive(true);

        var result = await _service.GetStateAsync(AdminRights);

        result.ShouldNotBeNull();
        result!.ShowCard.ShouldBeFalse();
        result.ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task GetStateAsync_JsonStateWithCompletedStations_IsParsed()
    {
        SetState("{\"status\":\"in_progress\",\"completedStations\":[\"title\",\"address\"]}");
        SetLlmLive(true);

        var result = await _service.GetStateAsync(AdminRights);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(OnboardingStatus.InProgress);
        result.CompletedStations.ShouldBe(new[] { "title", "address" });
        result.ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task UpdateStateAsync_NonAdmin_ReturnsNullAndDoesNotPersist()
    {
        SetState("pending");

        var result = await _service.UpdateStateAsync(OnboardingStatus.Dismissed, null, UserRights);

        result.ShouldBeNull();
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<SettingsModel>());
    }

    [Test]
    public async Task UpdateStateAsync_NoRow_ReturnsNullAndDoesNotActivate()
    {
        _settingsReader.GetSetting(SettingsConstants.ONBOARDING_STATE).Returns((SettingsModel?)null);

        var result = await _service.UpdateStateAsync(OnboardingStatus.InProgress, null, AdminRights);

        result.ShouldBeNull();
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<SettingsModel>());
    }

    [Test]
    public async Task UpdateStateAsync_SetsStatus_PersistsJsonAndCommits()
    {
        SetState("pending");
        SetLlmLive(true);

        var result = await _service.UpdateStateAsync(OnboardingStatus.Snoozed, null, AdminRights);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(OnboardingStatus.Snoozed);
        await _settingsRepository.Received(1).PutSetting(Arg.Is<SettingsModel>(s => s.Value.Contains(OnboardingStatus.Snoozed)));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task UpdateStateAsync_MarksStationCompleted_WithoutDuplicates()
    {
        SetState("{\"status\":\"in_progress\",\"completedStations\":[\"title\"]}");
        SetLlmLive(true);

        var result = await _service.UpdateStateAsync(null, "title", AdminRights);

        result.ShouldNotBeNull();
        result!.CompletedStations.ShouldBe(new[] { "title" });
        await _settingsRepository.Received(1).PutSetting(Arg.Is<SettingsModel>(s => s.Value.Contains("title")));
    }

    private void SetState(string value)
    {
        _settingsReader.GetSetting(SettingsConstants.ONBOARDING_STATE)
            .Returns(new SettingsModel { Type = SettingsConstants.ONBOARDING_STATE, Value = value });
    }

    private void SetLlmLive(bool live)
    {
        var providers = live
            ? new List<LLMProvider> { new() { ProviderId = "openai", IsEnabled = true, ApiKey = "sk-test" } }
            : new List<LLMProvider>();
        _llmRepository.GetProvidersAsync().Returns(providers);
    }
}

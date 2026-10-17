// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Routing of the tour save: kind "intro" writes the caller's own intro state for any role; kind "setup" - also an
/// empty kind sent by an older frontend - stays admin-only (a non-admin gets null, which the controller turns into
/// 403) and, when the admin completes or dismisses it, closes that admin's intro; an unknown kind is rejected.
/// Runs over the real OnboardingService so the admin gate is the production one.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class SaveOnboardingStateCommandHandlerTests
{
    private const string CallerId = "caller-1";

    private ISettingsReader _settingsReader = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IIntroTourStateService _introTourStateService = null!;
    private SaveOnboardingStateCommandHandler _handler = null!;
    private readonly OnboardingResource _intro = new() { Kind = OnboardingKind.Intro, Status = IntroTourStatus.InProgress };

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(SettingsConstants.ONBOARDING_STATE)
            .Returns(new SettingsModel { Type = SettingsConstants.ONBOARDING_STATE, Value = OnboardingStatus.InProgress });
        _settingsRepository = Substitute.For<ISettingsRepository>();
        var llmRepository = Substitute.For<ILLMRepository>();
        llmRepository.GetProvidersAsync().Returns(new List<LLMProvider>());
        var onboardingService = new OnboardingService(_settingsReader, _settingsRepository, Substitute.For<IUnitOfWork>(), llmRepository);

        _introTourStateService = Substitute.For<IIntroTourStateService>();
        _introTourStateService
            .ApplyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_intro);

        _handler = new SaveOnboardingStateCommandHandler(onboardingService, _introTourStateService);
    }

    private static SaveOnboardingStateCommand Command(string? kind, string? status, params string[] rights) => new()
    {
        Kind = kind,
        Status = status,
        UserId = CallerId,
        UserRights = rights.ToList()
    };

    [TestCase(Roles.User)]
    [TestCase(Roles.Authorised)]
    [TestCase(Roles.Admin)]
    public async Task IntroKind_WritesTheCallersOwnState_ForEveryRole(string role)
    {
        var result = await _handler.Handle(Command("intro", IntroTourStatus.InProgress, role), CancellationToken.None);

        result.ShouldBeSameAs(_intro);
        await _introTourStateService.Received(1).ApplyAsync(CallerId, IntroTourStatus.InProgress, null, Arg.Any<CancellationToken>());
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task IntroKind_WithoutAnyRole_IsAllowed()
    {
        var result = await _handler.Handle(Command(" Intro ", IntroTourDefaults.OfferedEvent), CancellationToken.None);

        result.ShouldBeSameAs(_intro);
    }

    [TestCase("setup")]
    [TestCase(null)]
    [TestCase("")]
    public async Task SetupKind_AsNonAdmin_IsRefused_AndWritesNothing(string? kind)
    {
        var result = await _handler.Handle(Command(kind, OnboardingStatus.Completed, Roles.Authorised), CancellationToken.None);

        result.ShouldBeNull();
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
        await _introTourStateService.DidNotReceiveWithAnyArgs().CloseAfterSetupAsync(default!, default);
        await _introTourStateService.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default, default, default);
    }

    [TestCase(OnboardingStatus.Completed)]
    [TestCase(OnboardingStatus.Dismissed)]
    public async Task SetupResolvedByTheAdmin_ClosesThatAdminsIntro(string status)
    {
        var result = await _handler.Handle(Command("setup", status, Roles.Admin), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(OnboardingKind.Setup);
        result.Status.ShouldBe(status);
        await _introTourStateService.Received(1).CloseAfterSetupAsync(CallerId, Arg.Any<CancellationToken>());
    }

    [TestCase(OnboardingStatus.InProgress)]
    [TestCase(OnboardingStatus.Snoozed)]
    [TestCase(null)]
    public async Task SetupNotResolved_LeavesTheIntroAlone(string? status)
    {
        var result = await _handler.Handle(Command(null, status, Roles.Admin), CancellationToken.None);

        result.ShouldNotBeNull();
        await _introTourStateService.DidNotReceiveWithAnyArgs().CloseAfterSetupAsync(default!, default);
    }

    [Test]
    public async Task AnUnknownKind_IsRejected()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(Command("tour", OnboardingStatus.Completed, Roles.Admin), CancellationToken.None));

        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }
}

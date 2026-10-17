// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Status model of the per-account intro tour: an account without a row is offered without any write; a reported
/// "offered" stamps the time, keeps a new account offerable and turns the single legacy offer into legacy_offered
/// for good; "later" counts up and the second one dismisses (on a legacy offer it ends the offer); "in_progress"
/// from a final state restarts with no finished stations, otherwise it resumes; stations are added once; unknown
/// statuses and malformed station ids are rejected without a write; closing after the setup tour completes the
/// intro but leaves a final state alone.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class IntroTourStateServiceTests
{
    private const string UserId = "user-1";
    private static readonly DateTime Now = new(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);

    private IUserIntroTourStateRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILLMRepository _llmRepository = null!;
    private IntroTourStateService _sut = null!;
    private UserIntroTourState? _row;

    [SetUp]
    public void SetUp()
    {
        _row = null;
        _repository = Substitute.For<IUserIntroTourStateRepository>();
        _repository.GetNoTrackingAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => _row);
        _repository.GetForUpdateAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => _row);
        _repository
            .When(r => r.EnsureExistsAsync(UserId, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()))
            .Do(call => _row ??= new UserIntroTourState
            {
                UserId = UserId,
                Status = call.ArgAt<string>(1),
                CreateTime = call.ArgAt<DateTime>(2)
            });
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _llmRepository = Substitute.For<ILLMRepository>();
        _llmRepository.GetProvidersAsync().Returns(new List<LLMProvider>());
        _sut = new IntroTourStateService(_repository, _unitOfWork, _llmRepository, new SettableTimeProvider(Now));
    }

    private void GivenRow(string status, int snoozeCount = 0, params string[] stations)
    {
        _row = new UserIntroTourState
        {
            UserId = UserId,
            Status = status,
            SnoozeCount = snoozeCount,
            CompletedStations = stations.ToList(),
            CreateTime = Now.AddDays(-3)
        };
    }

    [Test]
    public async Task GetState_WithoutARow_OffersAsPending_AndWritesNothing()
    {
        var result = await _sut.GetStateAsync(UserId);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(OnboardingKind.Intro);
        result.Status.ShouldBe(IntroTourStatus.Pending);
        result.ShouldOffer.ShouldBeTrue();
        result.ShowCard.ShouldBeFalse();
        result.IsLegacyOffer.ShouldBeFalse();
        result.CompletedStations.ShouldBeEmpty();
        await _repository.DidNotReceiveWithAnyArgs().EnsureExistsAsync(default!, default!, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task GetState_WithoutAUserId_ReturnsNull()
    {
        (await _sut.GetStateAsync(string.Empty)).ShouldBeNull();
    }

    [Test]
    public async Task GetState_LegacyOffer_IsOfferedOnce_WithTheLegacyFlag()
    {
        GivenRow(IntroTourStatus.LegacyOffer);

        var result = await _sut.GetStateAsync(UserId);

        result!.ShouldOffer.ShouldBeTrue();
        result.IsLegacyOffer.ShouldBeTrue();
        result.ShowCard.ShouldBeFalse();
    }

    [TestCase(IntroTourStatus.LegacyOffered)]
    [TestCase(IntroTourStatus.Completed)]
    [TestCase(IntroTourStatus.Dismissed)]
    [TestCase(IntroTourStatus.InProgress)]
    public async Task GetState_OtherStates_AreNotOffered(string status)
    {
        GivenRow(status);

        var result = await _sut.GetStateAsync(UserId);

        result!.ShouldOffer.ShouldBeFalse();
        result.ShowCard.ShouldBe(status == IntroTourStatus.InProgress);
    }

    [Test]
    public async Task GetState_Snoozed_IsOfferedAgain()
    {
        GivenRow(IntroTourStatus.Snoozed, snoozeCount: 1);

        var result = await _sut.GetStateAsync(UserId);

        result!.ShouldOffer.ShouldBeTrue();
        result.SnoozeCount.ShouldBe(1);
    }

    [Test]
    public async Task Offered_OnANewAccount_StampsTheTime_AndKeepsItOfferable()
    {
        var result = await _sut.ApplyAsync(UserId, IntroTourDefaults.OfferedEvent, null);

        result.Status.ShouldBe(IntroTourStatus.Pending);
        result.ShouldOffer.ShouldBeTrue();
        result.LastOfferedAt.ShouldBe(Now);
        _row!.LastOfferedAt.ShouldBe(Now);
        _row.UpdateTime.ShouldBe(Now);
        await _repository.Received(1).EnsureExistsAsync(UserId, IntroTourStatus.Pending, Now, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Offered_OnALegacyOffer_EndsTheSingleOffer()
    {
        GivenRow(IntroTourStatus.LegacyOffer);

        var result = await _sut.ApplyAsync(UserId, IntroTourDefaults.OfferedEvent, null);

        result.Status.ShouldBe(IntroTourStatus.LegacyOffered);
        result.ShouldOffer.ShouldBeFalse();
        result.LastOfferedAt.ShouldBe(Now);
    }

    [Test]
    public async Task Offered_OnASnoozedAccount_KeepsTheSnoozeAndItsCount()
    {
        GivenRow(IntroTourStatus.Snoozed, snoozeCount: 1);

        var result = await _sut.ApplyAsync(UserId, IntroTourDefaults.OfferedEvent, null);

        result.Status.ShouldBe(IntroTourStatus.Snoozed);
        result.SnoozeCount.ShouldBe(1);
    }

    [Test]
    public async Task FirstLater_Snoozes_SecondLater_Dismisses()
    {
        var first = await _sut.ApplyAsync(UserId, IntroTourStatus.Snoozed, null);

        first.Status.ShouldBe(IntroTourStatus.Snoozed);
        first.SnoozeCount.ShouldBe(1);
        first.ShouldOffer.ShouldBeTrue();

        var second = await _sut.ApplyAsync(UserId, IntroTourStatus.Snoozed, null);

        second.Status.ShouldBe(IntroTourStatus.Dismissed);
        second.SnoozeCount.ShouldBe(IntroTourDefaults.SnoozeLimit);
        second.ShouldOffer.ShouldBeFalse();
    }

    [TestCase(IntroTourStatus.LegacyOffer)]
    [TestCase(IntroTourStatus.LegacyOffered)]
    public async Task Later_OnALegacyOffer_EndsTheOffer_WithoutCounting(string status)
    {
        GivenRow(status);

        var result = await _sut.ApplyAsync(UserId, IntroTourStatus.Snoozed, null);

        result.Status.ShouldBe(IntroTourStatus.LegacyOffered);
        result.SnoozeCount.ShouldBe(0);
        result.ShouldOffer.ShouldBeFalse();
    }

    [TestCase(IntroTourStatus.Completed)]
    [TestCase(IntroTourStatus.Dismissed)]
    public async Task Later_OnAFinalState_ChangesNothing(string status)
    {
        GivenRow(status);

        var result = await _sut.ApplyAsync(UserId, IntroTourStatus.Snoozed, null);

        result.Status.ShouldBe(status);
        result.SnoozeCount.ShouldBe(0);
    }

    [Test]
    public async Task AToastTimeout_ReportsNothing_SoTheNextWelcomeOffersAgainWithoutASnooze()
    {
        await _sut.ApplyAsync(UserId, IntroTourDefaults.OfferedEvent, null);

        var next = await _sut.GetStateAsync(UserId);

        next!.ShouldOffer.ShouldBeTrue();
        next.SnoozeCount.ShouldBe(0);
        next.Status.ShouldBe(IntroTourStatus.Pending);
    }

    [Test]
    public async Task Yes_StartsTheTour_AndShowsTheCard()
    {
        var result = await _sut.ApplyAsync(UserId, IntroTourStatus.InProgress, null);

        result.Status.ShouldBe(IntroTourStatus.InProgress);
        result.ShowCard.ShouldBeTrue();
        result.ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task InProgress_WhileRunning_ResumesAndKeepsTheStations()
    {
        GivenRow(IntroTourStatus.InProgress, 0, "schedule", "shifts");

        var result = await _sut.ApplyAsync(UserId, IntroTourStatus.InProgress, null);

        result.CompletedStations.ShouldBe(new[] { "schedule", "shifts" });
    }

    [TestCase(IntroTourStatus.Completed)]
    [TestCase(IntroTourStatus.Dismissed)]
    [TestCase(IntroTourStatus.LegacyOffered)]
    public async Task InProgress_FromAFinalState_RestartsFromTheFirstStation(string status)
    {
        GivenRow(status, 2, "schedule", "shifts");

        var result = await _sut.ApplyAsync(UserId, IntroTourStatus.InProgress, null);

        result.Status.ShouldBe(IntroTourStatus.InProgress);
        result.CompletedStations.ShouldBeEmpty();
        result.SnoozeCount.ShouldBe(0);
    }

    [Test]
    public async Task InProgress_FromALegacyOffer_StartsTheTour()
    {
        GivenRow(IntroTourStatus.LegacyOffer);

        var result = await _sut.ApplyAsync(UserId, IntroTourStatus.InProgress, null);

        result.Status.ShouldBe(IntroTourStatus.InProgress);
        result.IsLegacyOffer.ShouldBeFalse();
    }

    [TestCase(IntroTourStatus.Completed)]
    [TestCase(IntroTourStatus.Dismissed)]
    public async Task CompletedAndDismissed_AreStoredAsReported(string status)
    {
        GivenRow(IntroTourStatus.InProgress);

        var result = await _sut.ApplyAsync(UserId, status, null);

        result.Status.ShouldBe(status);
        result.ShouldOffer.ShouldBeFalse();
        result.ShowCard.ShouldBeFalse();
    }

    [Test]
    public async Task AStation_IsAddedOnce()
    {
        GivenRow(IntroTourStatus.InProgress);

        await _sut.ApplyAsync(UserId, null, "schedule");
        var result = await _sut.ApplyAsync(UserId, null, " schedule ");

        result.CompletedStations.ShouldBe(new[] { "schedule" });
        result.Status.ShouldBe(IntroTourStatus.InProgress);
    }

    [Test]
    public async Task StatusAndStation_InOneCall_AreBothApplied()
    {
        GivenRow(IntroTourStatus.InProgress, 0, "schedule");

        var result = await _sut.ApplyAsync(UserId, IntroTourStatus.Completed, "klacksy");

        result.Status.ShouldBe(IntroTourStatus.Completed);
        result.CompletedStations.ShouldBe(new[] { "schedule", "klacksy" });
    }

    [Test]
    public async Task NeitherStatusNorStation_WritesNothing()
    {
        GivenRow(IntroTourStatus.Snoozed, 1);

        var result = await _sut.ApplyAsync(UserId, " ", null);

        result.Status.ShouldBe(IntroTourStatus.Snoozed);
        await _repository.DidNotReceiveWithAnyArgs().EnsureExistsAsync(default!, default!, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase("pending")]
    [TestCase("legacy_offer")]
    [TestCase("legacy_offered")]
    [TestCase("started")]
    public async Task AnUnreportableStatus_IsRejected_WithoutAWrite(string status)
    {
        await Should.ThrowAsync<InvalidRequestException>(() => _sut.ApplyAsync(UserId, status, null));

        await _repository.DidNotReceiveWithAnyArgs().EnsureExistsAsync(default!, default!, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase("schedule; drop table")]
    [TestCase("a/b")]
    public async Task AMalformedStation_IsRejected_WithoutAWrite(string station)
    {
        await Should.ThrowAsync<InvalidRequestException>(() => _sut.ApplyAsync(UserId, null, station));

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ATooLongStation_IsRejected()
    {
        var station = new string('s', IntroTourDefaults.MaxStationIdLength + 1);

        await Should.ThrowAsync<InvalidRequestException>(() => _sut.ApplyAsync(UserId, null, station));
    }

    [Test]
    public async Task AFullStationList_RejectsANewStation()
    {
        GivenRow(IntroTourStatus.InProgress, 0,
            Enumerable.Range(0, IntroTourDefaults.MaxCompletedStations).Select(i => $"s{i}").ToArray());

        await Should.ThrowAsync<InvalidRequestException>(() => _sut.ApplyAsync(UserId, null, "one-more"));
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ApplyWithoutAUserId_IsRejected()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => _sut.ApplyAsync(string.Empty, IntroTourStatus.InProgress, null));
    }

    [TestCase(IntroTourStatus.LegacyOffer)]
    [TestCase(IntroTourStatus.Snoozed)]
    [TestCase(IntroTourStatus.InProgress)]
    public async Task CloseAfterSetup_CompletesAnOpenIntro(string status)
    {
        GivenRow(status);

        await _sut.CloseAfterSetupAsync(UserId);

        _row!.Status.ShouldBe(IntroTourStatus.Completed);
        _row.UpdateTime.ShouldBe(Now);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task CloseAfterSetup_WithoutARow_CreatesItCompleted()
    {
        await _sut.CloseAfterSetupAsync(UserId);

        _row.ShouldNotBeNull();
        _row!.Status.ShouldBe(IntroTourStatus.Completed);
        (await _sut.GetStateAsync(UserId))!.ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task CloseAfterSetup_LeavesADismissedIntroAlone()
    {
        GivenRow(IntroTourStatus.Dismissed);

        await _sut.CloseAfterSetupAsync(UserId);

        _row!.Status.ShouldBe(IntroTourStatus.Dismissed);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task LlmLive_FollowsTheProviders()
    {
        _llmRepository.GetProvidersAsync().Returns(new List<LLMProvider>
        {
            new() { ProviderId = "ollama", IsEnabled = true, RequiresApiKey = false }
        });

        (await _sut.GetStateAsync(UserId))!.LlmLive.ShouldBeTrue();
    }
}

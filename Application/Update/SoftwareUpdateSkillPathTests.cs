// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Runs the real preflight, trigger handler, preview provider and advice handler against one substituted queue and
/// manifest, so the three paths are compared by their results: in every situation the advice's CanStart equals what the
/// trigger does and the preview issues a token exactly then. The third-party case pins the trap that the update status
/// hides a Whisper operation the trigger still refuses on. Also pins the version binding: a manifest that moved on after
/// the confirmation queues nothing, while the card's call without an expected version keeps taking what is offered.
/// </summary>

using Klacks.Api.Application.Commands.Update;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Handlers.Update;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Queries.Update;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Update;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Update;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Klacks.UnitTest.Application.Update;

[TestFixture]
public class SoftwareUpdateSkillPathTests
{
    private const string Latest = "9.9.9";
    private const string Reachable = "1.0.0";
    private const string Unreachable = "9.0.0";
    private const string OldRelease = "0.0.1";
    private const string CallerId = "caller";

    private IUpdateHistoryRepository _repository = null!;
    private IUpdateManifestReader _manifestReader = null!;
    private IConnectedUserIdReader _connectedUsers = null!;
    private IAutofillRunPresenceTracker _autofillRuns = null!;
    private UpdateTriggerPreflight _preflight = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IUpdateHistoryRepository>();
        _manifestReader = Substitute.For<IUpdateManifestReader>();
        _connectedUsers = Substitute.For<IConnectedUserIdReader>();
        _autofillRuns = Substitute.For<IAutofillRunPresenceTracker>();
        _autofillRuns.GetActive().Returns([]);
        _connectedUsers.GetConnectedUserIdsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var installation = Substitute.For<IInstallationIdentityProvider>();
        installation.Get().Returns(new InstallationIdentity(InstallationKind.Docker, "linux-x64"));
        _preflight = new UpdateTriggerPreflight(
            _repository, _manifestReader, new UpdateAvailabilityEvaluator(), Substitute.For<ISettingsReader>(), installation);
    }

    private void GivenManifest(string latest, string minUpgradableFrom, bool hasArtifact = true) =>
        _manifestReader.GetManifestAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateManifest
            {
                Channel = UpdateChannel.Stable,
                LatestVersion = latest,
                MinUpgradableFrom = minUpgradableFrom,
                Artifacts = hasArtifact
                    ? new UpdateArtifacts { Docker = new DockerArtifact { ApiImage = "ghcr.io/x/klacks-api:" + latest, Sha256 = "sha256:abc" } }
                    : new UpdateArtifacts(),
                Signature = "sig",
            });

    private void GivenActive(UpdateOperationType type) =>
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(new UpdateHistory
            {
                Id = Guid.NewGuid(),
                OperationType = type,
                Status = UpdateOperationStatus.Pending,
                RequestedAt = DateTime.UtcNow,
            });

    private TriggerUpdateCommandHandler Trigger() => new(_repository, _preflight);

    private EvaluateSoftwareUpdateQueryHandler Advice() => new(
        _preflight,
        _repository,
        Options.Create(new UpdateTrustOptions { ManifestBaseUrl = "https://releases.example" }),
        _autofillRuns,
        new WizardJobRegistry(),
        new HarmonizerJobRegistry(),
        new HolisticHarmonizerJobRegistry(),
        _connectedUsers,
        TimeProvider.System);

    private async Task<(bool Enqueued, bool CanStart, bool PreviewAllowed)> RunAllPathsAsync()
    {
        var evaluation = await Advice().Handle(new EvaluateSoftwareUpdateQuery(CallerId), CancellationToken.None);
        var preview = await new StartSoftwareUpdateConfirmationPreviewProvider(_preflight).BuildAsync(
            StartSoftwareUpdateSkill.SkillName, null!, [], CancellationToken.None);
        var trigger = await Trigger().Handle(new TriggerUpdateCommand(CallerId), CancellationToken.None);
        return (trigger.Enqueued, evaluation.CanStart, !preview.IsRefusal);
    }

    [Test]
    public async Task UpdateOffered_AllThreePathsAgreeItCanStart()
    {
        GivenManifest(Latest, Reachable);

        var (enqueued, canStart, previewAllowed) = await RunAllPathsAsync();

        enqueued.ShouldBeTrue();
        canStart.ShouldBeTrue();
        previewAllowed.ShouldBeTrue();
    }

    [Test]
    public async Task NoManifest_AllThreePathsRefuse()
    {
        var (enqueued, canStart, previewAllowed) = await RunAllPathsAsync();

        enqueued.ShouldBeFalse();
        canStart.ShouldBeFalse();
        previewAllowed.ShouldBeFalse();
    }

    [TestCase(Latest, Unreachable, true, TestName = "IntermediateRequired_AllThreePathsRefuse")]
    [TestCase(OldRelease, Reachable, true, TestName = "NothingNewer_AllThreePathsRefuse")]
    [TestCase(Latest, Reachable, false, TestName = "NoArtifactForThisInstallation_AllThreePathsRefuse")]
    public async Task ManifestRefusal_AllThreePathsAgree(string latest, string minUpgradableFrom, bool hasArtifact)
    {
        GivenManifest(latest, minUpgradableFrom, hasArtifact);

        var (enqueued, canStart, previewAllowed) = await RunAllPathsAsync();

        enqueued.ShouldBeFalse();
        canStart.ShouldBeFalse();
        previewAllowed.ShouldBeFalse();
    }

    [TestCase(UpdateOperationType.Update)]
    [TestCase(UpdateOperationType.WhisperInstall)]
    public async Task ActiveOperationOfAnyType_AllThreePathsRefuse(UpdateOperationType type)
    {
        GivenManifest(Latest, Reachable);
        GivenActive(type);

        var (enqueued, canStart, previewAllowed) = await RunAllPathsAsync();

        enqueued.ShouldBeFalse();
        canStart.ShouldBeFalse();
        previewAllowed.ShouldBeFalse();
    }

    [Test]
    public async Task Preview_BindsTheVersionTheTriggerWouldTake()
    {
        GivenManifest(Latest, Reachable);

        var preview = await new StartSoftwareUpdateConfirmationPreviewProvider(_preflight).BuildAsync(
            StartSoftwareUpdateSkill.SkillName, null!, [], CancellationToken.None);

        preview.Binding.ShouldNotBeNull();
        preview.Binding!.Matches(StartSoftwareUpdateSkill.BindingFor(Latest)).ShouldBeTrue();
        preview.Text.ShouldContain(Latest);
    }

    [Test]
    public async Task ManifestMovedOnAfterTheConfirmation_QueuesNothing()
    {
        GivenManifest(Latest, Reachable);

        var result = await Trigger().Handle(new TriggerUpdateCommand(CallerId, "9.9.8"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(UpdateReasons.TargetVersionChanged);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task ConfirmedVersionStillOffered_IsQueuedWithTheCallerAsRequester()
    {
        GivenManifest(Latest, Reachable);

        var result = await Trigger().Handle(new TriggerUpdateCommand(CallerId, Latest), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<UpdateHistory>(entry => entry.TargetVersion == Latest && entry.RequestedBy == CallerId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Advice_CountsOtherConnectedUsersAndLeavesTheCallerOut()
    {
        GivenManifest(Latest, Reachable);
        _connectedUsers.GetConnectedUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { CallerId.ToUpperInvariant(), "someone-else" });

        var evaluation = await Advice().Handle(new EvaluateSoftwareUpdateQuery(CallerId), CancellationToken.None);

        evaluation.OtherConnectedUsers.ShouldBe(1);
    }

    [Test]
    public async Task Advice_CountsAutofillRunsOfEveryGroup()
    {
        GivenManifest(Latest, Reachable);
        _autofillRuns.GetActive().Returns(
        [
            new AutofillRunPresenceDto(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), null),
            new AutofillRunPresenceDto(Guid.NewGuid(), null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), null),
        ]);

        var evaluation = await Advice().Handle(new EvaluateSoftwareUpdateQuery(CallerId), CancellationToken.None);

        evaluation.RunningWork.AutofillRuns.ShouldBe(2);
        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartWithCaution);
    }

    [Test]
    public async Task Advice_NeverWritesToTheQueue()
    {
        GivenManifest(Latest, Reachable);

        await Advice().Handle(new EvaluateSoftwareUpdateQuery(CallerId), CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }
}

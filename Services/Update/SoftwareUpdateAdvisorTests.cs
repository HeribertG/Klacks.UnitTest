// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins every rule of SoftwareUpdateAdvisor: each rejection of the trigger maps to its own finding and verdict, a queue
/// row waiting too long is reported as a fact without claiming the updater is down, a failed last attempt counts only for
/// the offered version, a failed return to the previous version advises against a start the trigger would accept, and the
/// cautions appear only when the update could actually start.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Services.Update;
using Klacks.Api.Domain.Models.Update;

namespace Klacks.UnitTest.Services.Update;

[TestFixture]
public class SoftwareUpdateAdvisorTests
{
    private const string Latest = "1.0.53";

    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SoftwareUpdateRunningWork Idle = new(0, 0, 0, 0);

    private static UpdateTriggerPreflightResult Preflight(
        string? rejection = null,
        UpdateManifest? manifest = null,
        UpdateAvailabilityStatus status = UpdateAvailabilityStatus.UpdateAvailable,
        UpdateHistory? active = null,
        UpdateChannel channel = UpdateChannel.Stable) =>
        new(
            new SemanticVersion(1, 0, 52),
            channel,
            new InstallationIdentity(InstallationKind.Docker, "linux-x64"),
            manifest,
            manifest is null ? null : new UpdateAvailability { Status = status, LatestVersion = manifest.LatestVersion },
            null,
            active,
            rejection);

    private static UpdateManifest Manifest(bool containsMigrations = false) =>
        new() { LatestVersion = Latest, ContainsMigrations = containsMigrations };

    private static SoftwareUpdateFacts Facts(
        UpdateTriggerPreflightResult preflight,
        UpdateHistory? last = null,
        SoftwareUpdateRunningWork? work = null,
        int others = 0,
        bool sourceConfigured = true) =>
        new(preflight, sourceConfigured, last, work ?? Idle, others, Now);

    private static UpdateHistory Operation(
        UpdateOperationStatus status,
        UpdateOperationType type = UpdateOperationType.Update,
        string target = Latest,
        DateTime? requestedAt = null,
        DateTime? startedAt = null) =>
        new()
        {
            OperationType = type,
            Status = status,
            TargetVersion = target,
            RequestedAt = requestedAt ?? Now,
            StartedAt = startedAt,
        };

    private static IEnumerable<SoftwareUpdateFindingCode> Codes(SoftwareUpdateEvaluation evaluation) =>
        evaluation.Findings.Select(finding => finding.Code);

    [Test]
    public void NothingAgainstIt_AdvisesStartingAndStatesTheRestart()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(manifest: Manifest())));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartNow);
        evaluation.CanStart.ShouldBeTrue();
        evaluation.LatestVersion.ShouldBe(Latest);
        Codes(evaluation).ShouldBe([SoftwareUpdateFindingCode.UpdateAvailable]);
        evaluation.Recommendation.ShouldContain(SoftwareUpdateNotices.Restart);
    }

    [Test]
    public void EveryEvaluation_NamesWhatItCannotSee()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(manifest: Manifest())));

        evaluation.NotMeasured.ShouldBe(SoftwareUpdateAdvisor.NotMeasured);
        evaluation.NotMeasured.ShouldContain("updater service");
    }

    [Test]
    public void NoAddressConfigured_IsAnInstallationWithoutUpdates()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(
            Facts(Preflight(UpdateReasons.NoManifest), sourceConfigured: false));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.CannotStart);
        Codes(evaluation).ShouldBe([SoftwareUpdateFindingCode.UpdateSourceNotConfigured]);
        evaluation.LatestVersion.ShouldBeNull();
    }

    [Test]
    public void AddressConfiguredButUnreadable_IsNotCalledConfigurationProblem()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(UpdateReasons.NoManifest)));

        Codes(evaluation).ShouldBe([SoftwareUpdateFindingCode.ManifestUnavailable]);
    }

    [Test]
    public void AlreadyCurrent_HasNothingToInstallAndIsNoBlocker()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.UpToDate, Manifest(), UpdateAvailabilityStatus.UpToDate)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.NothingToInstall);
        evaluation.CanStart.ShouldBeFalse();
        evaluation.Findings.ShouldHaveSingleItem().IsBlocking.ShouldBeFalse();
        evaluation.Findings[0].Code.ShouldBe(SoftwareUpdateFindingCode.AlreadyCurrent);
    }

    [Test]
    public void InvalidManifest_IsNotCalledUpToDate()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.UpToDate, Manifest(), UpdateAvailabilityStatus.ManifestInvalid)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.CannotStart);
        Codes(evaluation).ShouldBe([SoftwareUpdateFindingCode.ManifestInvalid]);
    }

    [Test]
    public void IntermediateVersionRequired_CannotStart()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.RequiresIntermediate, Manifest(), UpdateAvailabilityStatus.UpdateRequiresIntermediate)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.CannotStart);
        Codes(evaluation).ShouldBe([SoftwareUpdateFindingCode.IntermediateVersionRequired]);
    }

    [Test]
    public void NoArtifactForThisInstallation_NamesTheInstallationKind()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.NoArtifactForInstallation, Manifest(), UpdateAvailabilityStatus.NotAvailableForInstallation)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.CannotStart);
        var finding = evaluation.Findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe(SoftwareUpdateFindingCode.NoArtifactForInstallation);
        finding.Statement.ShouldContain(nameof(InstallationKind.Docker));
    }

    [Test]
    public void UnknownRejection_FailsClosed()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight("some future reason", Manifest())));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.CannotStart);
        Codes(evaluation).ShouldBe([SoftwareUpdateFindingCode.TriggerRefused]);
    }

    [Test]
    public void ActiveOperation_BlocksAndNamesItsType()
    {
        var active = Operation(UpdateOperationStatus.Running, UpdateOperationType.WhisperInstall);

        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.OperationInProgress, Manifest(), active: active)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.CannotStart);
        var finding = evaluation.Findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe(SoftwareUpdateFindingCode.OperationActive);
        finding.Statement.ShouldContain(nameof(UpdateOperationType.WhisperInstall));
    }

    [Test]
    public void ActiveOperationWhileAlreadyCurrent_IsNotReportedAsNothingToDo()
    {
        var active = Operation(UpdateOperationStatus.Running, UpdateOperationType.Rollback);

        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.UpToDate, Manifest(), UpdateAvailabilityStatus.UpToDate, active)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.CannotStart);
        Codes(evaluation).ShouldContain(SoftwareUpdateFindingCode.OperationActive);
    }

    [Test]
    public void PendingRowWaitingTooLong_IsReportedWithoutClaimingTheUpdaterIsDown()
    {
        var waiting = Operation(
            UpdateOperationStatus.Pending,
            requestedAt: Now.AddMinutes(-SoftwareUpdateAdviceLimits.UnclaimedOperationMinutes));

        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.OperationInProgress, Manifest(), active: waiting)));

        var finding = evaluation.Findings.Single(f => f.Code == SoftwareUpdateFindingCode.OperationNotPickedUp);
        finding.Statement.ShouldContain(SoftwareUpdateAdviceLimits.UnclaimedOperationMinutes.ToString());
        finding.Statement.ShouldNotContain("not running");
    }

    [Test]
    public void PendingRowJustQueued_IsNoSeparateFinding()
    {
        var waiting = Operation(
            UpdateOperationStatus.Pending,
            requestedAt: Now.AddMinutes(1 - SoftwareUpdateAdviceLimits.UnclaimedOperationMinutes));

        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.OperationInProgress, Manifest(), active: waiting)));

        Codes(evaluation).ShouldNotContain(SoftwareUpdateFindingCode.OperationNotPickedUp);
    }

    [Test]
    public void RunningRowOlderThanTheThreshold_IsNotCalledWaiting()
    {
        var running = Operation(
            UpdateOperationStatus.Running, requestedAt: Now.AddHours(-1), startedAt: Now.AddMinutes(-59));

        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(
            UpdateReasons.OperationInProgress, Manifest(), active: running)));

        Codes(evaluation).ShouldNotContain(SoftwareUpdateFindingCode.OperationNotPickedUp);
    }

    [TestCase(UpdateOperationStatus.Failed)]
    [TestCase(UpdateOperationStatus.RolledBack)]
    public void FailedAttemptAtTheOfferedVersion_IsACautionAManualStartRepeats(UpdateOperationStatus status)
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(manifest: Manifest()), Operation(status)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartWithCaution);
        Codes(evaluation).ShouldContain(SoftwareUpdateFindingCode.LastAttemptFailed);
    }

    [Test]
    public void FailedAttemptAtAnOlderVersion_IsNoFinding()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(manifest: Manifest()), Operation(UpdateOperationStatus.Failed, target: "1.0.51")));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartNow);
    }

    [Test]
    public void LastOperationARollback_IsNotReadAsAFailedAttempt()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(manifest: Manifest()), Operation(UpdateOperationStatus.Failed, UpdateOperationType.Rollback)));

        Codes(evaluation).ShouldNotContain(SoftwareUpdateFindingCode.LastAttemptFailed);
    }

    [TestCase(UpdateOperationType.Update)]
    [TestCase(UpdateOperationType.Rollback)]
    public void FailedReturnToThePreviousVersion_AdvisesAgainstAStartTheTriggerWouldAccept(UpdateOperationType type)
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(manifest: Manifest()), Operation(UpdateOperationStatus.RollbackFailed, type, "1.0.51")));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.NotRecommended);
        evaluation.CanStart.ShouldBeTrue();
        evaluation.Findings[0].Code.ShouldBe(SoftwareUpdateFindingCode.LastRollbackFailed);
        Codes(evaluation).ShouldNotContain(SoftwareUpdateFindingCode.LastAttemptFailed);
    }

    [Test]
    public void BetaChannel_IsACaution()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(manifest: Manifest(), channel: UpdateChannel.Beta)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartWithCaution);
        Codes(evaluation).ShouldContain(SoftwareUpdateFindingCode.BetaChannel);
        evaluation.Channel.ShouldBe(nameof(UpdateChannel.Beta));
    }

    [Test]
    public void SchemaChange_IsACaution()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(manifest: Manifest(containsMigrations: true))));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartWithCaution);
        Codes(evaluation).ShouldContain(SoftwareUpdateFindingCode.ContainsMigrations);
    }

    [TestCase(1, 0, 0, 0)]
    [TestCase(0, 1, 0, 0)]
    [TestCase(0, 0, 1, 0)]
    [TestCase(0, 0, 0, 1)]
    public void AnyRunningPlanningWork_IsACaution(int autofill, int wizard, int harmonizer, int holistic)
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(manifest: Manifest()), work: new SoftwareUpdateRunningWork(autofill, wizard, harmonizer, holistic)));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartWithCaution);
        Codes(evaluation).ShouldContain(SoftwareUpdateFindingCode.RunningWork);
    }

    [Test]
    public void OtherConnectedUsers_AreACautionWithTheirNumber()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(Preflight(manifest: Manifest()), others: 3));

        evaluation.Verdict.ShouldBe(SoftwareUpdateVerdict.StartWithCaution);
        evaluation.OtherConnectedUsers.ShouldBe(3);
        evaluation.Findings.Single(f => f.Code == SoftwareUpdateFindingCode.OtherUsersConnected).Statement.ShouldContain("3");
    }

    [Test]
    public void Cautions_StayOutWhenTheUpdateCannotStartAnyway()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(UpdateReasons.UpToDate, Manifest(containsMigrations: true), UpdateAvailabilityStatus.UpToDate),
            work: new SoftwareUpdateRunningWork(1, 1, 1, 1),
            others: 2));

        Codes(evaluation).ShouldBe([SoftwareUpdateFindingCode.AlreadyCurrent]);
    }

    [Test]
    public void BlockingFindings_ComeFirst()
    {
        var evaluation = SoftwareUpdateAdvisor.Evaluate(Facts(
            Preflight(manifest: Manifest()), Operation(UpdateOperationStatus.RollbackFailed), others: 1));

        evaluation.Findings[0].IsBlocking.ShouldBeTrue();
        evaluation.Findings.Skip(1).ShouldAllBe(finding => !finding.IsBlocking);
    }
}

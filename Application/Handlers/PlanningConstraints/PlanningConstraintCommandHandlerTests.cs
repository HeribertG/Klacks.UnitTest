// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.PlanningConstraints;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.PlanningConstraints;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.PlanningConstraints;

[TestFixture]
public class PlanningConstraintCommandHandlerTests
{
    private const string Admin = "admin-user-id";
    private const string MaxRunJson = """{"schemaVersion":1,"kind":"Night","maxRun":3}""";

    private static readonly DateTime NowUtc = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private IPlanningConstraintRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IPlanningConstraintReferenceReader _references = null!;
    private TimeProvider _timeProvider = null!;
    private readonly PlanningConstraintMapper _mapper = new();
    private readonly PlanningConstraintValidator _validator = new();

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IPlanningConstraintRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _timeProvider = Substitute.For<TimeProvider>();
        _timeProvider.GetUtcNow().Returns(new DateTimeOffset(NowUtc));
        _references = Substitute.For<IPlanningConstraintReferenceReader>();
        _references.ScopeTargetExistsAsync(Arg.Any<PlanningConstraintScopeType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _references.ActiveScenarioExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Test]
    public async Task Create_AssignsServerId_AndStoresAdminApproved()
    {
        PlanningConstraint? added = null;
        _repository.Add(Arg.Do<PlanningConstraint>(c => added = c));
        var handler = new CreatePlanningConstraintCommandHandler(
            _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<CreatePlanningConstraintCommandHandler>.Instance);

        var result = await handler.Handle(new CreatePlanningConstraintCommand(Write(), Admin), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.Id.ShouldNotBe(Guid.Empty);
        result.Id.ShouldBe(added.Id);
        result.Origin.ShouldBe(RuleOrigin.Admin);
        result.ApprovalStatus.ShouldBe(RuleApprovalStatus.Approved);
        result.ApprovedBy.ShouldBe(Admin);
        result.ApprovedAt.ShouldBe(NowUtc);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Create_HardTeamFairness_IsRejected_AndNothingIsStored()
    {
        var resource = Write();
        resource.Kind = PlanningConstraintKind.TeamFairness;
        resource.ScopeType = PlanningConstraintScopeType.Group;
        resource.ScopeId = Guid.NewGuid();
        resource.ParametersJson = """{"schemaVersion":1,"metric":"NightDays","window":"Month","maxSpread":1}""";
        var handler = new CreatePlanningConstraintCommandHandler(
            _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<CreatePlanningConstraintCommandHandler>.Instance);

        await Should.ThrowAsync<InvalidRequestException>(() => handler.Handle(new CreatePlanningConstraintCommand(resource, Admin), CancellationToken.None));

        _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Update_OfApprovedRow_CreatesSuccessor_AndRevokesTheOriginal()
    {
        var original = Stored(RuleApprovalStatus.Approved);
        original.AnalyseToken = Guid.NewGuid();
        _repository.GetAsync(original.Id, Arg.Any<CancellationToken>()).Returns(original);
        PlanningConstraint? added = null;
        _repository.Add(Arg.Do<PlanningConstraint>(c => added = c));
        var resource = Write();
        resource.ParametersJson = """{"schemaVersion":1,"kind":"Night","maxRun":4}""";
        var handler = new UpdatePlanningConstraintCommandHandler(
            _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<UpdatePlanningConstraintCommandHandler>.Instance);

        var result = await handler.Handle(new UpdatePlanningConstraintCommand(original.Id, resource, Admin), CancellationToken.None);

        original.ApprovalStatus.ShouldBe(RuleApprovalStatus.Revoked);
        original.ParametersJson.ShouldBe(MaxRunJson, "an approved row is immutable");
        added.ShouldNotBeNull();
        result.Id.ShouldBe(added!.Id);
        result.Id.ShouldNotBe(original.Id);
        result.PreviousVersionId.ShouldBe(original.Id);
        result.AnalyseToken.ShouldBe(original.AnalyseToken);
        result.ApprovalStatus.ShouldBe(RuleApprovalStatus.Approved);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Update_OfProposedRow_EditsInPlace_AndStaysProposed()
    {
        var proposal = Stored(RuleApprovalStatus.Proposed);
        proposal.Origin = RuleOrigin.LlmProposal;
        _repository.GetAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        var resource = Write();
        resource.Paraphrase = "At most four nights in a row";
        var handler = new UpdatePlanningConstraintCommandHandler(
            _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<UpdatePlanningConstraintCommandHandler>.Instance);

        var result = await handler.Handle(new UpdatePlanningConstraintCommand(proposal.Id, resource, Admin), CancellationToken.None);

        result.Id.ShouldBe(proposal.Id);
        result.ApprovalStatus.ShouldBe(RuleApprovalStatus.Proposed);
        result.Origin.ShouldBe(RuleOrigin.LlmProposal);
        result.Paraphrase.ShouldBe(resource.Paraphrase);
        _repository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [TestCase(RuleApprovalStatus.Rejected)]
    [TestCase(RuleApprovalStatus.Revoked)]
    public async Task Update_OfFinalRow_IsAConflict(RuleApprovalStatus status)
    {
        var row = Stored(status);
        _repository.GetAsync(row.Id, Arg.Any<CancellationToken>()).Returns(row);
        var handler = new UpdatePlanningConstraintCommandHandler(
            _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<UpdatePlanningConstraintCommandHandler>.Instance);

        await Should.ThrowAsync<ConflictException>(() => handler.Handle(new UpdatePlanningConstraintCommand(row.Id, Write(), Admin), CancellationToken.None));
    }

    [Test]
    public async Task Approve_FreshProposal_IsApproved()
    {
        var proposal = Stored(RuleApprovalStatus.Proposed, NowUtc.AddDays(-PlanningConstraintDefaults.ProposalLifetimeDays + 1));
        _repository.GetAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);

        var result = await ApproveHandler().Handle(new ApprovePlanningConstraintCommand(proposal.Id, Admin), CancellationToken.None);

        result.ApprovalStatus.ShouldBe(RuleApprovalStatus.Approved);
        result.ApprovedBy.ShouldBe(Admin);
        result.ApprovedAt.ShouldBe(NowUtc);
    }

    [Test]
    public async Task Approve_ExpiredProposal_IsRefused_EvenBeforeTheSweepRan()
    {
        var proposal = Stored(RuleApprovalStatus.Proposed, NowUtc.AddDays(-PlanningConstraintDefaults.ProposalLifetimeDays));
        _repository.GetAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);

        await Should.ThrowAsync<ConflictException>(() => ApproveHandler().Handle(new ApprovePlanningConstraintCommand(proposal.Id, Admin), CancellationToken.None));

        proposal.ApprovalStatus.ShouldBe(RuleApprovalStatus.Proposed);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Approve_UnknownId_IsNotFound()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() => ApproveHandler().Handle(new ApprovePlanningConstraintCommand(Guid.NewGuid(), Admin), CancellationToken.None));
    }

    [Test]
    public async Task RejectAndRevoke_FollowTheLifecycle()
    {
        var proposal = Stored(RuleApprovalStatus.Proposed);
        var approved = Stored(RuleApprovalStatus.Approved);
        _repository.GetAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        _repository.GetAsync(approved.Id, Arg.Any<CancellationToken>()).Returns(approved);
        var reject = new RejectPlanningConstraintCommandHandler(_repository, _mapper, _unitOfWork, NullLogger<RejectPlanningConstraintCommandHandler>.Instance);
        var revoke = new RevokePlanningConstraintCommandHandler(_repository, _mapper, _unitOfWork, NullLogger<RevokePlanningConstraintCommandHandler>.Instance);

        (await reject.Handle(new RejectPlanningConstraintCommand(proposal.Id), CancellationToken.None)).ApprovalStatus.ShouldBe(RuleApprovalStatus.Rejected);
        (await revoke.Handle(new RevokePlanningConstraintCommand(approved.Id), CancellationToken.None)).ApprovalStatus.ShouldBe(RuleApprovalStatus.Revoked);
        await Should.ThrowAsync<ConflictException>(() => reject.Handle(new RejectPlanningConstraintCommand(approved.Id), CancellationToken.None));
        await Should.ThrowAsync<ConflictException>(() => revoke.Handle(new RevokePlanningConstraintCommand(proposal.Id), CancellationToken.None));
    }

    [TestCase(RuleApprovalStatus.Approved)]
    [TestCase(RuleApprovalStatus.Revoked)]
    public async Task Delete_OfApprovedOrRevokedRow_IsAConflict(RuleApprovalStatus status)
    {
        var row = Stored(status);
        _repository.GetAsync(row.Id, Arg.Any<CancellationToken>()).Returns(row);

        await Should.ThrowAsync<ConflictException>(() => DeleteHandler().Handle(new DeletePlanningConstraintCommand(row.Id), CancellationToken.None));

        _repository.DidNotReceiveWithAnyArgs().Remove(default!);
    }

    [TestCase(RuleApprovalStatus.Proposed)]
    [TestCase(RuleApprovalStatus.Rejected)]
    public async Task Delete_OfProposedOrRejectedRow_SoftDeletes(RuleApprovalStatus status)
    {
        var row = Stored(status);
        _repository.GetAsync(row.Id, Arg.Any<CancellationToken>()).Returns(row);

        await DeleteHandler().Handle(new DeletePlanningConstraintCommand(row.Id), CancellationToken.None);

        _repository.Received(1).Remove(row);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Create_WithMissingScopeTarget_IsRejected()
    {
        var resource = Write();
        resource.ScopeType = PlanningConstraintScopeType.Group;
        resource.ScopeId = Guid.NewGuid();
        _references.ScopeTargetExistsAsync(PlanningConstraintScopeType.Group, resource.ScopeId, Arg.Any<CancellationToken>()).Returns(false);
        var handler = new CreatePlanningConstraintCommandHandler(
            _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<CreatePlanningConstraintCommandHandler>.Instance);

        await Should.ThrowAsync<InvalidRequestException>(() => handler.Handle(new CreatePlanningConstraintCommand(resource, Admin), CancellationToken.None));

        _repository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Create_ForAnInactiveScenario_IsRejected()
    {
        var resource = Write();
        resource.AnalyseToken = Guid.NewGuid();
        _references.ActiveScenarioExistsAsync(resource.AnalyseToken.Value, Arg.Any<CancellationToken>()).Returns(false);
        var handler = new CreatePlanningConstraintCommandHandler(
            _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<CreatePlanningConstraintCommandHandler>.Instance);

        await Should.ThrowAsync<InvalidRequestException>(() => handler.Handle(new CreatePlanningConstraintCommand(resource, Admin), CancellationToken.None));
    }

    [Test]
    public async Task Approve_LosingAConcurrentChange_IsAConflictWithItsErrorCode()
    {
        var proposal = Stored(RuleApprovalStatus.Proposed);
        _repository.GetAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        _unitOfWork.CompleteAsync().Returns(Task.FromException(new ConcurrencyException("row changed")));

        var thrown = await Should.ThrowAsync<PlanningConstraintConcurrencyException>(
            () => ApproveHandler().Handle(new ApprovePlanningConstraintCommand(proposal.Id, Admin), CancellationToken.None));

        thrown.ConflictCode.ShouldBe(PlanningConstraintConcurrencyException.ErrorCode);
    }

    private DeletePlanningConstraintCommandHandler DeleteHandler() => new(
        _repository, _mapper, _unitOfWork, NullLogger<DeletePlanningConstraintCommandHandler>.Instance);

    private ApprovePlanningConstraintCommandHandler ApproveHandler() => new(
        _repository, _validator, _references, _mapper, _unitOfWork, _timeProvider, NullLogger<ApprovePlanningConstraintCommandHandler>.Instance);

    private static PlanningConstraintWriteResource Write() => new()
    {
        Kind = PlanningConstraintKind.MaxConsecutiveOfKind,
        Severity = PlanningConstraintSeverity.Hard,
        Weight = 1d,
        ScopeType = PlanningConstraintScopeType.Global,
        ParametersJson = MaxRunJson,
    };

    private static PlanningConstraint Stored(RuleApprovalStatus status, DateTime? createTime = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = PlanningConstraintKind.MaxConsecutiveOfKind,
        Severity = PlanningConstraintSeverity.Hard,
        Weight = 1d,
        ScopeType = PlanningConstraintScopeType.Global,
        ParametersJson = MaxRunJson,
        ApprovalStatus = status,
        CreateTime = createTime ?? NowUtc.AddDays(-1),
    };
}

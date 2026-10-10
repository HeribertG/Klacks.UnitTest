// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Accept and reject only act on an Active scenario. A second accept of an already accepted (or
/// rejected/superseded) scenario used to run the whole promote pipeline again: the period's real works
/// were soft-deleted while the scenario token no longer carried any rows to promote, wiping the plan.
/// A late reject flipped an accepted scenario to Rejected and wrote that onto capture and ledger.
/// Both handlers must refuse with a coded 409 before touching any data.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioStatusGuardTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWorkSofteningRepository _softeningRepository = null!;
    private IScenarioComplianceService _complianceService = null!;
    private IWizardRunCaptureRepository _captureRepository = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private AnalyseScenario _scenario = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _softeningRepository = Substitute.For<IWorkSofteningRepository>();
        _complianceService = Substitute.For<IScenarioComplianceService>();
        _captureRepository = Substitute.For<IWizardRunCaptureRepository>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();

        _scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Name = "already handled",
            FromDate = new DateOnly(2026, 10, 1),
            UntilDate = new DateOnly(2026, 10, 31),
        };
        _repository.Get(_scenario.Id).Returns(_scenario);
    }

    private AcceptAnalyseScenarioCommandHandler CreateAcceptHandler() => new(
        _repository,
        _scenarioService,
        _unitOfWork,
        _softeningRepository,
        _complianceService,
        Substitute.For<ISupervisorOverrideAuthorizer>(),
        Substitute.For<IScheduleTimelineService>(),
        _conditionRepository,
        Substitute.For<IAgentConditionLedgerService>(),
        Substitute.For<IHttpContextAccessor>(),
        Substitute.For<IPeriodHoursService>(),
        Substitute.For<IWorkNotificationService>(),
        Substitute.For<IEscalationChainService>(),
        NullLogger<AcceptAnalyseScenarioCommandHandler>.Instance);

    private RejectAnalyseScenarioCommandHandler CreateRejectHandler() => new(
        _repository,
        _scenarioService,
        _unitOfWork,
        _captureRepository,
        _conditionRepository,
        Substitute.For<IAgentConditionLedgerService>(),
        Substitute.For<IHttpContextAccessor>(),
        NullLogger<RejectAnalyseScenarioCommandHandler>.Instance);

    [TestCase(AnalyseScenarioStatus.Accepted)]
    [TestCase(AnalyseScenarioStatus.Rejected)]
    [TestCase(AnalyseScenarioStatus.Superseded)]
    public async Task Accept_OfANonActiveScenario_IsRefusedWithACodedConflict_AndTouchesNothing(AnalyseScenarioStatus status)
    {
        // Arrange
        _scenario.Status = status;
        var handler = CreateAcceptHandler();

        // Act
        var ex = await Should.ThrowAsync<ScenarioNotActiveException>(
            () => handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None));

        // Assert
        ex.ShouldBeAssignableTo<ConflictException>();
        ex.ConflictCode.ShouldBe(ScenarioNotActiveException.ErrorCode);
        ex.Message.ShouldContain(_scenario.Id.ToString());
        ex.Message.ShouldContain(status.ToString());
        _scenario.Status.ShouldBe(status);

        await _scenarioService.DidNotReceive().ValidateNoAcceptConflictsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _complianceService.DidNotReceive().EvaluateAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _scenarioService.DidNotReceive().SoftDeleteRealScheduleDataAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _scenarioService.DidNotReceive().PromoteScenarioWorksAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _softeningRepository.DidNotReceive().DeleteByAnalyseTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().Put(Arg.Any<AnalyseScenario>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _conditionRepository.DidNotReceive().FindByScenarioIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestCase(AnalyseScenarioStatus.Accepted)]
    [TestCase(AnalyseScenarioStatus.Rejected)]
    [TestCase(AnalyseScenarioStatus.Superseded)]
    public async Task Reject_OfANonActiveScenario_IsRefusedWithACodedConflict_AndTouchesNothing(AnalyseScenarioStatus status)
    {
        // Arrange
        _scenario.Status = status;
        var handler = CreateRejectHandler();

        // Act
        var ex = await Should.ThrowAsync<ScenarioNotActiveException>(
            () => handler.Handle(
                new RejectAnalyseScenarioCommand(_scenario.Id, RejectReason.CoverageDrop, "late reject"),
                CancellationToken.None));

        // Assert
        ex.ConflictCode.ShouldBe(ScenarioNotActiveException.ErrorCode);
        _scenario.Status.ShouldBe(status);
        _scenario.RejectReason.ShouldBeNull();

        await _scenarioService.DidNotReceive().SoftDeleteScenarioDataAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().Put(Arg.Any<AnalyseScenario>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _captureRepository.DidNotReceive().GetByScenarioIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _conditionRepository.DidNotReceive().FindByScenarioIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reject_OfAnActiveScenario_StillDiscardsIt()
    {
        // Arrange
        _scenario.Status = AnalyseScenarioStatus.Active;
        var handler = CreateRejectHandler();

        // Act
        var result = await handler.Handle(new RejectAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
        await _scenarioService.Received(1).SoftDeleteScenarioDataAsync(_scenario.Token, Arg.Any<CancellationToken>());
    }
}

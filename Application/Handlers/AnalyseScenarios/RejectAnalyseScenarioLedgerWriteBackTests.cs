// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the reject write-back added in Etappe 4c: rejecting a scenario Klacksy prepared also closes
/// the finding it was prepared for, with the reason translated into the ledger's vocabulary and the
/// rejecting human recorded. A human-authored scenario has no ledger row and must stay untouched, and
/// no ledger trouble may ever cost the scenario rejection itself - the rejection is the user's action,
/// the write-back is bookkeeping.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class RejectAnalyseScenarioLedgerWriteBackTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWizardRunCaptureRepository _captureRepository = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private Klacks.Api.Application.Handlers.AnalyseScenarios.RejectAnalyseScenarioCommandHandler _handler = null!;
    private AnalyseScenario _scenario = null!;

    [SetUp]
    public void SetUp()
    {
        _scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "prepared",
            Status = AnalyseScenarioStatus.Active
        };

        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _repository.Get(_scenario.Id).Returns(_scenario);
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _captureRepository = Substitute.For<IWizardRunCaptureRepository>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _handler = new Klacks.Api.Application.Handlers.AnalyseScenarios.RejectAnalyseScenarioCommandHandler(
            _repository,
            _scenarioService,
            _unitOfWork,
            _captureRepository,
            _conditionRepository,
            _ledgerService,
            _httpContextAccessor,
            NullLogger<Klacks.Api.Application.Handlers.AnalyseScenarios.RejectAnalyseScenarioCommandHandler>.Instance);
    }

    [Test]
    public async Task Handle_ForAKlacksyPreparedScenario_RejectsTheFindingWithTheTranslatedReasonAndTheRejectingUser()
    {
        // Arrange
        var conditionId = Guid.NewGuid();
        var rejectingUserId = Guid.NewGuid();
        GivenCondition(conditionId);
        GivenSignedInUser(rejectingUserId);
        _ledgerService.TryRejectAsync(
            Arg.Any<Guid>(), Arg.Any<AgentConditionRejectReason>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var handled = await _handler.Handle(
            new RejectAnalyseScenarioCommand(_scenario.Id, RejectReason.CoverageDrop, "coverage would drop"),
            CancellationToken.None);

        // Assert
        handled.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
        _scenario.RejectReason.ShouldBe(RejectReason.CoverageDrop);

        await _ledgerService.Received(1).TryRejectAsync(
            conditionId,
            AgentConditionRejectReason.WrongThisTime,
            rejectingUserId,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithoutARejectReason_RecordsNoReasonRatherThanInventingOne()
    {
        // Arrange
        var conditionId = Guid.NewGuid();
        GivenCondition(conditionId);
        _ledgerService.TryRejectAsync(
            Arg.Any<Guid>(), Arg.Any<AgentConditionRejectReason>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await _handler.Handle(new RejectAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        // Assert
        await _ledgerService.Received(1).TryRejectAsync(
            conditionId, AgentConditionRejectReason.NoReason, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ForAHumanAuthoredScenario_TouchesNoLedgerRow()
    {
        // Arrange - no condition points at this scenario, which is the ordinary case.
        _conditionRepository.FindByScenarioIdAsync(_scenario.Id, Arg.Any<CancellationToken>())
            .Returns((AgentCondition?)null);

        // Act
        var handled = await _handler.Handle(
            new RejectAnalyseScenarioCommand(_scenario.Id, RejectReason.TooMuchChurn), CancellationToken.None);

        // Assert
        handled.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryRejectAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_WhenTheLedgerRefusesTheRejection_StillRejectsTheScenario()
    {
        // Arrange - the row may already be Executed, Resolved or dismissed by another planner.
        GivenCondition(Guid.NewGuid());
        _ledgerService.TryRejectAsync(
            Arg.Any<Guid>(), Arg.Any<AgentConditionRejectReason>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var handled = await _handler.Handle(
            new RejectAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        // Assert
        handled.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_WhenTheLedgerThrows_StillRejectsTheScenario()
    {
        // Arrange
        _conditionRepository.FindByScenarioIdAsync(_scenario.Id, Arg.Any<CancellationToken>())
            .Returns<AgentCondition?>(_ => throw new InvalidOperationException("ledger unavailable"));

        // Act
        var handled = await _handler.Handle(
            new RejectAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        // Assert
        handled.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
    }

    [Test]
    public async Task Handle_ForAnUnknownScenario_ThrowsAndNeverReachesTheLedger()
    {
        // Act / Assert
        await Should.ThrowAsync<KeyNotFoundException>(() =>
            _handler.Handle(new RejectAnalyseScenarioCommand(Guid.NewGuid()), CancellationToken.None));

        await _conditionRepository.DidNotReceiveWithAnyArgs().FindByScenarioIdAsync(default, default);
    }

    private void GivenCondition(Guid conditionId)
    {
        _conditionRepository.FindByScenarioIdAsync(_scenario.Id, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition
            {
                Id = conditionId,
                TriggerKind = "empty_container",
                Fingerprint = "empty_container:" + conditionId,
                Severity = "medium",
                Status = AgentConditionStatus.Prepared,
                ScenarioId = _scenario.Id,
                DetectedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                PayloadJson = "{}"
            });
    }

    private void GivenSignedInUser(Guid userId)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())]));
        _httpContextAccessor.HttpContext.Returns(new DefaultHttpContext { User = principal });
    }
}

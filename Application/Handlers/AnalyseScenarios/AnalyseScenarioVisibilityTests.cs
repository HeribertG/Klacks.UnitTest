// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-11 the AnalyseScenarios endpoints (list, get, summary, create, accept, reject, delete, delete all,
/// rename) ignored group visibility, so a group-restricted planner could read, decide or delete the scenarios of
/// groups outside their scope while Klacksy's list_scenarios already filtered them. A scenario of a hidden group -
/// and a group-less scenario, which spans every group, for a restricted caller - must now be answered exactly like
/// a missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.AnalyseScenarios;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioVisibilityTests
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly Until = new(2026, 3, 31);

    private IAnalyseScenarioRepository _repository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWizardRunCaptureRepository _captureRepository = null!;
    private IGroupVisibilityGuard _guard = null!;
    private Guid _visibleGroupId;
    private Guid _hiddenGroupId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _captureRepository = Substitute.For<IWizardRunCaptureRepository>();

        _visibleGroupId = Guid.NewGuid();
        _hiddenGroupId = Guid.NewGuid();

        _guard = Substitute.For<IGroupVisibilityGuard>();
        _guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) == _visibleGroupId);
    }

    private AnalyseScenario Scenario(Guid? groupId, AnalyseScenarioStatus status = AnalyseScenarioStatus.Active)
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "Proposal",
            GroupId = groupId,
            FromDate = From,
            UntilDate = Until,
            Status = status
        };
        _repository.Get(scenario.Id).Returns(scenario);
        return scenario;
    }

    private static string NotFoundMessage(Guid id) => $"AnalyseScenario with ID {id} not found";

    [Test]
    public async Task Get_ScenarioOfVisibleGroup_IsReturned()
    {
        var scenario = Scenario(_visibleGroupId);
        var handler = new GetAnalyseScenarioQueryHandler(
            _repository, _guard, Substitute.For<ILogger<GetAnalyseScenarioQueryHandler>>());

        var result = await handler.Handle(new GetAnalyseScenarioQuery(scenario.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(scenario.Id);
    }

    [Test]
    public async Task Get_ScenarioOfHiddenGroup_IsAnsweredLikeAMissingScenario()
    {
        var scenario = Scenario(_hiddenGroupId);
        var handler = new GetAnalyseScenarioQueryHandler(
            _repository, _guard, Substitute.For<ILogger<GetAnalyseScenarioQueryHandler>>());

        var result = await handler.Handle(new GetAnalyseScenarioQuery(scenario.Id), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task Get_GrouplessScenario_IsHiddenFromARestrictedCaller()
    {
        var scenario = Scenario(null);
        var handler = new GetAnalyseScenarioQueryHandler(
            _repository, _guard, Substitute.For<ILogger<GetAnalyseScenarioQueryHandler>>());

        var result = await handler.Handle(new GetAnalyseScenarioQuery(scenario.Id), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task Get_GrouplessScenario_IsVisibleToAnUnrestrictedCaller()
    {
        var scenario = Scenario(null);
        var handler = new GetAnalyseScenarioQueryHandler(
            _repository, TestGroupWriteVisibility.UnrestrictedGroups(), Substitute.For<ILogger<GetAnalyseScenarioQueryHandler>>());

        var result = await handler.Handle(new GetAnalyseScenarioQuery(scenario.Id), CancellationToken.None);

        result.ShouldNotBeNull();
    }

    [Test]
    public async Task Summary_ScenarioOfHiddenGroup_IsAnsweredLikeAMissingScenarioWithoutBuildingIt()
    {
        var scenario = Scenario(_hiddenGroupId);
        var summaryBuilder = Substitute.For<IScenarioSummaryBuilder>();
        var handler = new GetScenarioSummaryQueryHandler(
            _repository, summaryBuilder, _guard, Substitute.For<ILogger<GetScenarioSummaryQueryHandler>>());

        var result = await handler.Handle(new GetScenarioSummaryQuery(scenario.Id), CancellationToken.None);

        result.ShouldBeNull();
        await summaryBuilder.DidNotReceiveWithAnyArgs().BuildAsync(default, default);
    }

    [Test]
    public async Task Summary_ScenarioOfVisibleGroup_IsBuilt()
    {
        var scenario = Scenario(_visibleGroupId);
        var summaryBuilder = Substitute.For<IScenarioSummaryBuilder>();
        var handler = new GetScenarioSummaryQueryHandler(
            _repository, summaryBuilder, _guard, Substitute.For<ILogger<GetScenarioSummaryQueryHandler>>());

        await handler.Handle(new GetScenarioSummaryQuery(scenario.Id), CancellationToken.None);

        await summaryBuilder.Received(1).BuildAsync(scenario.Token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_OfHiddenGroup_IsEmptyAndDoesNotQuery()
    {
        var handler = new ListAnalyseScenariosQueryHandler(
            _repository, _guard, Substitute.For<ILogger<ListAnalyseScenariosQueryHandler>>());

        var result = await handler.Handle(new ListAnalyseScenariosQuery(_hiddenGroupId), CancellationToken.None);

        result.ShouldBeEmpty();
        await _repository.DidNotReceiveWithAnyArgs().GetByGroupAsync(default, default);
    }

    [Test]
    public async Task List_OfGrouplessScenarios_IsEmptyForARestrictedCaller()
    {
        var handler = new ListAnalyseScenariosQueryHandler(
            _repository, _guard, Substitute.For<ILogger<ListAnalyseScenariosQueryHandler>>());

        var result = await handler.Handle(new ListAnalyseScenariosQuery(null), CancellationToken.None);

        result.ShouldBeEmpty();
        await _repository.DidNotReceiveWithAnyArgs().GetByGroupAsync(default, default);
    }

    [Test]
    public async Task List_OfVisibleGroup_ReturnsItsScenarios()
    {
        var scenario = Scenario(_visibleGroupId);
        _repository.GetByGroupAsync(_visibleGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario> { scenario });
        var handler = new ListAnalyseScenariosQueryHandler(
            _repository, _guard, Substitute.For<ILogger<ListAnalyseScenariosQueryHandler>>());

        var result = await handler.Handle(new ListAnalyseScenariosQuery(_visibleGroupId), CancellationToken.None);

        result.Select(r => r.Id).ShouldBe(new[] { scenario.Id });
    }

    [Test]
    public async Task Create_ForHiddenGroup_IsRefusedLikeAMissingGroupAndClonesNothing()
    {
        var handler = CreateHandler(_guard);

        var act = () => handler.Handle(new CreateAnalyseScenarioCommand(CreateRequest(_hiddenGroupId)), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Group with ID {_hiddenGroupId} not found");
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _scenarioService.DidNotReceiveWithAnyArgs().CloneScenarioDataAsync(default, default, default, default, default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Create_WithoutGroup_IsForbiddenForARestrictedCaller()
    {
        var handler = CreateHandler(_guard);

        var act = () => handler.Handle(new CreateAnalyseScenarioCommand(CreateRequest(null)), CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(act);
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Create_ForVisibleGroup_IsStored()
    {
        var handler = CreateHandler(_guard);

        var result = await handler.Handle(new CreateAnalyseScenarioCommand(CreateRequest(_visibleGroupId)), CancellationToken.None);

        result.GroupId.ShouldBe(_visibleGroupId);
        await _repository.ReceivedWithAnyArgs(1).Add(default!);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Create_WithoutGroup_IsAllowedForAnUnrestrictedCaller()
    {
        var handler = CreateHandler(TestGroupWriteVisibility.UnrestrictedGroups());

        var result = await handler.Handle(new CreateAnalyseScenarioCommand(CreateRequest(null)), CancellationToken.None);

        result.GroupId.ShouldBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Accept_DecidedScenarioOfHiddenGroup_AnswersNotFoundInsteadOfConflict()
    {
        var scenario = Scenario(_hiddenGroupId, AnalyseScenarioStatus.Accepted);
        var handler = AcceptHandler();

        var act = () => handler.Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe(NotFoundMessage(scenario.Id));
        await _scenarioService.DidNotReceiveWithAnyArgs().ValidateNoAcceptConflictsAsync(default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Accept_ActiveScenarioOfHiddenGroup_PromotesNothing()
    {
        var scenario = Scenario(_hiddenGroupId);
        var handler = AcceptHandler();

        var act = () => handler.Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe(NotFoundMessage(scenario.Id));
        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteRealScheduleDataAsync(default, default, default, default, default);
        await _scenarioService.DidNotReceiveWithAnyArgs().PromoteScenarioWorksAsync(default, default, default, default);
        scenario.Status.ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task Reject_DecidedScenarioOfHiddenGroup_AnswersNotFoundInsteadOfConflict()
    {
        var scenario = Scenario(_hiddenGroupId, AnalyseScenarioStatus.Rejected);
        var handler = RejectHandler();

        var act = () => handler.Handle(new RejectAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe(NotFoundMessage(scenario.Id));
    }

    [Test]
    public async Task Reject_ActiveScenarioOfHiddenGroup_DiscardsNothing()
    {
        var scenario = Scenario(_hiddenGroupId);
        var handler = RejectHandler();

        var act = () => handler.Handle(new RejectAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        await Should.ThrowAsync<KeyNotFoundException>(act);
        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteScenarioDataAsync(default, default);
        scenario.Status.ShouldBe(AnalyseScenarioStatus.Active);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Reject_ActiveScenarioOfVisibleGroup_IsRejected()
    {
        var scenario = Scenario(_visibleGroupId);
        var handler = RejectHandler();

        var result = await handler.Handle(new RejectAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        scenario.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
    }

    [Test]
    public async Task Delete_ScenarioOfHiddenGroup_IsAnsweredLikeAMissingScenarioAndDeletesNothing()
    {
        var scenario = Scenario(_hiddenGroupId);
        var handler = new DeleteAnalyseScenarioCommandHandler(
            _repository, _scenarioService, _unitOfWork, _captureRepository, _guard,
            Substitute.For<ILogger<DeleteAnalyseScenarioCommandHandler>>());

        var act = () => handler.Handle(new DeleteAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe(NotFoundMessage(scenario.Id));
        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteScenarioDataAsync(default, default);
        await _repository.DidNotReceiveWithAnyArgs().Delete(default);
    }

    [Test]
    public async Task Delete_ScenarioOfVisibleGroup_IsDeleted()
    {
        var scenario = Scenario(_visibleGroupId);
        var handler = new DeleteAnalyseScenarioCommandHandler(
            _repository, _scenarioService, _unitOfWork, _captureRepository, _guard,
            Substitute.For<ILogger<DeleteAnalyseScenarioCommandHandler>>());

        var result = await handler.Handle(new DeleteAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).Delete(scenario.Id);
    }

    [Test]
    public async Task DeleteAll_OfHiddenGroup_IsRefusedLikeAMissingGroupAndDeletesNothing()
    {
        var handler = DeleteAllHandler();

        var act = () => handler.Handle(new DeleteAllAnalyseScenariosCommand(_hiddenGroupId), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Group with ID {_hiddenGroupId} not found");
        await _repository.DidNotReceiveWithAnyArgs().GetByGroupAsync(default, default);
        await _repository.DidNotReceiveWithAnyArgs().Delete(default);
    }

    [Test]
    public async Task DeleteAll_OfGrouplessScenarios_IsForbiddenForARestrictedCaller()
    {
        var handler = DeleteAllHandler();

        var act = () => handler.Handle(new DeleteAllAnalyseScenariosCommand(null), CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(act);
        await _repository.DidNotReceiveWithAnyArgs().Delete(default);
    }

    [Test]
    public async Task DeleteAll_OfVisibleGroup_DeletesItsScenarios()
    {
        var scenario = Scenario(_visibleGroupId);
        _repository.GetByGroupAsync(_visibleGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario> { scenario });
        var handler = DeleteAllHandler();

        var result = await handler.Handle(new DeleteAllAnalyseScenariosCommand(_visibleGroupId), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).Delete(scenario.Id);
    }

    [Test]
    public async Task Rename_ScenarioOfHiddenGroup_IsAnsweredLikeAMissingScenarioAndKeepsTheName()
    {
        var scenario = Scenario(_hiddenGroupId);
        var handler = new RenameAnalyseScenarioCommandHandler(
            _repository, _unitOfWork, _guard, Substitute.For<ILogger<RenameAnalyseScenarioCommandHandler>>());

        var act = () => handler.Handle(new RenameAnalyseScenarioCommand(scenario.Id, "Renamed"), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe(NotFoundMessage(scenario.Id));
        scenario.Name.ShouldBe("Proposal");
        await _repository.DidNotReceiveWithAnyArgs().Put(default!);
    }

    [Test]
    public async Task Rename_ScenarioOfVisibleGroup_IsRenamed()
    {
        var scenario = Scenario(_visibleGroupId);
        var handler = new RenameAnalyseScenarioCommandHandler(
            _repository, _unitOfWork, _guard, Substitute.For<ILogger<RenameAnalyseScenarioCommandHandler>>());

        var result = await handler.Handle(new RenameAnalyseScenarioCommand(scenario.Id, "Renamed"), CancellationToken.None);

        result.Name.ShouldBe("Renamed");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    private static CreateAnalyseScenarioRequest CreateRequest(Guid? groupId) => new()
    {
        Name = "Scenario",
        GroupId = groupId,
        FromDate = From,
        UntilDate = Until
    };

    private CreateAnalyseScenarioCommandHandler CreateHandler(IGroupVisibilityGuard guard) => new(
        _repository, _scenarioService, _unitOfWork, guard, Substitute.For<ILogger<CreateAnalyseScenarioCommandHandler>>());

    private DeleteAllAnalyseScenariosCommandHandler DeleteAllHandler() => new(
        _repository, _scenarioService, _unitOfWork, _captureRepository, _guard,
        Substitute.For<ILogger<DeleteAllAnalyseScenariosCommandHandler>>());

    private RejectAnalyseScenarioCommandHandler RejectHandler() => new(
        _repository,
        _scenarioService,
        _unitOfWork,
        _captureRepository,
        Substitute.For<IAgentConditionRepository>(),
        Substitute.For<IAgentConditionLedgerService>(),
        Substitute.For<IHttpContextAccessor>(),
        _guard,
        Substitute.For<ILogger<RejectAnalyseScenarioCommandHandler>>());

    private AcceptAnalyseScenarioCommandHandler AcceptHandler() => new(
        _repository,
        _scenarioService,
        _unitOfWork,
        Substitute.For<IWorkSofteningRepository>(),
        Substitute.For<IScenarioComplianceService>(),
        Substitute.For<ISupervisorOverrideAuthorizer>(),
        Substitute.For<IScheduleTimelineService>(),
        Substitute.For<IAgentConditionRepository>(),
        Substitute.For<IAgentConditionLedgerService>(),
        Substitute.For<IHttpContextAccessor>(),
        Substitute.For<IPeriodHoursService>(),
        Substitute.For<IWorkNotificationService>(),
        Substitute.For<IEscalationChainService>(),
        _guard,
        Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
}

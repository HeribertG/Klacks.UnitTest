// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit Tests fuer die AnalyseScenario-Handler (Create, List, Get, Accept, Reject, Delete).
/// @param _repository - Mock fuer IAnalyseScenarioRepository
/// @param _unitOfWork - Mock fuer IUnitOfWork
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.AnalyseScenarios;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class CreateAnalyseScenarioCommandHandlerTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private CreateAnalyseScenarioCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();

        var logger = Substitute.For<ILogger<CreateAnalyseScenarioCommandHandler>>();
        _handler = new CreateAnalyseScenarioCommandHandler(_repository, _scenarioService, _unitOfWork, logger);
    }

    [Test]
    public async Task Should_Create_Scenario_With_NewToken()
    {
        // Arrange
        var request = new CreateAnalyseScenarioRequest
        {
            Name = "Test Scenario",
            Description = "Test description",
            GroupId = Guid.NewGuid(),
            FromDate = new DateOnly(2026, 3, 1),
            UntilDate = new DateOnly(2026, 3, 31)
        };
        var command = new CreateAnalyseScenarioCommand(request);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Token.Should().NotBe(Guid.Empty);
        result.Name.Should().Be("Test Scenario");
    }

    [Test]
    public async Task Should_Set_Status_To_Active()
    {
        // Arrange
        var request = new CreateAnalyseScenarioRequest
        {
            Name = "Active Scenario",
            Description = null,
            GroupId = Guid.NewGuid(),
            FromDate = new DateOnly(2026, 4, 1),
            UntilDate = new DateOnly(2026, 4, 30)
        };
        var command = new CreateAnalyseScenarioCommand(request);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Status.Should().Be((int)AnalyseScenarioStatus.Active);
    }
}

[TestFixture]
public class ListAnalyseScenariosQueryHandlerTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private ListAnalyseScenariosQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        var logger = Substitute.For<ILogger<ListAnalyseScenariosQueryHandler>>();
        _handler = new ListAnalyseScenariosQueryHandler(_repository, logger);
    }

    [Test]
    public async Task Should_Return_Empty_List_When_No_Scenarios()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        _repository.GetByGroupAsync(groupId, Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());
        var query = new ListAnalyseScenariosQuery(groupId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Test]
    public async Task Should_Return_Mapped_Scenarios()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var scenarios = new List<AnalyseScenario>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Scenario A",
                Description = "Desc A",
                GroupId = groupId,
                FromDate = new DateOnly(2026, 1, 1),
                UntilDate = new DateOnly(2026, 1, 31),
                Token = token,
                CreatedByUser = "admin",
                Status = AnalyseScenarioStatus.Active
            }
        };
        _repository.GetByGroupAsync(groupId, Arg.Any<CancellationToken>())
            .Returns(scenarios);
        var query = new ListAnalyseScenariosQuery(groupId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Scenario A");
        result[0].Token.Should().Be(token);
        result[0].Status.Should().Be((int)AnalyseScenarioStatus.Active);
    }
}

[TestFixture]
public class GetAnalyseScenarioQueryHandlerTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private GetAnalyseScenarioQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        var logger = Substitute.For<ILogger<GetAnalyseScenarioQueryHandler>>();
        _handler = new GetAnalyseScenarioQueryHandler(_repository, logger);
    }

    [Test]
    public async Task Should_Return_Null_When_Not_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        _repository.Get(scenarioId).Returns((AnalyseScenario?)null);
        var query = new GetAnalyseScenarioQuery(scenarioId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task Should_Return_Mapped_Scenario_When_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var scenario = new AnalyseScenario
        {
            Id = scenarioId,
            Name = "Found Scenario",
            Description = "Some description",
            GroupId = Guid.NewGuid(),
            FromDate = new DateOnly(2026, 5, 1),
            UntilDate = new DateOnly(2026, 5, 31),
            Token = token,
            CreatedByUser = "tester",
            Status = AnalyseScenarioStatus.Accepted
        };
        _repository.Get(scenarioId).Returns(scenario);
        var query = new GetAnalyseScenarioQuery(scenarioId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(scenarioId);
        result.Name.Should().Be("Found Scenario");
        result.Token.Should().Be(token);
        result.Status.Should().Be((int)AnalyseScenarioStatus.Accepted);
    }
}

[TestFixture]
public class AcceptAnalyseScenarioCommandHandlerTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private AcceptAnalyseScenarioCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();

        var logger = Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>();
        _handler = new AcceptAnalyseScenarioCommandHandler(_repository, _scenarioService, _unitOfWork, logger);
    }

    [Test]
    public async Task Should_Throw_When_Scenario_Not_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        _repository.Get(scenarioId).Returns((AnalyseScenario?)null);
        var command = new AcceptAnalyseScenarioCommand(scenarioId);

        // Act
        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"AnalyseScenario with ID {scenarioId} not found");
    }

    [Test]
    public async Task Should_Not_Call_UnitOfWork_When_Scenario_Not_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        _repository.Get(scenarioId).Returns((AnalyseScenario?)null);
        var command = new AcceptAnalyseScenarioCommand(scenarioId);

        // Act
        try { await _handler.Handle(command, CancellationToken.None); } catch { }

        // Assert
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}

[TestFixture]
public class RejectAnalyseScenarioCommandHandlerTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private RejectAnalyseScenarioCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();

        var logger = Substitute.For<ILogger<RejectAnalyseScenarioCommandHandler>>();
        _handler = new RejectAnalyseScenarioCommandHandler(_repository, _scenarioService, _unitOfWork, logger);
    }

    [Test]
    public async Task Should_Throw_When_Scenario_Not_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        _repository.Get(scenarioId).Returns((AnalyseScenario?)null);
        var command = new RejectAnalyseScenarioCommand(scenarioId);

        // Act
        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"AnalyseScenario with ID {scenarioId} not found");
    }

    [Test]
    public async Task Should_Not_Call_UnitOfWork_When_Scenario_Not_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        _repository.Get(scenarioId).Returns((AnalyseScenario?)null);
        var command = new RejectAnalyseScenarioCommand(scenarioId);

        // Act
        try { await _handler.Handle(command, CancellationToken.None); } catch { }

        // Assert
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}

[TestFixture]
public class DeleteAnalyseScenarioCommandHandlerTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private DeleteAnalyseScenarioCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();

        var logger = Substitute.For<ILogger<DeleteAnalyseScenarioCommandHandler>>();
        _handler = new DeleteAnalyseScenarioCommandHandler(_repository, _scenarioService, _unitOfWork, logger);
    }

    [Test]
    public async Task Should_Throw_When_Scenario_Not_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        _repository.Get(scenarioId).Returns((AnalyseScenario?)null);
        var command = new DeleteAnalyseScenarioCommand(scenarioId);

        // Act
        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"AnalyseScenario with ID {scenarioId} not found");
    }

    [Test]
    public async Task Should_Not_Call_UnitOfWork_When_Scenario_Not_Found()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        _repository.Get(scenarioId).Returns((AnalyseScenario?)null);
        var command = new DeleteAnalyseScenarioCommand(scenarioId);

        // Act
        try { await _handler.Handle(command, CancellationToken.None); } catch { }

        // Assert
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task Should_Delete_And_Complete_When_Scenario_Exists()
    {
        // Arrange
        var scenarioId = Guid.NewGuid();
        var scenario = new AnalyseScenario
        {
            Id = scenarioId,
            Name = "To Delete",
            Token = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            FromDate = new DateOnly(2026, 6, 1),
            UntilDate = new DateOnly(2026, 6, 30),
            Status = AnalyseScenarioStatus.Active
        };
        _repository.Get(scenarioId).Returns(scenario);
        var command = new DeleteAnalyseScenarioCommand(scenarioId);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        await _repository.Received(1).Delete(scenarioId);
        await _unitOfWork.Received(1).CompleteAsync();
    }
}

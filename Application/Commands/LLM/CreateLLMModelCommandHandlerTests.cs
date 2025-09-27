using NUnit.Framework;
using NSubstitute;
using AutoMapper;
using Klacks.Api.Application.Handlers.LLM;
using Klacks.Api.Application.Commands.LLM;
using Klacks.Api.Domain.Models.LLM;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Exceptions;
using UnitTest.Base;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UnitTest.Application.Commands.LLM;

[TestFixture]
public class CreateLLMModelCommandHandlerTests : BaseCommandHandlerTests<CreateLLMModelCommandHandler, CreateLLMModelCommand, LLMModel, LLMModel>
{
    private ILLMRepository _mockLLMRepository;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        _mockLLMRepository = Substitute.For<ILLMRepository>();
        MockUnitOfWork.LLMRepository.Returns(_mockLLMRepository);
    }

    protected override CreateLLMModelCommandHandler CreateHandler()
    {
        return new CreateLLMModelCommandHandler(MockUnitOfWork, MockMapper, MockLogger);
    }

    protected override CreateLLMModelCommand CreateValidCommand()
    {
        return new CreateLLMModelCommand
        {
            Resource = new LLMModel
            {
                ModelId = "test-model-1",
                DisplayName = "Test Model 1",
                ProviderId = "openai",
                ApiModelId = "gpt-3.5-turbo",
                ContextWindow = 4096,
                MaxTokens = 4096,
                CostPerInputToken = 0.001m,
                CostPerOutputToken = 0.002m,
                IsEnabled = true,
                IsDefault = false
            },
            ProviderApiKey = "sk-test-api-key-123456789"
        };
    }

    protected override LLMModel CreateValidEntity()
    {
        var entity = base.CreateValidEntity();
        entity.ModelId = "test-model-1";
        entity.DisplayName = "Test Model 1";
        entity.ProviderId = "openai";
        entity.IsEnabled = true;
        return entity;
    }

    protected override async Task<LLMModel> ExecuteHandler(CreateLLMModelCommand command)
    {
        if (command == null)
        {
            return await Handler.Handle(null, CancellationToken.None);
        }
        return await Handler.Handle(command, CancellationToken.None);
    }

    protected override void SetupMockExpectations(CreateLLMModelCommand command, LLMModel expectedResult)
    {
        var createdModel = CreateValidEntity();
        _mockLLMRepository.CreateModelAsync(Arg.Any<LLMModel>()).Returns(createdModel);
        _mockLLMRepository.UpdateProviderApiKeyAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
    }

    protected override async Task VerifyMockInteractions()
    {
        await _mockLLMRepository.Received(1).CreateModelAsync(Arg.Any<LLMModel>());
        await _mockLLMRepository.Received(1).UpdateProviderApiKeyAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_ValidModelWithApiKey_ShouldCreateModelAndUpdateProvider()
    {
        // Arrange
        var command = CreateValidCommand();
        var createdModel = CreateValidEntity();
        
        _mockLLMRepository.CreateModelAsync(Arg.Any<LLMModel>()).Returns(createdModel);
        _mockLLMRepository.UpdateProviderApiKeyAsync(command.Resource.ProviderId, command.ProviderApiKey)
                         .Returns(Task.CompletedTask);

        // Act
        var result = await Handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ModelId, Is.EqualTo(createdModel.ModelId));
        
        await _mockLLMRepository.Received(1).CreateModelAsync(Arg.Is<LLMModel>(m => 
            m.ModelId == command.Resource.ModelId &&
            m.ProviderId == command.Resource.ProviderId));
        
        await _mockLLMRepository.Received(1).UpdateProviderApiKeyAsync(
            command.Resource.ProviderId, 
            command.ProviderApiKey);
    }

    [Test]
    public async Task Handle_ValidModelWithoutApiKey_ShouldCreateModelOnly()
    {
        // Arrange
        var command = CreateValidCommand();
        command.ProviderApiKey = null;
        var createdModel = CreateValidEntity();
        
        _mockLLMRepository.CreateModelAsync(Arg.Any<LLMModel>()).Returns(createdModel);

        // Act
        var result = await Handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        
        await _mockLLMRepository.Received(1).CreateModelAsync(Arg.Any<LLMModel>());
        await _mockLLMRepository.DidNotReceive().UpdateProviderApiKeyAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_DuplicateModelId_ShouldThrowInvalidRequestException()
    {
        // Arrange
        var command = CreateValidCommand();
        _mockLLMRepository.CreateModelAsync(Arg.Any<LLMModel>())
                         .ThrowsAsync(new InvalidRequestException("Model with this ID already exists"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => Handler.Handle(command, CancellationToken.None));
        
        Assert.That(ex.Message, Does.Contain("already exists"));
    }

    [Test]
    public async Task Handle_InvalidProvider_ShouldThrowInvalidRequestException()
    {
        // Arrange
        var command = CreateValidCommand();
        command.Resource.ProviderId = "invalid-provider";
        
        _mockLLMRepository.CreateModelAsync(Arg.Any<LLMModel>())
                         .ThrowsAsync(new InvalidRequestException("Invalid provider"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => Handler.Handle(command, CancellationToken.None));
        
        Assert.That(ex.Message, Does.Contain("Invalid provider"));
    }

    [Test]
    public async Task Handle_EmptyModelId_ShouldThrowArgumentException()
    {
        // Arrange
        var command = CreateValidCommand();
        command.Resource.ModelId = "";

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Handler.Handle(command, CancellationToken.None));
        
        Assert.That(ex.Message, Does.Contain("ModelId").IgnoreCase);
    }

    [Test]
    public async Task Handle_NegativeCost_ShouldThrowArgumentException()
    {
        // Arrange
        var command = CreateValidCommand();
        command.Resource.CostPerInputToken = -0.001m;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Handler.Handle(command, CancellationToken.None));
        
        Assert.That(ex.Message, Does.Contain("cost").IgnoreCase);
    }

    [Test]
    public async Task Handle_InvalidContextWindow_ShouldThrowArgumentException()
    {
        // Arrange
        var command = CreateValidCommand();
        command.Resource.ContextWindow = 0;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Handler.Handle(command, CancellationToken.None));
        
        Assert.That(ex.Message, Does.Contain("context").IgnoreCase);
    }
}
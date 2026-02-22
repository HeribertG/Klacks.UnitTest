using FluentAssertions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class CreateLLMModelCommandHandlerTests
{
    private ILLMRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ITransaction _transaction = null!;
    private CreateLLMModelCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ILLMRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _transaction = Substitute.For<ITransaction>();
        _unitOfWork.BeginTransactionAsync().Returns(_transaction);

        var logger = Substitute.For<ILogger<CreateLLMModelCommandHandler>>();
        _handler = new CreateLLMModelCommandHandler(_repository, _unitOfWork, logger);
    }

    [TearDown]
    public void TearDown()
    {
        _transaction.Dispose();
    }

    [Test]
    public async Task Handle_WhenIsDefaultTrue_ShouldResetOtherDefaults()
    {
        var model = new LLMModel { ModelId = "new-model", IsDefault = true };
        _repository.GetModelByIdAsync("new-model").Returns((LLMModel?)null);
        _repository.CreateModelAsync(Arg.Any<LLMModel>()).Returns(model);

        await _handler.Handle(new PostCommand<LLMModel>(model), CancellationToken.None);

        await _repository.Received(1).SetDefaultModelAsync("new-model");
        await _repository.Received(1).CreateModelAsync(model);
    }

    [Test]
    public async Task Handle_WhenIsDefaultFalse_ShouldNotResetOtherDefaults()
    {
        var model = new LLMModel { ModelId = "new-model", IsDefault = false };
        _repository.GetModelByIdAsync("new-model").Returns((LLMModel?)null);
        _repository.CreateModelAsync(Arg.Any<LLMModel>()).Returns(model);

        await _handler.Handle(new PostCommand<LLMModel>(model), CancellationToken.None);

        await _repository.DidNotReceive().SetDefaultModelAsync(Arg.Any<string>());
        await _repository.Received(1).CreateModelAsync(model);
    }
}

[TestFixture]
public class UpdateLLMModelCommandHandlerTests
{
    private ILLMRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ITransaction _transaction = null!;
    private UpdateLLMModelCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ILLMRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _transaction = Substitute.For<ITransaction>();
        _unitOfWork.BeginTransactionAsync().Returns(_transaction);

        var logger = Substitute.For<ILogger<UpdateLLMModelCommandHandler>>();
        _handler = new UpdateLLMModelCommandHandler(_repository, _unitOfWork, logger);
    }

    [TearDown]
    public void TearDown()
    {
        _transaction.Dispose();
    }

    [Test]
    public async Task Handle_WhenSettingIsDefaultTrue_ShouldResetOtherDefaults()
    {
        var existingId = Guid.NewGuid();
        var existing = new LLMModel { Id = existingId, ModelId = "model-1", IsDefault = false };
        var updated = new LLMModel { Id = existingId, ModelId = "model-1", IsDefault = true, ModelName = "Updated" };

        _repository.Get(existingId).Returns(existing);
        _repository.UpdateModelAsync(Arg.Any<LLMModel>()).Returns(existing);

        await _handler.Handle(new PutCommand<LLMModel>(updated), CancellationToken.None);

        await _repository.Received(1).SetDefaultModelAsync("model-1");
    }

    [Test]
    public async Task Handle_WhenAlreadyDefault_ShouldNotCallSetDefault()
    {
        var existingId = Guid.NewGuid();
        var existing = new LLMModel { Id = existingId, ModelId = "model-1", IsDefault = true };
        var updated = new LLMModel { Id = existingId, ModelId = "model-1", IsDefault = true, ModelName = "Updated" };

        _repository.Get(existingId).Returns(existing);
        _repository.UpdateModelAsync(Arg.Any<LLMModel>()).Returns(existing);

        await _handler.Handle(new PutCommand<LLMModel>(updated), CancellationToken.None);

        await _repository.DidNotReceive().SetDefaultModelAsync(Arg.Any<string>());
    }

    [Test]
    public async Task Handle_WhenIsDefaultFalse_ShouldNotResetOtherDefaults()
    {
        var existingId = Guid.NewGuid();
        var existing = new LLMModel { Id = existingId, ModelId = "model-1", IsDefault = false };
        var updated = new LLMModel { Id = existingId, ModelId = "model-1", IsDefault = false, ModelName = "Updated" };

        _repository.Get(existingId).Returns(existing);
        _repository.UpdateModelAsync(Arg.Any<LLMModel>()).Returns(existing);

        await _handler.Handle(new PutCommand<LLMModel>(updated), CancellationToken.None);

        await _repository.DidNotReceive().SetDefaultModelAsync(Arg.Any<string>());
    }

    [Test]
    public async Task Handle_WhenModelNotFound_ShouldThrowKeyNotFoundException()
    {
        var id = Guid.NewGuid();
        var updated = new LLMModel { Id = id, ModelId = "model-1", IsDefault = true };
        _repository.Get(id).Returns((LLMModel?)null);

        Func<Task> act = async () => await _handler.Handle(new PutCommand<LLMModel>(updated), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().SetDefaultModelAsync(Arg.Any<string>());
    }
}

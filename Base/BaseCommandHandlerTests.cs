using NUnit.Framework;
using NSubstitute;
using AutoMapper;
using Klacks.Api.Application.Handlers.Base;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Base;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTest.Base;

public abstract class BaseCommandHandlerTests<THandler, TCommand, TEntity, TResult>
    where THandler : class
    where TCommand : class  
    where TEntity : BaseEntity
    where TResult : class
{
    protected IUnitOfWork MockUnitOfWork { get; private set; }
    protected IMapper MockMapper { get; private set; }
    protected ILogger<THandler> MockLogger { get; private set; }
    protected THandler Handler { get; private set; }

    [SetUp]
    public virtual void Setup()
    {
        MockUnitOfWork = Substitute.For<IUnitOfWork>();
        MockMapper = Substitute.For<IMapper>();
        MockLogger = Substitute.For<ILogger<THandler>>();

        Handler = CreateHandler();
    }

    protected abstract THandler CreateHandler();
    
    protected virtual TCommand CreateValidCommand()
    {
        return Activator.CreateInstance<TCommand>();
    }

    protected virtual TEntity CreateValidEntity()
    {
        var entity = Activator.CreateInstance<TEntity>();
        entity.Id = Guid.NewGuid();
        entity.CreateTime = DateTime.UtcNow;
        return entity;
    }

    protected virtual TResult CreateExpectedResult()
    {
        return Activator.CreateInstance<TResult>();
    }

    [Test]
    public virtual async Task Handle_ValidCommand_ShouldSucceed()
    {
        // Arrange
        var command = CreateValidCommand();
        var expectedResult = CreateExpectedResult();
        
        SetupMockExpectations(command, expectedResult);

        // Act
        var result = await ExecuteHandler(command);

        // Assert
        Assert.That(result, Is.Not.Null);
        await VerifyMockInteractions();
    }

    [Test]
    public virtual async Task Handle_NullCommand_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExecuteHandler(null));
        
        Assert.That(ex.ParamName, Is.Not.Null);
    }

    protected abstract Task<TResult> ExecuteHandler(TCommand command);
    protected abstract void SetupMockExpectations(TCommand command, TResult expectedResult);
    protected abstract Task VerifyMockInteractions();

    [TearDown]
    public virtual void TearDown()
    {
        MockUnitOfWork?.ClearSubstitute();
        MockMapper?.ClearSubstitute();
        MockLogger?.ClearSubstitute();
    }
}
using NUnit.Framework;
using NSubstitute;
using AutoMapper;
using Klacks.Api.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTest.Base;

public abstract class BaseQueryHandlerTests<THandler, TQuery, TResult>
    where THandler : class
    where TQuery : class
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
    
    protected virtual TQuery CreateValidQuery()
    {
        return Activator.CreateInstance<TQuery>();
    }

    protected virtual TResult CreateExpectedResult()
    {
        return Activator.CreateInstance<TResult>();
    }

    protected virtual IEnumerable<TResult> CreateExpectedResults()
    {
        return new List<TResult> { CreateExpectedResult() };
    }

    [Test]
    public virtual async Task Handle_ValidQuery_ShouldReturnResult()
    {
        // Arrange
        var query = CreateValidQuery();
        var expectedResult = CreateExpectedResult();
        
        SetupMockExpectations(query, expectedResult);

        // Act
        var result = await ExecuteHandler(query);

        // Assert
        Assert.That(result, Is.Not.Null);
        await VerifyMockInteractions();
    }

    [Test]
    public virtual async Task Handle_NullQuery_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExecuteHandler(null));
        
        Assert.That(ex.ParamName, Is.Not.Null);
    }

    protected abstract Task<TResult> ExecuteHandler(TQuery query);
    protected abstract void SetupMockExpectations(TQuery query, TResult expectedResult);
    protected abstract Task VerifyMockInteractions();

    [TearDown]
    public virtual void TearDown()
    {
        MockUnitOfWork?.ClearSubstitute();
        MockMapper?.ClearSubstitute();
        MockLogger?.ClearSubstitute();
    }
}
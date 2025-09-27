using NUnit.Framework;
using NSubstitute;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klacks.Api.Domain.Models.LLM;
using Klacks.Api.Domain.Services.LLM.Providers;

namespace UnitTest.Base;

public abstract class BaseLLMProviderTests<TProvider>
    where TProvider : class, ILLMProvider
{
    protected HttpClient MockHttpClient { get; private set; }
    protected ILogger<TProvider> MockLogger { get; private set; }
    protected TProvider Provider { get; private set; }

    [SetUp]
    public virtual void Setup()
    {
        MockLogger = Substitute.For<ILogger<TProvider>>();
        MockHttpClient = CreateMockHttpClient();
        Provider = CreateProvider();
    }

    protected abstract TProvider CreateProvider();
    
    protected virtual HttpClient CreateMockHttpClient()
    {
        var mockHandler = Substitute.For<HttpMessageHandler>();
        return new HttpClient(mockHandler);
    }

    protected virtual LLMProviderRequest CreateValidRequest()
    {
        return new LLMProviderRequest
        {
            Messages = new[]
            {
                new LLMMessage
                {
                    Role = "user",
                    Content = "Test message"
                }
            },
            MaxTokens = 100,
            Temperature = 0.7,
            Model = GetDefaultModelName()
        };
    }

    protected virtual LLMProviderResponse CreateExpectedResponse()
    {
        return new LLMProviderResponse
        {
            Content = "Test response",
            Usage = new LLMUsage
            {
                InputTokens = 10,
                OutputTokens = 15,
                TotalTokens = 25
            },
            Model = GetDefaultModelName(),
            FinishReason = "stop"
        };
    }

    protected abstract string GetDefaultModelName();
    protected abstract string GetProviderName();

    [Test]
    public virtual async Task ProcessMessageAsync_ValidRequest_ShouldReturnResponse()
    {
        // Arrange
        var request = CreateValidRequest();
        var expectedResponse = CreateExpectedResponse();
        
        SetupHttpClientMockResponse(expectedResponse);

        // Act
        var result = await Provider.ProcessMessageAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Content, Is.Not.Empty);
        Assert.That(result.Usage, Is.Not.Null);
        Assert.That(result.Usage.TotalTokens, Is.GreaterThan(0));
    }

    [Test]
    public virtual async Task ProcessMessageAsync_EmptyMessage_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidRequest();
        request.Messages = Array.Empty<LLMMessage>();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider.ProcessMessageAsync(request));
        
        Assert.That(ex.Message, Does.Contain("message").IgnoreCase);
    }

    [Test]
    public virtual async Task ProcessMessageAsync_NullRequest_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => Provider.ProcessMessageAsync(null));
        
        Assert.That(ex.ParamName, Is.EqualTo("request"));
    }

    [Test]
    public virtual async Task ProcessMessageAsync_HttpError_ShouldThrowHttpRequestException()
    {
        // Arrange
        var request = CreateValidRequest();
        SetupHttpClientMockError(HttpStatusCode.Unauthorized);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider.ProcessMessageAsync(request));
        
        Assert.That(ex.Message, Does.Contain("401").Or.Contain("Unauthorized"));
    }

    [Test]
    public void ProviderName_ShouldReturnCorrectName()
    {
        // Act
        var providerName = Provider.ProviderName;

        // Assert
        Assert.That(providerName, Is.EqualTo(GetProviderName()));
    }

    [Test]
    public void SupportedModels_ShouldNotBeEmpty()
    {
        // Act
        var supportedModels = Provider.SupportedModels;

        // Assert
        Assert.That(supportedModels, Is.Not.Null);
        Assert.That(supportedModels, Is.Not.Empty);
        Assert.That(supportedModels, Does.Contain(GetDefaultModelName()));
    }

    protected abstract void SetupHttpClientMockResponse(LLMProviderResponse expectedResponse);
    protected abstract void SetupHttpClientMockError(HttpStatusCode statusCode);

    [TearDown]
    public virtual void TearDown()
    {
        MockHttpClient?.Dispose();
        MockLogger?.ClearSubstitute();
    }
}
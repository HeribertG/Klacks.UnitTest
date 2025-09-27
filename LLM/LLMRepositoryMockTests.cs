using NUnit.Framework;
using NSubstitute;
using Klacks.Api.Domain.Models.LLM;
using Klacks.Api.Domain.Interfaces;

namespace UnitTest.LLM;

[TestFixture]
public class LLMRepositoryMockTests
{
    private ILLMRepository _mockRepository = null!;

    [SetUp]
    public void Setup()
    {
        _mockRepository = Substitute.For<ILLMRepository>();
    }

    [Test]
    public async Task Repository_Get_ShouldReturnModel()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var expectedModel = new LLMModel 
        { 
            Id = modelId, 
            ModelId = "test-model",
            ModelName = "Test Model" 
        };
        _mockRepository.Get(modelId).Returns(Task.FromResult<LLMModel?>(expectedModel));

        // Act
        var result = await _mockRepository.Get(modelId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo(modelId));
        Assert.That(result.ModelId, Is.EqualTo("test-model"));
        await _mockRepository.Received(1).Get(modelId);
    }

    [Test]
    public async Task Repository_List_ShouldReturnModels()
    {
        // Arrange
        var models = new List<LLMModel>
        {
            new() { ModelId = "model1", ModelName = "Model 1" },
            new() { ModelId = "model2", ModelName = "Model 2" }
        };
        _mockRepository.List().Returns(Task.FromResult(models));

        // Act
        var result = await _mockRepository.List();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.First().ModelId, Is.EqualTo("model1"));
        await _mockRepository.Received(1).List();
    }

    [Test]
    public async Task Repository_GetModelsAsync_ShouldWork()
    {
        // Arrange
        var models = new List<LLMModel>
        {
            new() { ModelId = "enabled-model", IsEnabled = true },
            new() { ModelId = "disabled-model", IsEnabled = false }
        };
        _mockRepository.GetModelsAsync(true).Returns(Task.FromResult(models.Where(m => m.IsEnabled).ToList()));

        // Act
        var result = await _mockRepository.GetModelsAsync(true);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result.First().ModelId, Is.EqualTo("enabled-model"));
        await _mockRepository.Received(1).GetModelsAsync(true);
    }

    [Test]
    public async Task Repository_GetDefaultModelAsync_ShouldReturnDefaultModel()
    {
        // Arrange
        var defaultModel = new LLMModel 
        { 
            ModelId = "default-model", 
            IsDefault = true,
            IsEnabled = true 
        };
        _mockRepository.GetDefaultModelAsync().Returns(Task.FromResult<LLMModel?>(defaultModel));

        // Act
        var result = await _mockRepository.GetDefaultModelAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsDefault, Is.True);
        Assert.That(result.ModelId, Is.EqualTo("default-model"));
        await _mockRepository.Received(1).GetDefaultModelAsync();
    }

    [Test]
    public async Task Repository_TrackUsageAsync_ShouldReturnUsage()
    {
        // Arrange
        var usage = new LLMUsage
        {
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.01m,
            ModelId = Guid.NewGuid(),
            UserId = "user-123",
            ConversationId = "conv-456"
        };
        _mockRepository.TrackUsageAsync(usage).Returns(Task.FromResult(usage));

        // Act
        var result = await _mockRepository.TrackUsageAsync(usage);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalTokens, Is.EqualTo(150));
        Assert.That(result.Cost, Is.EqualTo(0.01m));
        await _mockRepository.Received(1).TrackUsageAsync(usage);
    }
}
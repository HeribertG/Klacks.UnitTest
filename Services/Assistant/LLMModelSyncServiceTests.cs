// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Unit tests for LLMModelSyncService, covering insert, disable, skip-if-already-disabled,
 * null-discovery skip, and notification creation scenarios.
 */
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class LLMModelSyncServiceTests
{
    private ILLMRepository _repo = null!;
    private ILLMProviderFactory _factory = null!;
    private LLMModelSyncService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = Substitute.For<ILLMRepository>();
        _factory = Substitute.For<ILLMProviderFactory>();
        _sut = new LLMModelSyncService(_repo, _factory, NullLogger<LLMModelSyncService>.Instance);
    }

    [Test]
    public async Task SyncAllProvidersAsync_NewModelFromApi_InsertsEnabledModel()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>(
                [new LLMModelDiscovery("gpt-5-new", "GPT-5 New")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(c =>
            Task.FromResult(new LLMModelTestResult(c.Arg<string>(), c.Arg<string>(), true, null, 100)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsAsync(false).Returns([]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        LLMModel? inserted = null;
        _repo.CreateModelAsync(Arg.Do<LLMModel>(m => inserted = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        inserted.Should().NotBeNull();
        inserted!.ApiModelId.Should().Be("gpt-5-new");
        inserted.IsEnabled.Should().BeTrue();
        inserted.ProviderId.Should().Be("openai");
    }

    [Test]
    public async Task SyncAllProvidersAsync_MissingModelInApi_DisablesModel()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-4", "GPT-4")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(c =>
            Task.FromResult(new LLMModelTestResult(c.Arg<string>(), c.Arg<string>(), true, null, 100)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        var existingModel = new LLMModel
        {
            ModelId = "gpt-3",
            ApiModelId = "gpt-3",
            ProviderId = "openai",
            IsEnabled = true,
            ModelName = "GPT-3"
        };
        _repo.GetModelsAsync(false).Returns([existingModel]);

        LLMModel? updated = null;
        _repo.UpdateModelAsync(Arg.Do<LLMModel>(m => updated = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        updated.Should().NotBeNull();
        updated!.ApiModelId.Should().Be("gpt-3");
        updated.IsEnabled.Should().BeFalse();
    }

    [Test]
    public async Task SyncAllProvidersAsync_DisabledModelMissingFromApi_DoesNotUpdate()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-4", "GPT-4")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(c =>
            Task.FromResult(new LLMModelTestResult(c.Arg<string>(), c.Arg<string>(), true, null, 100)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);

        var disabledModel = new LLMModel
        {
            ModelId = "gpt-3",
            ApiModelId = "gpt-3",
            ProviderId = "openai",
            IsEnabled = false,
            ModelName = "GPT-3"
        };
        _repo.GetModelsAsync(false).Returns([disabledModel]);

        await _sut.SyncAllProvidersAsync();

        await _repo.DidNotReceive().UpdateModelAsync(Arg.Any<LLMModel>());
    }

    [Test]
    public async Task SyncAllProvidersAsync_ProviderReturnsNull_SkipsProvider()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("azure");
        provider.ProviderName.Returns("Azure");
        provider.GetAvailableModelsAsync().Returns(Task.FromResult<List<LLMModelDiscovery>?>(null));
        provider.TestModelAsync(Arg.Any<string>()).Returns(c =>
            Task.FromResult(new LLMModelTestResult(c.Arg<string>(), c.Arg<string>(), true, null, 100)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsAsync(false).Returns([]);

        await _sut.SyncAllProvidersAsync();

        await _repo.DidNotReceive().CreateModelAsync(Arg.Any<LLMModel>());
    }

    [Test]
    public async Task SyncAllProvidersAsync_HasChanges_SavesNotification()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>(
                [new LLMModelDiscovery("gpt-5-new", "GPT-5 New")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(c =>
            Task.FromResult(new LLMModelTestResult(c.Arg<string>(), c.Arg<string>(), true, null, 100)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsAsync(false).Returns([]);

        _repo.CreateModelAsync(Arg.Any<LLMModel>())
             .Returns(c => c.Arg<LLMModel>());

        LLMSyncNotification? savedNotification = null;
        _repo.CreateSyncNotificationAsync(Arg.Do<LLMSyncNotification>(n => savedNotification = n))
             .Returns(c => c.Arg<LLMSyncNotification>());

        await _sut.SyncAllProvidersAsync();

        savedNotification.Should().NotBeNull();
        savedNotification!.ProviderId.Should().Be("openai");
        savedNotification.NewModelsCount.Should().Be(1);
        savedNotification.NewModelNames.Should().Contain("GPT-5 New");
    }

    [Test]
    public async Task SyncAllProvidersAsync_NewModel_PassesTest_InsertsEnabled()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-5", "GPT-5")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(
            Task.FromResult(new LLMModelTestResult("gpt-5", "GPT-5", true, null, 200)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsAsync(false).Returns([]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        LLMModel? inserted = null;
        _repo.CreateModelAsync(Arg.Do<LLMModel>(m => inserted = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        inserted.Should().NotBeNull();
        inserted!.IsEnabled.Should().BeTrue();
    }

    [Test]
    public async Task SyncAllProvidersAsync_NewModel_FailsTest_InsertsDisabled()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-oss", "GPT-OSS")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(
            Task.FromResult(new LLMModelTestResult("gpt-oss", "GPT-OSS", false, "404 model not found", 150)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsAsync(false).Returns([]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        LLMModel? inserted = null;
        _repo.CreateModelAsync(Arg.Do<LLMModel>(m => inserted = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        inserted.Should().NotBeNull();
        inserted!.IsEnabled.Should().BeFalse();
    }

    [Test]
    public async Task SyncAllProvidersAsync_NewModel_FailsTest_NotificationHasFailedCount()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-oss", "GPT-OSS")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(
            Task.FromResult(new LLMModelTestResult("gpt-oss", "GPT-OSS", false, "404 model not found", 150)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsAsync(false).Returns([]);
        _repo.CreateModelAsync(Arg.Any<LLMModel>()).Returns(c => c.Arg<LLMModel>());

        LLMSyncNotification? notification = null;
        _repo.CreateSyncNotificationAsync(Arg.Do<LLMSyncNotification>(n => notification = n))
             .Returns(c => c.Arg<LLMSyncNotification>());

        await _sut.SyncAllProvidersAsync();

        notification.Should().NotBeNull();
        notification!.FailedModelsCount.Should().Be(1);
        notification.ModelTestResults.Should().HaveCount(1);
        notification.ModelTestResults[0].Passed.Should().BeFalse();
        notification.ModelTestResults[0].ErrorMessage.Should().Be("404 model not found");
    }
}

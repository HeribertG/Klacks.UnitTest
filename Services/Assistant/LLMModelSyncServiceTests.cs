// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Unit tests for LLMModelSyncService, covering insert, soft-delete-on-disappearance,
 * restore-on-reappearance, empty-discovery guard, default-model-skip,
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
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        LLMModel? inserted = null;
        _repo.CreateModelAsync(Arg.Do<LLMModel>(m => inserted = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        inserted.ShouldNotBeNull();
        inserted!.ApiModelId.ShouldBe("gpt-5-new");
        inserted.IsEnabled.ShouldBeTrue();
        inserted.ProviderId.ShouldBe("openai");
    }

    [Test]
    public async Task SyncAllProvidersAsync_MissingModelInApi_SoftDeletesModel()
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
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([existingModel]);

        LLMModel? updated = null;
        _repo.UpdateModelAsync(Arg.Do<LLMModel>(m => updated = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        updated.ShouldNotBeNull();
        updated!.ApiModelId.ShouldBe("gpt-3");
        updated.IsEnabled.ShouldBeFalse();
        updated.IsDeleted.ShouldBeTrue();
        updated.DeletedTime.ShouldNotBeNull();
    }

    [Test]
    public async Task SyncAllProvidersAsync_DisabledModelMissingFromApi_SoftDeletesModel()
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

        var disabledModel = new LLMModel
        {
            ModelId = "gpt-3",
            ApiModelId = "gpt-3",
            ProviderId = "openai",
            IsEnabled = false,
            ModelName = "GPT-3"
        };
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([disabledModel]);

        LLMModel? updated = null;
        _repo.UpdateModelAsync(Arg.Do<LLMModel>(m => updated = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        updated.ShouldNotBeNull();
        updated!.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task SyncAllProvidersAsync_AlreadyDeletedModelStillMissing_DoesNotUpdate()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-4", "GPT-4")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(c =>
            Task.FromResult(new LLMModelTestResult(c.Arg<string>(), c.Arg<string>(), true, null, 100)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.CreateModelAsync(Arg.Any<LLMModel>()).Returns(c => c.Arg<LLMModel>());
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        var deletedModel = new LLMModel
        {
            ModelId = "gpt-3",
            ApiModelId = "gpt-3",
            ProviderId = "openai",
            IsEnabled = false,
            IsDeleted = true,
            ModelName = "GPT-3"
        };
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([deletedModel]);

        await _sut.SyncAllProvidersAsync();

        // gpt-4 is a genuinely new model and is expected to be created;
        // the already-deleted gpt-3 row must not be touched (no restore, no re-delete).
        await _repo.DidNotReceive().UpdateModelAsync(Arg.Any<LLMModel>());
    }

    [Test]
    public async Task SyncAllProvidersAsync_SoftDeletedModelReappears_RestoresInsteadOfDuplicating()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("google");
        provider.ProviderName.Returns("Google");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gemini-2.5-pro", "Gemini 2.5 Pro")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(c =>
            Task.FromResult(new LLMModelTestResult(c.Arg<string>(), c.Arg<string>(), true, null, 100)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        var previouslyDeletedId = Guid.NewGuid();
        var deletedModel = new LLMModel
        {
            Id = previouslyDeletedId,
            ModelId = "gemini-25-pro",
            ApiModelId = "gemini-2.5-pro",
            ProviderId = "google",
            IsEnabled = false,
            IsDeleted = true,
            DeletedTime = DateTime.UtcNow.AddDays(-3),
            ModelName = "Gemini 2.5 Pro"
        };
        _repo.GetModelsByProviderIncludingDeletedAsync("google").Returns([deletedModel]);

        LLMModel? updated = null;
        _repo.UpdateModelAsync(Arg.Do<LLMModel>(m => updated = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        await _repo.DidNotReceive().CreateModelAsync(Arg.Any<LLMModel>());
        updated.ShouldNotBeNull();
        updated!.Id.ShouldBe(previouslyDeletedId);
        updated.IsDeleted.ShouldBeFalse();
        updated.DeletedTime.ShouldBeNull();
        updated.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public async Task SyncAllProvidersAsync_EmptyDiscoveryList_DoesNotDeleteExistingModels()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([]));

        _factory.GetEnabledProvidersAsync().Returns([provider]);

        var existingModel = new LLMModel
        {
            ModelId = "gpt-3",
            ApiModelId = "gpt-3",
            ProviderId = "openai",
            IsEnabled = true,
            ModelName = "GPT-3"
        };
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([existingModel]);

        await _sut.SyncAllProvidersAsync();

        await _repo.DidNotReceive().UpdateModelAsync(Arg.Any<LLMModel>());
    }

    [Test]
    public async Task SyncAllProvidersAsync_DefaultModelMissingFromApi_SkipsSoftDelete()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-4", "GPT-4")]));

        _factory.GetEnabledProvidersAsync().Returns([provider]);

        var defaultModel = new LLMModel
        {
            ModelId = "gpt-3",
            ApiModelId = "gpt-3",
            ProviderId = "openai",
            IsEnabled = true,
            IsDefault = true,
            ModelName = "GPT-3"
        };
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([defaultModel]);

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
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([]);

        _repo.CreateModelAsync(Arg.Any<LLMModel>())
             .Returns(c => c.Arg<LLMModel>());

        LLMSyncNotification? savedNotification = null;
        _repo.CreateSyncNotificationAsync(Arg.Do<LLMSyncNotification>(n => savedNotification = n))
             .Returns(c => c.Arg<LLMSyncNotification>());

        await _sut.SyncAllProvidersAsync();

        savedNotification.ShouldNotBeNull();
        savedNotification!.ProviderId.ShouldBe("openai");
        savedNotification.NewModelsCount.ShouldBe(1);
        savedNotification.NewModelNames.ShouldContain("GPT-5 New");
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
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        LLMModel? inserted = null;
        _repo.CreateModelAsync(Arg.Do<LLMModel>(m => inserted = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        inserted.ShouldNotBeNull();
        inserted!.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public async Task SyncAllProvidersAsync_NewModel_FailsTest_InsertsAsDeleted()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("openai");
        provider.ProviderName.Returns("OpenAI");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gpt-oss", "GPT-OSS")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(
            Task.FromResult(new LLMModelTestResult("gpt-oss", "GPT-OSS", false, "404 model not found", 150)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        LLMModel? inserted = null;
        _repo.CreateModelAsync(Arg.Do<LLMModel>(m => inserted = m))
             .Returns(c => c.Arg<LLMModel>());

        await _sut.SyncAllProvidersAsync();

        inserted.ShouldNotBeNull();
        inserted!.IsEnabled.ShouldBeFalse();
        inserted.IsDeleted.ShouldBeTrue();
        inserted.DeletedTime.ShouldNotBeNull();
    }

    [Test]
    public async Task SyncAllProvidersAsync_ReappearingModel_FailsTest_StaysDeletedWithoutRewriteOrNotification()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("google");
        provider.ProviderName.Returns("Google");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>([new LLMModelDiscovery("gemini-2.0-flash", "Gemini 2.0 Flash")]));
        provider.TestModelAsync(Arg.Any<string>()).Returns(
            Task.FromResult(new LLMModelTestResult("gemini-2.0-flash", "Gemini 2.0 Flash", false,
                "This model models/gemini-2.0-flash is no longer available.", 300)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>())
             .Returns(c => c.Arg<LLMSyncNotification>());

        var deletedModel = new LLMModel
        {
            Id = Guid.NewGuid(),
            ModelId = "gemini-2-0-flash",
            ApiModelId = "gemini-2.0-flash",
            ProviderId = "google",
            IsEnabled = false,
            IsDeleted = true,
            DeletedTime = DateTime.UtcNow.AddDays(-1),
            ModelName = "Gemini 2.0 Flash"
        };
        _repo.GetModelsByProviderIncludingDeletedAsync("google").Returns([deletedModel]);

        await _sut.SyncAllProvidersAsync();

        // A provider that keeps advertising an already soft-deleted, still-failing model
        // must be a no-op: no re-insert, no DeletedTime re-stamp, and no duplicate
        // sync-log notification on every cycle.
        await _repo.DidNotReceive().CreateModelAsync(Arg.Any<LLMModel>());
        await _repo.DidNotReceive().UpdateModelAsync(Arg.Any<LLMModel>());
        await _repo.DidNotReceive().CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>());
    }

    [Test]
    public async Task SyncAllProvidersAsync_NewModelAndStillFailingDeletedModel_NotificationExcludesStaleModel()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns("google");
        provider.ProviderName.Returns("Google");
        provider.GetAvailableModelsAsync().Returns(
            Task.FromResult<List<LLMModelDiscovery>?>(
            [
                new LLMModelDiscovery("gemini-3.5-flash", "Gemini 3.5 Flash"),
                new LLMModelDiscovery("gemini-2.0-flash", "Gemini 2.0 Flash")
            ]));
        provider.TestModelAsync("gemini-3.5-flash").Returns(
            Task.FromResult(new LLMModelTestResult("gemini-3.5-flash", "Gemini 3.5 Flash", true, null, 100)));
        provider.TestModelAsync("gemini-2.0-flash").Returns(
            Task.FromResult(new LLMModelTestResult("gemini-2.0-flash", "Gemini 2.0 Flash", false,
                "This model models/gemini-2.0-flash is no longer available.", 300)));

        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.CreateModelAsync(Arg.Any<LLMModel>()).Returns(c => c.Arg<LLMModel>());

        var deletedModel = new LLMModel
        {
            Id = Guid.NewGuid(),
            ModelId = "gemini-2-0-flash",
            ApiModelId = "gemini-2.0-flash",
            ProviderId = "google",
            IsEnabled = false,
            IsDeleted = true,
            DeletedTime = DateTime.UtcNow.AddDays(-1),
            ModelName = "Gemini 2.0 Flash"
        };
        _repo.GetModelsByProviderIncludingDeletedAsync("google").Returns([deletedModel]);

        LLMSyncNotification? notification = null;
        _repo.CreateSyncNotificationAsync(Arg.Do<LLMSyncNotification>(n => notification = n))
             .Returns(c => c.Arg<LLMSyncNotification>());

        await _sut.SyncAllProvidersAsync();

        notification.ShouldNotBeNull();
        notification!.NewModelsCount.ShouldBe(1);
        notification.NewModelNames.ShouldContain("Gemini 3.5 Flash");
        notification.NewModelNames.ShouldNotContain("Gemini 2.0 Flash");
        notification.FailedModelsCount.ShouldBe(0);
        notification.ModelTestResults.Count().ShouldBe(1);
        notification.ModelTestResults[0].ApiModelId.ShouldBe("gemini-3.5-flash");
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
        _repo.GetModelsByProviderIncludingDeletedAsync("openai").Returns([]);
        _repo.CreateModelAsync(Arg.Any<LLMModel>()).Returns(c => c.Arg<LLMModel>());

        LLMSyncNotification? notification = null;
        _repo.CreateSyncNotificationAsync(Arg.Do<LLMSyncNotification>(n => notification = n))
             .Returns(c => c.Arg<LLMSyncNotification>());

        await _sut.SyncAllProvidersAsync();

        notification.ShouldNotBeNull();
        notification!.FailedModelsCount.ShouldBe(1);
        notification.ModelTestResults.Count().ShouldBe(1);
        notification.ModelTestResults[0].Passed.ShouldBeFalse();
        notification.ModelTestResults[0].ErrorMessage.ShouldBe("404 model not found");
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of LLMModelSyncService.SyncProviderAsync over one discovery list that hits every branch at once:
/// new passing, new failing and new non-chat models, a deleted model that reappears and passes, one that reappears and
/// still fails, an active one, and three missing ones (active, default, already deleted). Pins the inserted and updated
/// field values, the repository call order, the notification content, every rendered log line with its level, and the
/// no-change, empty-discovery, failing-provider and cancelled paths of SyncAllProvidersAsync.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class LLMModelSyncServiceCharacterizationTests
{
    private const string ProviderId = "openai";
    private const string ProviderName = "OpenAI";
    private const string RestoredParameters = "temperature";

    private ILLMRepository _repo = null!;
    private ILLMProviderFactory _factory = null!;
    private RecordingLogger<LLMModelSyncService> _logger = null!;
    private LLMModelSyncService _sut = null!;
    private List<LLMModel> _created = null!;
    private List<(string ApiModelId, bool IsDeleted, bool IsEnabled, DateTime? DeletedTime, string ModelName)> _updated = null!;
    private LLMSyncNotification? _notification;

    [SetUp]
    public void SetUp()
    {
        _repo = Substitute.For<ILLMRepository>();
        _factory = Substitute.For<ILLMProviderFactory>();
        _logger = new RecordingLogger<LLMModelSyncService>();
        _sut = new LLMModelSyncService(_repo, _factory, _logger);
        _created = [];
        _updated = [];
        _notification = null;
        _repo.CreateModelAsync(Arg.Any<LLMModel>()).Returns(c =>
        {
            _created.Add(c.Arg<LLMModel>());
            return c.Arg<LLMModel>();
        });
        _repo.UpdateModelAsync(Arg.Any<LLMModel>()).Returns(c =>
        {
            var m = c.Arg<LLMModel>();
            _updated.Add((m.ApiModelId, m.IsDeleted, m.IsEnabled, m.DeletedTime, m.ModelName));
            return m;
        });
        _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>()).Returns(c =>
        {
            _notification = c.Arg<LLMSyncNotification>();
            return c.Arg<LLMSyncNotification>();
        });
    }

    [Test]
    public async Task MixedDiscovery_InsertsRestoresAndRemoves_InDiscoveryOrder_AndNotifiesOnce()
    {
        var provider = MixedProvider(out var existing);
        _factory.GetEnabledProvidersAsync().Returns([provider]);
        var before = DateTime.UtcNow;

        await _sut.SyncAllProvidersAsync();

        var after = DateTime.UtcNow;
        _created.Select(m => m.ApiModelId).ShouldBe(new[] { "new.pass_Model/X", "new-fail", "text-embedding-3-small" });
        var passed = _created[0];
        passed.Id.ShouldNotBe(Guid.Empty);
        passed.ModelId.ShouldBe("new-pass-model-x");
        passed.ModelName.ShouldBe("New Pass");
        passed.ProviderId.ShouldBe(ProviderId);
        passed.IsEnabled.ShouldBeTrue();
        passed.IsDeleted.ShouldBeFalse();
        passed.DeletedTime.ShouldBeNull();
        passed.IsDefault.ShouldBeFalse();
        passed.MaxTokens.ShouldBe(4096);
        passed.ContextWindow.ShouldBe(128000);
        passed.CostPerInputToken.ShouldBe(0);
        passed.CostPerOutputToken.ShouldBe(0);
        passed.CreateTime!.Value.ShouldBeInRange(before, after);
        passed.UpdateTime!.Value.ShouldBeInRange(before, after);
        foreach (var failed in _created.Skip(1))
        {
            failed.IsEnabled.ShouldBeFalse();
            failed.IsDeleted.ShouldBeTrue();
            failed.DeletedTime!.Value.ShouldBeInRange(before, after);
        }

        _created[1].ModelId.ShouldBe("new-fail");
        _created[2].ModelId.ShouldBe("text-embedding-3-small");

        _updated.Select(u => u.ApiModelId).ShouldBe(new[] { "restored-pass", "gone" });
        _updated[0].ShouldBe(("restored-pass", false, true, (DateTime?)null, "Restored Pass"));
        _updated[1].IsDeleted.ShouldBeTrue();
        _updated[1].IsEnabled.ShouldBeFalse();
        _updated[1].DeletedTime!.Value.ShouldBeInRange(before, after);
        existing["restored-pass"].UpdateTime!.Value.ShouldBeInRange(before, after);
        existing["gone"].UpdateTime!.Value.ShouldBeInRange(before, after);
        existing["stale-fail"].DeletedTime.ShouldBe(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        existing["default-gone"].IsDeleted.ShouldBeFalse();

        await provider.Received(1).TestModelAsync("Restored-Pass", RestoredParameters);
        await provider.Received(1).TestModelAsync("stale-fail", null);
        await provider.DidNotReceive().TestModelAsync("text-embedding-3-small", Arg.Any<string?>());
        await provider.DidNotReceive().TestModelAsync("active", Arg.Any<string?>());

        Received.InOrder(() =>
        {
            _repo.GetModelsByProviderIncludingDeletedAsync(ProviderId);
            _repo.CreateModelAsync(Arg.Is<LLMModel>(m => m.ApiModelId == "new.pass_Model/X"));
            _repo.CreateModelAsync(Arg.Is<LLMModel>(m => m.ApiModelId == "new-fail"));
            _repo.CreateModelAsync(Arg.Is<LLMModel>(m => m.ApiModelId == "text-embedding-3-small"));
            _repo.UpdateModelAsync(Arg.Is<LLMModel>(m => m.ApiModelId == "restored-pass"));
            _repo.UpdateModelAsync(Arg.Is<LLMModel>(m => m.ApiModelId == "gone"));
            _repo.CreateSyncNotificationAsync(Arg.Any<LLMSyncNotification>());
        });

        var n = _notification!;
        n.Id.ShouldNotBe(Guid.Empty);
        n.ProviderId.ShouldBe(ProviderId);
        n.ProviderName.ShouldBe(ProviderName);
        n.NewModelsCount.ShouldBe(4);
        n.FailedModelsCount.ShouldBe(2);
        n.DeactivatedModelsCount.ShouldBe(1);
        n.NewModelNames.ShouldBe(new[] { "New Pass", "New Fail", "Embedding", "Restored Pass" });
        n.DeactivatedModelNames.ShouldBe(new[] { "Gone" });
        n.ModelTestResults.ShouldBe(new[]
        {
            new LLMModelTestResult("new.pass_Model/X", "New Pass", true, null, 100),
            new LLMModelTestResult("new-fail", "New Fail", false, "404", 150),
            new LLMModelTestResult("text-embedding-3-small", "Embedding", false, NonChatModelPatterns.SkipReason, 0),
            new LLMModelTestResult("Restored-Pass", "Restored Pass", true, null, 120),
        });
        n.IsRead.ShouldBeFalse();
        n.SyncedAt.ShouldBeInRange(before, after);
        n.CreateTime!.Value.ShouldBeInRange(before, after);
        n.UpdateTime!.Value.ShouldBeInRange(before, after);
    }

    [Test]
    public async Task MixedDiscovery_LogsEveryBranch_WithItsLevel_InOrder()
    {
        var provider = MixedProvider(out _);
        _factory.GetEnabledProvidersAsync().Returns([provider]);

        await _sut.SyncAllProvidersAsync();

        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Information, "LLMModelSyncService - OpenAI: inserted new.pass_Model/X (probe passed in 100ms)"),
            (LogLevel.Information, "LLMModelSyncService - OpenAI: inserted as DELETED new-fail (probe failed in 150ms)"),
            (LogLevel.Debug, "LLMModelSyncService - OpenAI: text-embedding-3-small skipped, non-chat model id pattern, not tested"),
            (LogLevel.Information, "LLMModelSyncService - OpenAI: inserted as DELETED text-embedding-3-small (probe skipped (non-chat model) in 0ms)"),
            (LogLevel.Information, "LLMModelSyncService - OpenAI: restored, reappeared in provider list Restored-Pass (test passed in 120ms)"),
            (LogLevel.Debug, "LLMModelSyncService - OpenAI: stale-fail stays deleted, still fails test, no change"),
            (LogLevel.Information, "LLMModelSyncService - OpenAI: removed model gone, no longer offered by provider"),
            (LogLevel.Warning, "LLMModelSyncService - OpenAI: default-gone is the default model and no longer offered, skipping removal"),
            (LogLevel.Information, "LLMModelSyncService - OpenAI sync: 4 new/restored (2 disabled), 1 removed"),
        });
    }

    [Test]
    public async Task NothingChanged_LogsNoChanges_AndWritesNothing()
    {
        var provider = Provider([new LLMModelDiscovery("active", "Active")]);
        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsByProviderIncludingDeletedAsync(ProviderId).Returns([Existing("active", "Active", deleted: false)]);

        await _sut.SyncAllProvidersAsync();

        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Information, "LLMModelSyncService - OpenAI sync: no changes"),
        });
        await _repo.DidNotReceiveWithAnyArgs().CreateSyncNotificationAsync(default!);
        await _repo.DidNotReceiveWithAnyArgs().UpdateModelAsync(default!);
    }

    [Test]
    public async Task EmptyDiscovery_WarnsAndSkipsRemoval_ThenReportsNoChanges()
    {
        var provider = Provider([]);
        _factory.GetEnabledProvidersAsync().Returns([provider]);
        _repo.GetModelsByProviderIncludingDeletedAsync(ProviderId).Returns([Existing("active", "Active", deleted: false)]);

        await _sut.SyncAllProvidersAsync();

        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Warning, "LLMModelSyncService - OpenAI: discovery returned an empty model list, skipping removal check"),
            (LogLevel.Information, "LLMModelSyncService - OpenAI sync: no changes"),
        });
    }

    [Test]
    public async Task NullDiscovery_ReturnsBeforeReadingTheRepository_AndLogsNothing()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns(ProviderId);
        provider.ProviderName.Returns(ProviderName);
        provider.GetAvailableModelsAsync().Returns(Task.FromResult<List<LLMModelDiscovery>?>(null));
        _factory.GetEnabledProvidersAsync().Returns([provider]);

        await _sut.SyncAllProvidersAsync();

        await _repo.DidNotReceiveWithAnyArgs().GetModelsByProviderIncludingDeletedAsync(default!);
        _logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public async Task ProviderThatThrows_IsLoggedAsWarning_AndTheNextProviderStillSyncs()
    {
        var failure = new InvalidOperationException("probe crashed");
        var broken = Provider([new LLMModelDiscovery("x", "X")], id: "broken", name: "Broken");
        broken.TestModelAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(Task.FromException<LLMModelTestResult>(failure));
        var healthy = Provider([new LLMModelDiscovery("active", "Active")]);
        _factory.GetEnabledProvidersAsync().Returns([broken, healthy]);
        _repo.GetModelsByProviderIncludingDeletedAsync("broken").Returns([]);
        _repo.GetModelsByProviderIncludingDeletedAsync(ProviderId).Returns([Existing("active", "Active", deleted: false)]);

        await _sut.SyncAllProvidersAsync();

        _logger.Entries[0].Level.ShouldBe(LogLevel.Warning);
        _logger.Entries[0].Message.ShouldBe("LLMModelSyncService - Broken sync failed: probe crashed");
        _logger.Entries[0].Exception.ShouldBeSameAs(failure);
        _logger.Entries[1].Message.ShouldBe("LLMModelSyncService - OpenAI sync: no changes");
        _logger.Entries.Count.ShouldBe(2);
    }

    [Test]
    public async Task CancelledToken_SyncsNoProvider()
    {
        var provider = Provider([new LLMModelDiscovery("x", "X")]);
        _factory.GetEnabledProvidersAsync().Returns([provider]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _sut.SyncAllProvidersAsync(cts.Token);

        await provider.DidNotReceive().GetAvailableModelsAsync();
        _logger.Entries.ShouldBeEmpty();
    }

    private ILLMProvider MixedProvider(out Dictionary<string, LLMModel> existing)
    {
        var provider = Provider(
        [
            new LLMModelDiscovery("new.pass_Model/X", "New Pass"),
            new LLMModelDiscovery("new-fail", "New Fail"),
            new LLMModelDiscovery("text-embedding-3-small", "Embedding"),
            new LLMModelDiscovery("Restored-Pass", "Restored Pass"),
            new LLMModelDiscovery("stale-fail", "Stale Fail"),
            new LLMModelDiscovery("active", "Active"),
        ]);
        provider.TestModelAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(c =>
        {
            var id = c.ArgAt<string>(0);
            return id switch
            {
                "new.pass_Model/X" => new LLMModelTestResult(id, "probe-name", true, null, 100),
                "new-fail" => new LLMModelTestResult(id, "probe-name", false, "404", 150),
                "Restored-Pass" => new LLMModelTestResult(id, "probe-name", true, null, 120),
                _ => new LLMModelTestResult(id, "probe-name", false, "gone", 90),
            };
        });

        var restored = Existing("restored-pass", "Old Name", deleted: true);
        restored.SupportedParameters = RestoredParameters;
        existing = new Dictionary<string, LLMModel>
        {
            ["restored-pass"] = restored,
            ["stale-fail"] = Existing("stale-fail", "Stale Fail", deleted: true),
            ["active"] = Existing("active", "Active", deleted: false),
            ["gone"] = Existing("gone", "Gone", deleted: false),
            ["default-gone"] = Existing("default-gone", "Default Gone", deleted: false, isDefault: true),
            ["deleted-gone"] = Existing("deleted-gone", "Deleted Gone", deleted: true),
        };
        _repo.GetModelsByProviderIncludingDeletedAsync(ProviderId).Returns(existing.Values.ToList());
        return provider;
    }

    private static ILLMProvider Provider(List<LLMModelDiscovery> discovered, string id = ProviderId, string name = ProviderName)
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProviderId.Returns(id);
        provider.ProviderName.Returns(name);
        provider.GetAvailableModelsAsync().Returns(Task.FromResult<List<LLMModelDiscovery>?>(discovered));
        return provider;
    }

    private static LLMModel Existing(string apiModelId, string name, bool deleted, bool isDefault = false) => new()
    {
        Id = Guid.NewGuid(),
        ModelId = apiModelId,
        ModelName = name,
        ApiModelId = apiModelId,
        ProviderId = ProviderId,
        IsEnabled = !deleted,
        IsDeleted = deleted,
        IsDefault = isDefault,
        DeletedTime = deleted ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
    };
}

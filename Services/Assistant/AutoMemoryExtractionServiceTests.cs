// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AutoMemoryExtractionService — verifies that extracted facts containing
/// internal entity names are discarded before storage while clean facts are stored,
/// so leaked internal terminology can never re-enter the agent memory. Also verifies the
/// service resolves its cheap model exclusively through the shared ICheapestModelResolver
/// instead of duplicating that lookup, that a situational memory expires after the configured TTL, and
/// that a duplicate of an expiring memory has its expiry pushed forward instead of being dropped - an
/// expired row is invisible to every read path but still blocks the dedupe check, so without the refresh
/// the fact could never be extracted again.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AutoMemoryExtractionServiceTests
{
    private ICheapestModelResolver _cheapestModelResolver = null!;
    private IAgentMemoryRepository _memoryRepository = null!;
    private IEmbeddingService _embeddingService = null!;
    private ILLMProvider _provider = null!;
    private AutoMemoryOptions _options = null!;
    private AutoMemoryExtractionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _cheapestModelResolver = Substitute.For<ICheapestModelResolver>();
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _embeddingService = Substitute.For<IEmbeddingService>();
        _provider = Substitute.For<ILLMProvider>();
        _options = new AutoMemoryOptions();

        var model = new LLMModel { ModelId = "cheap-model", ApiModelId = "cheap-model" };
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)model, (ILLMProvider?)_provider));
        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((float[]?)null);

        _service = CreateService();
    }

    private const int NonDefaultTtlDays = 7;

    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private AutoMemoryExtractionService CreateService() =>
        new(Substitute.For<ILogger<AutoMemoryExtractionService>>(),
            _cheapestModelResolver,
            _memoryRepository,
            _embeddingService,
            Microsoft.Extensions.Options.Options.Create(_options),
            new SettableTimeProvider(FixedNow.UtcDateTime));

    private void SetupExtractionResponse(string jsonArray)
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = jsonArray
        });
    }

    [Test]
    public async Task FactWithInternalEntityName_IsDiscarded()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Klacks_Bestellung\",\"content\":\"Eine Bestellung (OriginalOrder) wird beim Versiegeln zur SealedOrder.\",\"category\":\"procedure\",\"importance\":7}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FactWithInternalNameInKey_IsDiscarded()
    {
        SetupExtractionResponse(
            "[{\"key\":\"SealedOrder_Regel\",\"content\":\"Versiegelte Auftraege sind unveraenderlich.\",\"category\":\"procedure\",\"importance\":7}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CleanFact_IsStored()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.Key == "Firmen_Standort"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PersonalFact_IsScopedToCurrentUser()
    {
        var userId = Guid.NewGuid();
        SetupExtractionResponse(
            "[{\"key\":\"Lieblingssport\",\"content\":\"Der Benutzer mag Fussball.\",\"category\":\"preference\",\"importance\":6}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", userId.ToString());

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.Key == "Lieblingssport" && m.UserId == userId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompanyFact_StaysGlobal_NotScopedToUser()
    {
        var userId = Guid.NewGuid();
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", userId.ToString());

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.Key == "Firmen_Standort" && m.UserId == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExtractAndStoreMemoriesAsync_ResolvesModelThroughSharedCheapestModelResolver()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _cheapestModelResolver.Received(1).ResolveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoModelAvailableFromResolver_NoExtractionAttempted()
    {
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)null, (ILLMProvider?)null));

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _provider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>());
        await _memoryRepository.DidNotReceive().AddAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenExtractionIsDisabled_NoModelIsCalledAndNothingIsStored()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");
        _options.Enabled = false;
        var service = CreateService();

        await service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _provider.DidNotReceiveWithAnyArgs().ProcessAsync(default!);
        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFactWhoseKeyAlreadyExists_IsNotStoredAgain()
    {
        GivenDuplicate(new AgentMemory { Id = Guid.NewGuid(), Key = "Firmen_Standort", ExpiresAt = null });
        SetupExtractionResponse(
            "[{\"key\":\"  Firmen_Standort \",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADuplicateThatNeverExpires_IsSkippedWithoutAnyUpdate()
    {
        GivenDuplicate(new AgentMemory { Id = Guid.NewGuid(), Key = "Firmen_Standort", ExpiresAt = null });
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADuplicateOfAnExpiringMemory_HasItsExpiryPushedForwardInsteadOfBeingDropped()
    {
        _options.ContextTtlDays = NonDefaultTtlDays;
        var duplicate = new AgentMemory
        {
            Id = Guid.NewGuid(),
            Key = "Aktueller_Fokus",
            ExpiresAt = FixedNow.UtcDateTime.AddDays(-1)
        };
        GivenDuplicate(duplicate);
        SetupExtractionResponse(
            "[{\"key\":\"Aktueller_Fokus\",\"content\":\"Der Benutzer plant gerade den Mai.\",\"category\":\"context\",\"importance\":5}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.Received(1).UpdateAsync(
            Arg.Is<AgentMemory>(m => m.Id == duplicate.Id
                && m.ExpiresAt == FixedNow.UtcDateTime.AddDays(NonDefaultTtlDays)),
            Arg.Any<CancellationToken>());
        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task APersonalFact_IsCheckedForDuplicatesInsideTheUsersOwnScope()
    {
        var userId = Guid.NewGuid();
        SetupExtractionResponse(
            "[{\"key\":\"Lieblingssport\",\"content\":\"Der   Benutzer mag Fussball.\",\"category\":\"preference\",\"importance\":6}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", userId.ToString());

        await _memoryRepository.Received(1).FindDuplicateAsync(
            Arg.Any<Guid>(), userId, "lieblingssport", "der benutzer mag fussball.",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASituationalMemory_ExpiresAfterTheConfiguredTtl()
    {
        _options.ContextTtlDays = NonDefaultTtlDays;
        SetupExtractionResponse(
            "[{\"key\":\"Aktueller_Fokus\",\"content\":\"Der Benutzer plant gerade den Mai.\",\"category\":\"context\",\"importance\":5}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.ExpiresAt == FixedNow.UtcDateTime.AddDays(NonDefaultTtlDays)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ATemporalMemory_ExpiresAfterTheConfiguredTtlToo()
    {
        _options.ContextTtlDays = NonDefaultTtlDays;
        SetupExtractionResponse(
            "[{\"key\":\"Ferien_Anna\",\"content\":\"Anna ist bis Ende Mai in den Ferien.\",\"category\":\"temporal\",\"importance\":6}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.Category == MemoryCategories.Temporal
                && m.ExpiresAt == FixedNow.UtcDateTime.AddDays(NonDefaultTtlDays)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADurableFact_NeverExpires()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.ExpiresAt == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public void TheExtractionPrompt_ForbidsPermissionsRolesAndOneOffConfirmations()
    {
        AutoMemoryExtractionService.ExtractionSystemPrompt.ShouldContain("permissions");
        AutoMemoryExtractionService.ExtractionSystemPrompt.ShouldContain("roles");
        AutoMemoryExtractionService.ExtractionSystemPrompt.ShouldContain("navigation state");
        AutoMemoryExtractionService.ExtractionSystemPrompt.ShouldContain("lasting policy decision");
    }

    private void GivenDuplicate(AgentMemory duplicate) =>
        _memoryRepository.FindDuplicateAsync(
                Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(duplicate);
}

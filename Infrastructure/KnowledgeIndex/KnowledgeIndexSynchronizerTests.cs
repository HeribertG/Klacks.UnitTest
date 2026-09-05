// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeIndexSynchronizerTests
{
    private ISkillRegistry _skillRegistry = null!;
    private IAgentRecipeRepository _recipeRepository = null!;
    private IEmbeddingProvider _embeddings = null!;
    private IKnowledgeIndexRepository _repo = null!;
    private ISkillPhraseRepository _phrases = null!;
    private IKnowledgeEmbeddingSnapshotReader _snapshotReader = null!;
    private ILogger<KnowledgeIndexSynchronizer> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        _embeddings = Substitute.For<IEmbeddingProvider>();
        _embeddings.EmbeddingSpaceId.Returns("test-space");
        _repo = Substitute.For<IKnowledgeIndexRepository>();
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _phrases.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillPhrase>)new List<SkillPhrase>());
        _snapshotReader = Substitute.For<IKnowledgeEmbeddingSnapshotReader>();
        GivenSnapshot(new Dictionary<string, float[]>());
        _logger = Substitute.For<ILogger<KnowledgeIndexSynchronizer>>();
    }

    private KnowledgeIndexSynchronizer CreateSut() =>
        new(_skillRegistry, _recipeRepository, _embeddings, _repo, _phrases, _snapshotReader, _logger);

    private void GivenSnapshot(Dictionary<string, float[]> snapshot) =>
        _snapshotReader.LoadAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, float[]>)snapshot);

    private static byte[] HashFor(string spaceId, string embeddingText) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(spaceId + "\n" + embeddingText));

    private void GivenPhrases(params SkillPhrase[] phrases) =>
        _phrases.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillPhrase>)phrases.ToList());

    private static SkillPhrase Keyword(string ownerKind, string ownerName, string phrase, int sortOrder) =>
        new()
        {
            OwnerKind = ownerKind,
            OwnerName = ownerName,
            Language = null,
            Kind = SkillPhraseKinds.Keyword,
            Phrase = phrase,
            SortOrder = sortOrder
        };

    private static SkillPhrase Synonym(string ownerKind, string ownerName, string language, string phrase, int sortOrder) =>
        new()
        {
            OwnerKind = ownerKind,
            OwnerName = ownerName,
            Language = language,
            Kind = SkillPhraseKinds.Synonym,
            Phrase = phrase,
            SortOrder = sortOrder
        };

    [Test]
    public async Task SyncAsync_SkipsEntriesWithUnchangedHash()
    {
        var descriptor = new SkillDescriptor(
            "X", "Desc", SkillCategory.System, [], [], [], null);

        _skillRegistry.GetAllSkills().Returns([descriptor]);

        var embeddingText = "X. Desc\nParameters: ";
        var existingHash = HashFor("test-space", embeddingText);

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>
            {
                { (KnowledgeEntryKind.Skill, "X"), existingHash }
            });

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.DidNotReceive().EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpsertAsync(Arg.Any<IReadOnlyList<KnowledgeEntry>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_EmbeddingSpaceChanged_ReembedsEntryWithUnchangedText()
    {
        var descriptor = new SkillDescriptor(
            "X", "Desc", SkillCategory.System, [], [], [], null);

        _skillRegistry.GetAllSkills().Returns([descriptor]);

        var embeddingText = "X. Desc\nParameters: ";
        var hashFromOtherProvider = HashFor("onnx:multilingual-e5-small@384", embeddingText);

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>
            {
                { (KnowledgeEntryKind.Skill, "X"), hashFromOtherProvider }
            });

        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new float[][] { new float[384] });

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == embeddingText),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list => list.Count == 1 && list[0].SourceId == "X"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_OrphansGetDeleted()
    {
        _skillRegistry.GetAllSkills().Returns([]);

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>
            {
                { (KnowledgeEntryKind.Skill, "OrphanSkill"), new byte[] { 1, 2, 3 } }
            });

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _repo.Received(1).DeleteAsync(
            Arg.Is<IReadOnlyList<(KnowledgeEntryKind, string)>>(list =>
                list.Count == 1 && list[0].Item1 == KnowledgeEntryKind.Skill && list[0].Item2 == "OrphanSkill"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_NewOrChangedEntries_EmbeddedAndUpserted()
    {
        var descriptor = new SkillDescriptor(
            "NewSkill", "Creates employees.", SkillCategory.System,
            [new SkillParameter("name", "Employee name", SkillParameterType.String, true)],
            [], [], null);

        _skillRegistry.GetAllSkills().Returns([descriptor]);
        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());

        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new float[][] { new float[384] });

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.Received(1).EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list => list.Count == 1 && list[0].SourceId == "NewSkill"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_EmbeddingTextIncludesTriggerKeywordsAndSynonyms()
    {
        var descriptor = new SkillDescriptor(
            "explain_page_dashboard", "Explains the dashboard page.", SkillCategory.System,
            [], [], [], null);

        _skillRegistry.GetAllSkills().Returns([descriptor]);
        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Skill, "explain_page_dashboard", "abdeckung", 0),
            Keyword(SkillPhraseOwnerKinds.Skill, "explain_page_dashboard", "bestätigung", 1),
            Synonym(SkillPhraseOwnerKinds.Skill, "explain_page_dashboard", "de", "was sehe ich hier", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "explain_page_dashboard", "fr", "tableau de bord", 0));

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());

        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new float[][] { new float[384] });

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list =>
                list.Count == 1 &&
                list[0].Text.Contains("Keywords: abdeckung, bestätigung") &&
                list[0].Text.Contains("Synonyms: was sehe ich hier, tableau de bord")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_SkillWithMultiplePermissions_UsesFirstPermission()
    {
        var descriptor = new SkillDescriptor(
            "PermSkill", "Needs permission.", SkillCategory.System,
            [], ["shifts.read", "shifts.write"], [], null);

        _skillRegistry.GetAllSkills().Returns([descriptor]);
        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());

        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new float[][] { new float[384] });

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list =>
                list.Count == 1 && list[0].RequiredPermission == "shifts.read"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_EnabledRecipe_IndexedAsRecipeKindWithGoalKeywordsSynonymsAndSteps()
    {
        _skillRegistry.GetAllSkills().Returns([]);

        var recipe = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "onboard-employee",
            Goal = "Onboard a new employee end to end.",
            StepsJson = """[{"kind":"mutate","skill":"create_employee"},{"kind":"mutate","skill":"add_client_to_group"}]""",
            IsEnabled = true
        };

        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { recipe });
        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Recipe, "onboard-employee", "onboard", 0),
            Keyword(SkillPhraseOwnerKinds.Recipe, "onboard-employee", "einstell", 1),
            Keyword(SkillPhraseOwnerKinds.Recipe, "onboard-employee", "neuer mitarbeiter", 2),
            Synonym(SkillPhraseOwnerKinds.Recipe, "onboard-employee", "fr", "intégrer un employé", 0));

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());

        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new float[][] { new float[384] });

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list =>
                list.Count == 1 &&
                list[0].Kind == KnowledgeEntryKind.Recipe &&
                list[0].SourceId == "onboard-employee" &&
                list[0].RequiredPermission == null &&
                list[0].Text.Contains("Onboard a new employee end to end.") &&
                list[0].Text.Contains("onboard") &&
                list[0].Text.Contains("einstell") &&
                list[0].Text.Contains("neuer mitarbeiter") &&
                list[0].Text.Contains("intégrer un employé") &&
                list[0].Text.Contains("create_employee") &&
                list[0].Text.Contains("add_client_to_group")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_SkillsAndRecipes_AreBothIndexedTogether()
    {
        var descriptor = new SkillDescriptor("X", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        var recipe = new AgentRecipe { Id = Guid.NewGuid(), Name = "some-recipe", Goal = "Do something.", IsEnabled = true };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { recipe });

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<IReadOnlyList<string>>().Select(_ => new float[384]).ToArray());

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list =>
                list.Count == 2 &&
                list.Any(e => e.Kind == KnowledgeEntryKind.Skill && e.SourceId == "X") &&
                list.Any(e => e.Kind == KnowledgeEntryKind.Recipe && e.SourceId == "some-recipe")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_SynonymsAreDedupedAcrossLanguages_ShorterLanguageTagFirst()
    {
        var descriptor = new SkillDescriptor("S", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        GivenPhrases(
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "pt-BR", "partilhado", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "en", "Gemeinsam", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "en", "shared", 1),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "de", "gemeinsam", 0));

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        captured.Single().Text.ShouldBe("S. Desc\nParameters: \nSynonyms: gemeinsam, shared, partilhado");
    }

    [Test]
    public async Task SyncAsync_SkillKeywordsAreNotDeduped()
    {
        var descriptor = new SkillDescriptor("S", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "plan", 0),
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "Plan", 1),
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "plan", 2));

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        captured.Single().Text.ShouldBe("S. Desc\nParameters: \nKeywords: plan, Plan, plan");
    }

    [Test]
    public async Task SyncAsync_RecipeKeywordsAreEmittedVerbatimWithoutASecondDedup()
    {
        _skillRegistry.GetAllSkills().Returns([]);

        var recipe = new AgentRecipe { Id = Guid.NewGuid(), Name = "r", Goal = "Goal.", IsEnabled = true };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { recipe });

        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Recipe, "r", "plan", 0),
            Keyword(SkillPhraseOwnerKinds.Recipe, "r", "Plan", 1));

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        captured.Single().Text.ShouldBe("r. Goal.\nKeywords: plan, Plan");
    }

    [Test]
    public async Task SyncAsync_SynonymAlsoPresentAsKeyword_IsEmittedOnceInTheKeywordSection()
    {
        var descriptor = new SkillDescriptor("S", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "heartbeat", 0),
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "monitoring", 1),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "de", "heartbeat", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "de", "überwachung", 1),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "en", "monitoring", 0));

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        captured.Single().Text.ShouldBe("S. Desc\nParameters: \nKeywords: heartbeat, monitoring\nSynonyms: überwachung");
    }

    [Test]
    public async Task SyncAsync_SynonymDuplicatesAKeywordInDifferentCasing_IsStillRemoved()
    {
        var descriptor = new SkillDescriptor("S", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "Plan", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "de", "plan", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "de", "entwurf", 1));

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        captured.Single().Text.ShouldBe("S. Desc\nParameters: \nKeywords: Plan\nSynonyms: entwurf");
    }

    [Test]
    public async Task SyncAsync_EverySynonymDuplicatesAKeyword_OmitsTheSynonymSectionEntirely()
    {
        var descriptor = new SkillDescriptor("S", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "token", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "de", "token", 0),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "en", "Token", 0));

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        captured.Single().Text.ShouldBe("S. Desc\nParameters: \nKeywords: token");
    }

    [Test]
    public async Task SyncAsync_KeywordDuplicationWithinItsOwnSection_IsNotAffectedByTheSynonymDedup()
    {
        var descriptor = new SkillDescriptor("S", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        GivenPhrases(
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "plan", 0),
            Keyword(SkillPhraseOwnerKinds.Skill, "S", "plan", 1),
            Synonym(SkillPhraseOwnerKinds.Skill, "S", "de", "entwurf", 0));

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        captured.Single().Text.ShouldBe("S. Desc\nParameters: \nKeywords: plan, plan\nSynonyms: entwurf");
    }

    [Test]
    public async Task SyncAsync_DirtyEntryFoundInSnapshot_IsUpsertedWithoutEmbedding()
    {
        var descriptor = new SkillDescriptor("X", "Desc", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([descriptor]);

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());

        var snapshotVector = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        GivenSnapshot(new Dictionary<string, float[]>
        {
            { KnowledgeEmbeddingCodec.ToHex(HashFor("test-space", "X. Desc\nParameters: ")), snapshotVector }
        });

        var captured = CaptureUpsertedEntries();

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.DidNotReceive().EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        captured.Single().SourceId.ShouldBe("X");
        captured.Single().Embedding.ShouldBe(snapshotVector);
    }

    [Test]
    public async Task SyncAsync_PartialSnapshotHit_EmbedsOnlyTheMissAndKeepsVectorsAligned()
    {
        var first = new SkillDescriptor("A", "DescA", SkillCategory.System, [], [], [], null);
        var second = new SkillDescriptor("B", "DescB", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([first, second]);

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());

        var snapshotVector = new[] { 1f, 1f, 1f, 1f };
        GivenSnapshot(new Dictionary<string, float[]>
        {
            { KnowledgeEmbeddingCodec.ToHex(HashFor("test-space", "A. DescA\nParameters: ")), snapshotVector }
        });

        var embeddedVector = new[] { 2f, 2f, 2f, 2f };
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { embeddedVector });

        var captured = new List<KnowledgeEntry>();
        _repo.When(r => r.UpsertAsync(Arg.Any<IReadOnlyList<KnowledgeEntry>>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => captured.AddRange(callInfo.Arg<IReadOnlyList<KnowledgeEntry>>()));

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == "B. DescB\nParameters: "),
            Arg.Any<CancellationToken>());

        captured.Count.ShouldBe(2);
        captured.Single(e => e.SourceId == "A").Embedding.ShouldBe(snapshotVector);
        captured.Single(e => e.SourceId == "B").Embedding.ShouldBe(embeddedVector);
    }

    [Test]
    public async Task SyncAsync_EmptySnapshot_EmbedsEveryDirtyEntryAsBefore()
    {
        var first = new SkillDescriptor("A", "DescA", SkillCategory.System, [], [], [], null);
        var second = new SkillDescriptor("B", "DescB", SkillCategory.System, [], [], [], null);
        _skillRegistry.GetAllSkills().Returns([first, second]);

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());

        var vectorA = new[] { 1f, 0f };
        var vectorB = new[] { 0f, 1f };
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { vectorA, vectorB });

        var captured = new List<KnowledgeEntry>();
        _repo.When(r => r.UpsertAsync(Arg.Any<IReadOnlyList<KnowledgeEntry>>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => captured.AddRange(callInfo.Arg<IReadOnlyList<KnowledgeEntry>>()));

        var sync = CreateSut();
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(texts =>
                texts.Count == 2 &&
                texts[0] == "A. DescA\nParameters: " &&
                texts[1] == "B. DescB\nParameters: "),
            Arg.Any<CancellationToken>());

        captured.Count.ShouldBe(2);
        captured.Single(e => e.SourceId == "A").Embedding.ShouldBe(vectorA);
        captured.Single(e => e.SourceId == "B").Embedding.ShouldBe(vectorB);
    }

    private List<KnowledgeEntry> CaptureUpsertedEntries()
    {
        var captured = new List<KnowledgeEntry>();

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>());
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<IReadOnlyList<string>>().Select(_ => new float[384]).ToArray());
        _repo.When(r => r.UpsertAsync(Arg.Any<IReadOnlyList<KnowledgeEntry>>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => captured.AddRange(callInfo.Arg<IReadOnlyList<KnowledgeEntry>>()));

        return captured;
    }
}

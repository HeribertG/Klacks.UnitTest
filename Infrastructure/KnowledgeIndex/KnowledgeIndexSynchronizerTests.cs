// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Services;
using Klacks.Api.Infrastructure.KnowledgeIndex.Domain;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeIndexSynchronizerTests
{
    private ISkillRegistry _skillRegistry = null!;
    private IEmbeddingProvider _embeddings = null!;
    private IKnowledgeIndexRepository _repo = null!;

    [SetUp]
    public void Setup()
    {
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _embeddings = Substitute.For<IEmbeddingProvider>();
        _repo = Substitute.For<IKnowledgeIndexRepository>();
    }

    [Test]
    public async Task SyncAsync_SkipsEntriesWithUnchangedHash()
    {
        var descriptor = new SkillDescriptor(
            "X", "Desc", SkillCategory.System, [], [], [], null);

        _skillRegistry.GetAllSkills().Returns([descriptor]);

        var embeddingText = "X. Desc\nParameters: ";
        var existingHash = SHA256.HashData(Encoding.UTF8.GetBytes(embeddingText));

        _repo.GetAllHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(KnowledgeEntryKind, string), byte[]>
            {
                { (KnowledgeEntryKind.Skill, "X"), existingHash }
            });

        var sync = new KnowledgeIndexSynchronizer(_skillRegistry, _embeddings, _repo);
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.DidNotReceive().EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpsertAsync(Arg.Any<IReadOnlyList<KnowledgeEntry>>(), Arg.Any<CancellationToken>());
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

        var sync = new KnowledgeIndexSynchronizer(_skillRegistry, _embeddings, _repo);
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

        var sync = new KnowledgeIndexSynchronizer(_skillRegistry, _embeddings, _repo);
        await sync.SyncAsync(CancellationToken.None);

        await _embeddings.Received(1).EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list => list.Count == 1 && list[0].SourceId == "NewSkill"),
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

        var sync = new KnowledgeIndexSynchronizer(_skillRegistry, _embeddings, _repo);
        await sync.SyncAsync(CancellationToken.None);

        await _repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyList<KnowledgeEntry>>(list =>
                list.Count == 1 && list[0].RequiredPermission == "shifts.read"),
            Arg.Any<CancellationToken>());
    }
}

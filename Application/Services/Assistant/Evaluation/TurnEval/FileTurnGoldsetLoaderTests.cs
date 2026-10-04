// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

[TestFixture]
public class FileTurnGoldsetLoaderTests
{
    private const string RealGoldsetName = "turn-selection-v1";
    private const string JsonExtension = ".json";

    // Suffixed per test run so a leftover from a killed run, or another fixture instance in the same shared
    // bin output directory, can never collide on the exact same file name and race File.Delete in teardown.
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];
    private static readonly string WrongKindGoldsetName = $"turneval-test-wrong-kind-{RunId}";
    private static readonly string EnumGoldsetName = $"turneval-test-enum-parsing-{RunId}";
    private static readonly string ParentDecoyGoldsetName = $"turneval-test-parent-decoy-{RunId}";
    private static readonly string CorrectionKindGoldsetName = $"turneval-test-correction-kind-{RunId}";

    private static readonly string[] GoldsetRelativePath = ["Application", "Skills", "Goldsets"];

    private static readonly string[] RepoGoldsetRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Goldsets"
    ];

    private const string WrongKindJson = """
        {
          "version": 2,
          "kind": "knowledge-index",
          "items": []
        }
        """;

    private const string EnumParsingJson = """
        {
          "version": 2,
          "kind": "turn-selection",
          "items": [
            {
              "id": "te-001",
              "message": "change the phone number of Mrs Muller",
              "expectedTool": "add_client_phone",
              "expectedSlots": [
                { "name": "lastName", "match": "resolved-entity-id", "entity": { "type": "client", "idNumber": 990001 } },
                { "name": "phone", "match": "contains", "value": "552" },
                { "name": "firstName", "match": "ignore" }
              ]
            }
          ]
        }
        """;

    private const string CorrectionKindJson = """
        {
          "version": 1,
          "kind": "turn-correction",
          "items": [
            {
              "id": "cr-001",
              "message": "Nein, ich meinte alle Mitarbeitenden.",
              "expectedTool": "search_employees",
              "expectsCorrection": true,
              "previousTurn": {
                "message": "Trag alle Mitarbeitenden in die Gruppe Zürich ein.",
                "calledSkill": "find_customer_candidates",
                "assistantAnswerExcerpt": "Ich habe nach Kunden gesucht."
              }
            }
          ]
        }
        """;

    private FileTurnGoldsetLoader _loader = null!;
    private readonly List<string> _tempFiles = new();

    private static string GoldsetDirectory =>
        Path.Combine([AppContext.BaseDirectory, .. GoldsetRelativePath]);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Directory.CreateDirectory(GoldsetDirectory);
        EnsureRealGoldsetPresent();
        WriteTempGoldset(Path.Combine(GoldsetDirectory, WrongKindGoldsetName + JsonExtension), WrongKindJson);
        WriteTempGoldset(Path.Combine(GoldsetDirectory, EnumGoldsetName + JsonExtension), EnumParsingJson);
        WriteTempGoldset(Path.Combine(GoldsetDirectory, CorrectionKindGoldsetName + JsonExtension), CorrectionKindJson);
        WriteTempGoldset(
            Path.Combine(Directory.GetParent(GoldsetDirectory)!.FullName, ParentDecoyGoldsetName + JsonExtension),
            EnumParsingJson);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Windows (antivirus/indexer) can briefly hold a lock on a just-written file. The file is
                // uniquely named for this run and harms nothing if a later cleanup or the next run's own
                // OneTimeSetUp overwrites it.
            }
        }
    }

    [SetUp]
    public void SetUp()
    {
        _loader = new FileTurnGoldsetLoader();
    }

    [Test]
    public async Task LoadAsync_RealGoldset_LoadsItems()
    {
        var items = await _loader.LoadAsync(RealGoldsetName);

        items.Count.ShouldBeGreaterThan(0);
        items.ShouldAllBe(i => !string.IsNullOrWhiteSpace(i.Id));
        items.ShouldContain(i => i.ExpectedTool == null);
        items.ShouldContain(i => i.ExpectedTool != null);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void LoadAsync_BlankName_ThrowsArgumentException(string goldset)
    {
        Should.ThrowAsync<ArgumentException>(() => _loader.LoadAsync(goldset));
    }

    [Test]
    public void LoadAsync_UnknownName_ThrowsFileNotFoundException()
    {
        Should.ThrowAsync<FileNotFoundException>(() => _loader.LoadAsync("turneval-test-does-not-exist"));
    }

    [Test]
    public void LoadAsync_WrongKind_ThrowsInvalidDataException()
    {
        Should.ThrowAsync<InvalidDataException>(() => _loader.LoadAsync(WrongKindGoldsetName));
    }

    [Test]
    public void LoadAsync_PathTraversal_IsSanitizedAndNotFound()
    {
        Should.ThrowAsync<FileNotFoundException>(() => _loader.LoadAsync("../" + ParentDecoyGoldsetName));
    }

    [Test]
    public async Task LoadAsync_CorrectionKind_LoadsItems()
    {
        var items = await _loader.LoadAsync(CorrectionKindGoldsetName);

        items.Count.ShouldBe(1);
        items[0].ExpectsCorrection.ShouldBeTrue();
        items[0].PreviousTurn.ShouldNotBeNull();
        items[0].PreviousTurn!.CalledSkill.ShouldBe("find_customer_candidates");
    }

    [Test]
    public async Task LoadAsync_ResolvedEntityIdMatchMode_ParsesEnumAndEntity()
    {
        var items = await _loader.LoadAsync(EnumGoldsetName);

        items.Count.ShouldBe(1);
        var slots = items[0].ExpectedSlots;
        slots.Count.ShouldBe(3);

        var nameSlot = slots.Single(s => s.Name == "lastName");
        nameSlot.Match.ShouldBe(SlotMatchMode.ResolvedEntityId);
        nameSlot.Entity.ShouldNotBeNull();
        nameSlot.Entity!.Type.ShouldBe("client");
        nameSlot.Entity.IdNumber.ShouldBe(990001);

        slots.Single(s => s.Name == "phone").Match.ShouldBe(SlotMatchMode.Contains);
        slots.Single(s => s.Name == "firstName").Match.ShouldBe(SlotMatchMode.Ignore);
    }

    private void EnsureRealGoldsetPresent()
    {
        var target = Path.Combine(GoldsetDirectory, RealGoldsetName + JsonExtension);
        if (File.Exists(target))
        {
            return;
        }

        var source = LocateRepoGoldset(RealGoldsetName + JsonExtension);
        File.Copy(source, target);
        _tempFiles.Add(target);
    }

    private void WriteTempGoldset(string path, string content)
    {
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
    }

    private static string LocateRepoGoldset(string fileName)
    {
        return RepositoryRootLocator.RequireFile([.. RepoGoldsetRelativePath, fileName]);
    }
}

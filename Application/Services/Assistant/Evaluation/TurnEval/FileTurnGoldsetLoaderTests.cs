// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class FileTurnGoldsetLoaderTests
{
    private const string RealGoldsetName = "turn-selection-v1";
    private const string WrongKindGoldsetName = "turneval-test-wrong-kind";
    private const string EnumGoldsetName = "turneval-test-enum-parsing";
    private const string ParentDecoyGoldsetName = "turneval-test-parent-decoy";
    private const string JsonExtension = ".json";

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
        WriteTempGoldset(
            Path.Combine(Directory.GetParent(GoldsetDirectory)!.FullName, ParentDecoyGoldsetName + JsonExtension),
            EnumParsingJson);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            File.Delete(file);
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. RepoGoldsetRelativePath, fileName]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', RepoGoldsetRelativePath)}/{fileName} by walking up from the test base directory.");
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Pins the contract of RepositoryRootLocator: sibling repositories are only found next to the Klacks.Api of the
/// tree the search starts in, never in an outer checkout further up the disk (the git-worktree leak).
/// </summary>
[TestFixture]
public class RepositoryRootLocatorTests
{
    private const string UiPath = "Klacks.Ui/src/assets/i18n";
    private const string ApiProjectFile = "Klacks.Api.csproj";

    private string _outer = string.Empty;

    [SetUp]
    public void CreateTemporaryTree()
    {
        _outer = Path.Combine(Path.GetTempPath(), "RepositoryRootLocatorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outer);
    }

    [TearDown]
    public void DeleteTemporaryTree()
    {
        if (Directory.Exists(_outer))
        {
            Directory.Delete(_outer, recursive: true);
        }
    }

    [Test]
    public void FindRoot_ReturnsTheNearestAncestorHoldingKlacksApi()
    {
        var inner = CreateTreeWithApi(Path.Combine(_outer, "inner"));
        var start = Directory.CreateDirectory(Path.Combine(inner, "Klacks.UnitTest", "bin", "Debug")).FullName;

        RepositoryRootLocator.FindRoot(start).ShouldBe(inner);
    }

    [Test]
    public void FindRoot_IgnoresAKlacksApiDirectoryWithoutProjectFile()
    {
        Directory.CreateDirectory(Path.Combine(_outer, "Klacks.Api"));
        var start = Directory.CreateDirectory(Path.Combine(_outer, "build", "bin")).FullName;

        RepositoryRootLocator.FindRoot(start).ShouldBeNull();
    }

    [Test]
    public void FindDirectoryIn_DoesNotLeakIntoAnOuterCheckoutThatHoldsTheSibling()
    {
        Directory.CreateDirectory(Path.Combine(_outer, "Klacks.Ui", "src", "assets", "i18n"));
        var inner = CreateTreeWithApi(Path.Combine(_outer, "inner"));
        var start = Directory.CreateDirectory(Path.Combine(inner, "Klacks.UnitTest", "bin", "Debug")).FullName;

        var root = RepositoryRootLocator.FindRoot(start);

        root.ShouldBe(inner);
        RepositoryRootLocator.FindDirectoryIn(root, UiPath).ShouldBeNull();
    }

    [Test]
    public void FindDirectoryIn_FindsTheSiblingNextToKlacksApi()
    {
        var root = CreateTreeWithApi(Path.Combine(_outer, "tree"));
        var ui = Directory.CreateDirectory(Path.Combine(root, "Klacks.Ui", "src", "assets", "i18n")).FullName;

        RepositoryRootLocator.FindDirectoryIn(root, UiPath).ShouldBe(ui);
    }

    [Test]
    public void FindFileIn_AcceptsSlashSeparatedAndSplitSegments()
    {
        var root = CreateTreeWithApi(Path.Combine(_outer, "tree"));
        var file = Path.Combine(root, "Klacks.Api", "Application", "Skills", "skill-seeds.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "[]");

        RepositoryRootLocator.FindFileIn(root, "Klacks.Api/Application/Skills/skill-seeds.json").ShouldBe(file);
        RepositoryRootLocator.FindFileIn(root, "Klacks.Api", "Application/Skills", "skill-seeds.json").ShouldBe(file);
        RepositoryRootLocator.FindDirectoryIn(root, "Klacks.Api/Application/Skills/skill-seeds.json").ShouldBeNull();
    }

    [Test]
    public void FindDirectoryIn_WithoutRootReturnsNull()
    {
        RepositoryRootLocator.FindDirectoryIn(null, UiPath).ShouldBeNull();
        RepositoryRootLocator.FindFileIn(null, UiPath).ShouldBeNull();
    }

    [Test]
    public void Root_IsTheTreeTheTestsWereBuiltFrom()
    {
        var root = RepositoryRootLocator.Root;

        root.ShouldNotBeNull();
        File.Exists(Path.Combine(root, RepositoryRootLocator.ApiProjectDirectoryName, ApiProjectFile)).ShouldBeTrue();
        RepositoryRootLocator.ApiProject.ShouldBe(Path.Combine(root, RepositoryRootLocator.ApiProjectDirectoryName));
        AppContext.BaseDirectory.ShouldStartWith(root);
    }

    [Test]
    public void NotFoundMessage_NamesTheSearchedRootAndThePath()
    {
        var message = RepositoryRootLocator.NotFoundMessage(UiPath);

        message.ShouldContain(UiPath);
        message.ShouldContain(RepositoryRootLocator.RequireRoot());
    }

    private static string CreateTreeWithApi(string root)
    {
        var api = Directory.CreateDirectory(Path.Combine(root, RepositoryRootLocator.ApiProjectDirectoryName));
        File.WriteAllText(Path.Combine(api.FullName, ApiProjectFile), "<Project />");
        return root;
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Resolves files and directories of the source tree the tests were built from. The tree root is the NEAREST
/// ancestor of the test binaries that holds Klacks.Api/Klacks.Api.csproj; the sibling repositories (Klacks.Ui,
/// Klacks.Marketplace, ...) are looked up next to Klacks.Api there and nowhere else. A git worktree without its own
/// Klacks.Ui therefore finds none, instead of leaking into another checkout further up the disk.
/// Every relative path is a list of segments, each of which may itself contain '/'.
/// </summary>
public static class RepositoryRootLocator
{
    public const string ApiProjectDirectoryName = "Klacks.Api";

    private const string ApiProjectFileName = "Klacks.Api.csproj";
    private const char PathSeparator = '/';

    private static readonly Lazy<string?> TreeRoot = new(() => FindRoot(AppContext.BaseDirectory));

    public static string? Root => TreeRoot.Value;

    /// <summary>
    /// Full path of the Klacks.Api project of the tree the tests were built from.
    /// </summary>
    public static string ApiProject => Path.Combine(RequireRoot(), ApiProjectDirectoryName);

    /// <param name="startDirectory">Directory the upward search starts from</param>
    /// <returns>The nearest ancestor (or the directory itself) that holds Klacks.Api/Klacks.Api.csproj, otherwise null</returns>
    public static string? FindRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ApiProjectDirectoryName, ApiProjectFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static string RequireRoot() =>
        Root ?? throw new DirectoryNotFoundException(NoRootMessage(AppContext.BaseDirectory));

    public static string? FindDirectory(params string[] relativePath) =>
        FindDirectoryIn(Root, relativePath);

    public static string? FindFile(params string[] relativePath) =>
        FindFileIn(Root, relativePath);

    /// <param name="root">Tree root to resolve against; null yields null</param>
    /// <param name="relativePath">Path segments below the root</param>
    public static string? FindDirectoryIn(string? root, params string[] relativePath)
    {
        var candidate = Combine(root, relativePath);
        return candidate != null && Directory.Exists(candidate) ? candidate : null;
    }

    /// <param name="root">Tree root to resolve against; null yields null</param>
    /// <param name="relativePath">Path segments below the root</param>
    public static string? FindFileIn(string? root, params string[] relativePath)
    {
        var candidate = Combine(root, relativePath);
        return candidate != null && File.Exists(candidate) ? candidate : null;
    }

    public static string RequireDirectory(params string[] relativePath) =>
        FindDirectory(relativePath) ?? throw new DirectoryNotFoundException(NotFoundMessage(relativePath));

    public static string RequireFile(params string[] relativePath) =>
        FindFile(relativePath) ?? throw new FileNotFoundException(NotFoundMessage(relativePath));

    /// <summary>
    /// Message for a path that is missing in the tree root. Used both for failures (a Klacks.Api path must exist) and
    /// for Assert.Inconclusive (a sibling repository that is not checked out next to Klacks.Api).
    /// </summary>
    public static string NotFoundMessage(params string[] relativePath)
    {
        var root = Root;
        return root == null
            ? NoRootMessage(AppContext.BaseDirectory)
            : $"'{string.Join(PathSeparator, relativePath)}' does not exist in the repository root '{root}' " +
              $"(the nearest ancestor holding {ApiProjectDirectoryName}); a repository is only searched there, " +
              "so check it out next to Klacks.Api.";
    }

    private static string? Combine(string? root, string[] relativePath)
    {
        if (root == null)
        {
            return null;
        }

        var segments = relativePath
            .SelectMany(entry => entry.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Prepend(root);

        return Path.Combine(segments.ToArray());
    }

    private static string NoRootMessage(string startDirectory) =>
        $"No ancestor of '{startDirectory}' holds {ApiProjectDirectoryName}/{ApiProjectFileName}, " +
        "so the repository root cannot be determined.";
}

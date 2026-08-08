// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Resolves where artifacts belong. It walks up from the directory the test assembly was loaded from
/// until it finds the test project file, rather than counting "../" levels or trusting the current
/// working directory — the bin path depth differs between a console run and an IDE run, and the
/// working directory is whatever the runner happened to set.
/// </summary>
public static class AutofillRepositoryPaths
{
    private const string TestProjectFileName = "Klacks.UnitTest.csproj";
    private const string AutofillFolderName = "Autofill";
    private const string ArtifactFolderName = "artifacts";

    private static readonly Lazy<string> TestProjectRootValue = new(FindTestProjectRoot);

    /// <summary>Directory that holds <c>Klacks.UnitTest.csproj</c>.</summary>
    public static string TestProjectRoot => TestProjectRootValue.Value;

    /// <summary>
    /// Directory the artifacts of one scenario go into, created if it does not exist yet.
    /// </summary>
    /// <param name="scenario">Scenario name; becomes the folder name</param>
    public static string ArtifactDirectory(string scenario)
    {
        var directory = Path.Combine(TestProjectRoot, AutofillFolderName, ArtifactFolderName, scenario);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindTestProjectRoot()
    {
        var fromAssembly = SearchUpwards(AppContext.BaseDirectory);
        if (fromAssembly is not null)
        {
            return fromAssembly;
        }

        // Only reached when the assembly does not sit under the project tree, for example when the
        // building blocks are driven from a separate host. The working directory is the fallback,
        // never the primary source.
        var fromWorkingDirectory = SearchUpwards(Directory.GetCurrentDirectory());
        if (fromWorkingDirectory is not null)
        {
            return fromWorkingDirectory;
        }

        throw new InvalidOperationException(
            $"Could not locate '{TestProjectFileName}' above '{AppContext.BaseDirectory}' "
            + $"or above '{Directory.GetCurrentDirectory()}'. Artifacts are written relative to the test project.");
    }

    private static string? SearchUpwards(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, TestProjectFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

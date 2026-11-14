// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the rule "no silent catch-all" (.claude/rules/backend-architecture.md, Errors, cancellation, logging) for
/// every .cs file of Klacks.Api except bin/, obj/ and Migrations/. The CodeQL rule cs/catch-of-all-exceptions is
/// excluded by configuration because catch (Exception) is often intended here (failure isolation per item, one
/// BackgroundService iteration); this guard keeps the part of that rule that matters: a catch-all that swallows the
/// exception without a trace.
///
/// A Roslyn syntax scan (SilentCatchAllScanner), not a text scan. A catch-all is a catch without a type or with
/// Exception / System.Exception, with or without a when filter. It is silent when its block contains no throw
/// (statement or expression) and no call of a method named Log... (LogWarning, LogError, LogDebug, LogInformation,
/// LogCritical, LogTrace, Log and own helpers such as LogUnreadable). Lambdas, anonymous methods and local functions
/// inside the block are not searched. The Log prefix is the only logging path recognised: log calls in Klacks.Api go
/// through ILogger or a helper named Log..., so no further way was added. A catch that only turns the exception
/// into an error value for the caller (return default, an error result) is silent in this sense and is counted.
///
/// Blocks that existed when the guard was introduced (2026-11-14) are listed in SilentCatchAllGuardAllowlist with their
/// exact count per file. The list is shrink-only: a new silent catch-all fails, an allowlisted file whose count differs
/// in either direction fails, and so does an entry whose file is gone. The failure message prints the current table as
/// a C# initializer, so a cleanup can copy the lowered number.
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SilentCatchAllGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const int MinimumScannedFiles = 5000;

    private static readonly string[] ExcludedDirectoryNames = ["bin", "obj", "Migrations"];

    private static readonly Lazy<(Dictionary<string, IReadOnlyList<int>> Hits, int ScannedFiles)> Scan =
        new(ScanApi);

    [Test]
    public void Api_MustNotIntroduceSilentCatchAllBlocks()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scannedFiles} source files were scanned. The guard cannot have inspected the real " +
            "source tree, so a green result would be meaningless.");

        var violations = hits
            .Where(hit => !SilentCatchAllGuardAllowlist.Entries.ContainsKey(hit.Key))
            .OrderBy(hit => hit.Key, StringComparer.Ordinal)
            .ToList();

        var report = new StringBuilder();
        foreach (var (path, lines) in violations)
        {
            report.AppendLine($"  {path}: {lines.Count} silent catch-all block(s) at line(s) {string.Join(", ", lines)}");
        }

        violations.ShouldBeEmpty(
            "A catch (Exception) / catch without type that neither logs nor rethrows swallows every failure, " +
            "including bugs. Catch the specific type the callee throws (for example FormatException, " +
            "NpgsqlException, HttpRequestException), or log the exception (ILogger.LogWarning / LogError), or " +
            "rethrow it. Deliberate best-effort needs at least a LogDebug. Do not add the file to " +
            $"SilentCatchAllGuardAllowlist.{Environment.NewLine}{report}{Environment.NewLine}" +
            $"Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Allowlist_MustMatchTheSourceExactly()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(MinimumScannedFiles);

        var stale = new StringBuilder();
        foreach (var (path, expectedCount) in SilentCatchAllGuardAllowlist.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!hits.TryGetValue(path, out var lines))
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has no silent catch-all anymore (or is gone) - remove the entry.");
                continue;
            }

            if (lines.Count != expectedCount)
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has {lines.Count} " +
                                 $"at line(s) {string.Join(", ", lines)}.");
            }
        }

        stale.Length.ShouldBe(
            0,
            "SilentCatchAllGuardAllowlist is out of date. It is shrink-only: lower or remove an entry after a " +
            "cleanup; a count that grew must be fixed in the source (catch the specific type, log or rethrow), " +
            $"not raised here.{Environment.NewLine}{stale}{Environment.NewLine}" +
            $"Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Scanner_CountsOnlyCatchAllBlocksWithoutLogOrThrow()
    {
        const string source = """
            using System;
            public class Sample
            {
                public void Run()
                {
                    try { Work(); } catch (Exception) { }
                    try { Work(); } catch { return; }
                    try { Work(); } catch (System.Exception ex) when (ex.Message.Length > 0) { Result = ex.Message; }
                    try { Work(); } catch (global::System.Exception) { }
                    try { Work(); } catch (Exception ex) { _logger.LogDebug(ex, "x"); }
                    try { Work(); } catch (Exception ex) { LogUnreadable(ex); }
                    try { Work(); } catch (Exception ex) { _logger?.LogWarning(ex, "x"); }
                    try { Work(); } catch (Exception) { throw; }
                    try { Work(); } catch (Exception ex) { throw new InvalidOperationException("x", ex); }
                    try { Work(); } catch (Exception ex) { value = y ?? throw ex; }
                    try { Work(); } catch (FormatException) { }
                    try { Work(); } catch (IOException) { }
                    try { Work(); } catch (Exception ex) { Logout(); }
                    try { Work(); } catch (Exception ex) { Action a = () => _logger.LogError(ex, "x"); }
                    try { Work(); } catch (Exception ex) { void Local() { _logger.LogError(ex, "x"); } }
                    try { Work(); } catch (Exception ex) { Func<int> f = () => throw ex; }
                    try { Work(); } catch (Exception) { try { Work(); } catch (Exception inner) { _logger.LogError(inner, "x"); } }
                    try { Work(); } catch when (flag) { }
                    try { Work(); } catch (global::Exception) { }
                    try { Work(); } catch (SystemException) { }
                    try { Work(); } catch (MySystem.Exception) { }
                }
            }
            """;

        var lines = SilentCatchAllScanner.FindSilentCatchAllLines(source);

        lines.ShouldBe(new[] { 6, 7, 8, 9, 18, 19, 20, 21, 23, 24 });
    }

    private static (Dictionary<string, IReadOnlyList<int>> Hits, int ScannedFiles) ScanApi()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        if (!Directory.Exists(apiRoot))
        {
            throw new DirectoryNotFoundException(
                $"Api project directory '{apiRoot}' does not exist. The guard would silently pass, so this is treated as a failure.");
        }

        var hits = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath.Split('/').Any(segment => ExcludedDirectoryNames.Contains(segment, StringComparer.Ordinal)))
            {
                continue;
            }

            scannedFiles++;

            var lines = SilentCatchAllScanner.FindSilentCatchAllLines(File.ReadAllText(file));
            if (lines.Count > 0)
            {
                hits[relativePath] = lines;
            }
        }

        return (hits, scannedFiles);
    }

    private static string RenderTable(Dictionary<string, IReadOnlyList<int>> hits)
    {
        var table = new StringBuilder();
        foreach (var (path, lines) in hits.OrderBy(hit => hit.Key, StringComparer.Ordinal))
        {
            table.AppendLine($"        [\"{path}\"] = {lines.Count},");
        }

        return table.ToString();
    }
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard against the fresh-install crash class found 2026-09-03:
/// Infrastructure/Persistence/Seed/*.cs runs unconditionally on every fresh installation
/// (DatabaseInitializer.SeedDataAsync -> DataSeeder.Add), AFTER every migration under
/// Infrastructure/Persistence/Migrations already ran. Several migrations insert fixed-id rows with
/// their own idempotency guard (INSERT ... SELECT ... WHERE NOT EXISTS) so the same statement is also
/// safe to run again on an existing database. A seed file that inserts one of those SAME ids through a
/// plain, unguarded INSERT ... VALUES hits Postgres 23505 (duplicate key) on a fresh install, because
/// the migration already created the row moments earlier in the same transaction. MacrosSeed.cs
/// ('Vacation50%' / 'Paid Absence') and AbsencesSeed.cs ('Schulung unbezahlt') both had this bug.
///
/// A source scan is used deliberately: the collision is a data fact spread across two independently
/// evolving raw-SQL string literals (migration vs. seed), not something reflection or the EF model can
/// see - both sides are `migrationBuilder.Sql(...)` text, never HasData/fluent seeding.
///
/// Detection heuristic: within every `INSERT INTO ... ;` statement block, the leading value of each
/// VALUES row / INSERT...SELECT projection is a GUID literal directly preceded by '(' or 'SELECT ' -
/// which holds because every affected table's `id` column is listed first, matching the id-first
/// convention documented in reference_klacks-repository-savechanges-two-conventions and every INSERT
/// this guard has inspected. A seed statement counts as guarded when its own block text contains
/// "WHERE NOT EXISTS" or "ON CONFLICT" - matching the idiom migrations already use (see
/// SplitTrainingAndWirePaidAbsence.cs, WireAbsenceMacrosToPercentVariable.cs).
///
/// Scope note - what this guard does NOT cover:
/// - IDs inserted via `gen_random_uuid()` (skill_phrase, most `settings` rows): no literal to collide
///   on, so migrations using it are invisible here by design - they cannot produce this crash class.
/// - UPDATE/DELETE statements, including `WHERE id IN (...)`: only text following the literal
///   "INSERT INTO" keyword up to the next ';' is scanned, so an UPDATE's id list is never mistaken for
///   an insert.
/// - A migration statement's own guard is not evaluated - migrations are out of scope for this bug
///   (the fix lives in Seed/*.cs, never in Migrations/*.cs), only the ids they insert are catalogued.
/// - Multiple ';' inside one logical SQL statement (none of the currently scanned statements contain
///   one outside a string terminator) and GUIDs embedded in non-leading positions of a row.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class SeedMigrationCollisionGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string MigrationsDirectory = "Infrastructure/Persistence/Migrations";
    private const string SeedDirectory = "Infrastructure/Persistence/Seed";
    private const string SourceFilePattern = "*.cs";
    private const string DesignerSuffix = ".Designer.cs";
    private const string ModelSnapshotMarker = "ModelSnapshot";
    private const int MinimumScannedMigrationFiles = 50;
    private const int MinimumScannedSeedFiles = 20;
    private const int MinimumCatalogedMigrationInserts = 3;

    private static readonly Regex InsertStatementBlock =
        new(@"INSERT\s+INTO\s+(?:public\.)?(?<table>[A-Za-z_][A-Za-z0-9_]*)[\s\S]*?;", RegexOptions.Compiled);

    private static readonly Regex LeadingRowId =
        new(@"(?:\(|SELECT)\s*'(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})'",
            RegexOptions.Compiled);

    [Test]
    public void SeedFiles_MustNotUnguardedInsertAnIdThatAMigrationAlreadyInserted()
    {
        var apiRoot = LocateApiProject();
        var migrationInserts = ScanInsertedIds(Path.Combine(apiRoot, MigrationsDirectory), apiRoot, out var scannedMigrationFiles);
        var seedInserts = ScanInsertedIds(Path.Combine(apiRoot, SeedDirectory), apiRoot, out var scannedSeedFiles);

        scannedMigrationFiles.ShouldBeGreaterThan(
            MinimumScannedMigrationFiles,
            $"Only {scannedMigrationFiles} migration source files were scanned - the guard cannot have " +
            "inspected the real migrations directory, so a green result would be meaningless.");

        scannedSeedFiles.ShouldBeGreaterThan(
            MinimumScannedSeedFiles,
            $"Only {scannedSeedFiles} seed source files were scanned - the guard cannot have inspected " +
            "the real seed directory, so a green result would be meaningless.");

        var migrationInsertedIds = migrationInserts
            .SelectMany(row => row.RowIds.Select(id => (row.Table, Id: id)))
            .ToHashSet();

        migrationInsertedIds.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumCatalogedMigrationInserts,
            "The scan catalogued too few fixed-id INSERTs from migrations. Either the migrations " +
            "directory changed shape or the scan regressed - in both cases an empty violation list " +
            "below proves nothing.");

        var violations = new List<(string File, int Line, string Table, string Id)>();
        foreach (var seedRow in seedInserts)
        {
            if (seedRow.IsGuarded)
            {
                continue;
            }

            foreach (var id in seedRow.RowIds)
            {
                if (migrationInsertedIds.Contains((seedRow.Table, id)))
                {
                    violations.Add((seedRow.File, seedRow.Line, seedRow.Table, id));
                }
            }
        }

        var report = new StringBuilder();
        foreach (var violation in violations)
        {
            report.AppendLine($"  {violation.File}:{violation.Line} -> table '{violation.Table}', id {violation.Id}");
        }

        violations.ShouldBeEmpty(
            "A seed file inserts a fixed id, via a plain unguarded INSERT ... VALUES, that a migration " +
            "already inserts with its own WHERE NOT EXISTS / ON CONFLICT guard. On a fresh install " +
            "migrations always run first, so the seed's insert hits Postgres 23505 (duplicate key) and " +
            "the whole seed transaction rolls back. Guard the seed row the same way the migration " +
            $"guards its own copy: INSERT ... SELECT ... WHERE NOT EXISTS (or ON CONFLICT (id) DO " +
            $"NOTHING).{Environment.NewLine}{report}");
    }

    [Test]
    public void KnownHistoricalCollisions_AreCatalogedAsGuardedInSeeds()
    {
        var apiRoot = LocateApiProject();
        var seedInserts = ScanInsertedIds(Path.Combine(apiRoot, SeedDirectory), apiRoot, out _);

        var knownFixedIds = new[]
        {
            ("macro", "7c5a9d21-4e8b-4f3a-9c67-2d1e8f5b0a43"),
            ("macro", "9f2b4c67-3d1a-4e85-b7c9-5a8d0e6f2b31"),
            ("absence", "4a7e2c91-6b3f-4d58-9e0a-1c5b8f7d3a62")
        };

        foreach (var (table, id) in knownFixedIds)
        {
            var matchingRows = seedInserts.Where(row => row.Table == table && row.RowIds.Contains(id)).ToList();

            matchingRows.ShouldNotBeEmpty(
                $"Expected to still find id {id} seeded for table '{table}'. If the row was " +
                "intentionally removed from the seed, this sanity anchor must be updated too - " +
                "otherwise the guard above could be passing only because it no longer sees real data.");

            matchingRows.ShouldAllBe(
                row => row.IsGuarded,
                $"id {id} in table '{table}' must be inserted through a guarded statement (WHERE NOT " +
                "EXISTS / ON CONFLICT) because a migration already inserts the same fixed id.");
        }
    }

    private static IReadOnlyList<InsertRow> ScanInsertedIds(string directory, string apiRoot, out int scannedFiles)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Guarded directory '{directory}' does not exist. The guard would silently pass, so " +
                "this is treated as a failure.");
        }

        var rows = new List<InsertRow>();
        scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(directory, SourceFilePattern, SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(DesignerSuffix, StringComparison.Ordinal) ||
                fileName.Contains(ModelSnapshotMarker, StringComparison.Ordinal))
            {
                continue;
            }

            scannedFiles++;
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);

            foreach (Match block in InsertStatementBlock.Matches(text))
            {
                var table = block.Groups["table"].Value;
                var blockText = block.Value;
                var isGuarded =
                    blockText.Contains("WHERE NOT EXISTS", StringComparison.OrdinalIgnoreCase) ||
                    blockText.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase);

                var rowIds = LeadingRowId.Matches(blockText)
                    .Select(m => m.Groups["uuid"].Value.ToLowerInvariant())
                    .Distinct()
                    .ToList();

                if (rowIds.Count == 0)
                {
                    continue;
                }

                var line = text[..block.Index].Count(c => c == '\n') + 1;
                rows.Add(new InsertRow(relative, line, table, isGuarded, rowIds));
            }
        }

        return rows;
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, MigrationsDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }

    private sealed record InsertRow(string File, int Line, string Table, bool IsGuarded, IReadOnlyList<string> RowIds);
}

// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Data.Seed;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class FakeSeedDumpTableFilterTests
{
    private const string InsertLine =
        "INSERT INTO public.membership VALUES ('a', 'b', 2, '2024-08-18 00:00:00+00', NULL, '2026-03-22 12:03:55.26562+01', 'Anonymus', NULL, NULL, NULL, false, NULL);";

    private static readonly IReadOnlySet<string> MembershipOnly =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "membership" };

    [Test]
    public void RemoveTables_QualifiedTableName_IsRemoved()
    {
        var dump = InsertLine + "\n" + "INSERT INTO public.client VALUES ('x');\n";

        var result = FakeSeedDumpTableFilter.RemoveTables(dump, MembershipOnly);

        result.ShouldNotContain("INSERT INTO public.membership");
        result.ShouldContain("INSERT INTO public.client");
    }

    [Test]
    public void RemoveTables_BareTableName_IsRemoved()
    {
        var dump = "INSERT INTO group_item (id, client_id, group_id) SELECT 'a', 'b', g.id FROM \"group\" g WHERE g.name = 'ZH' LIMIT 1;\n"
            + "INSERT INTO client_contract (id, client_id) SELECT 'a', 'b';\n";
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "group_item" };

        var result = FakeSeedDumpTableFilter.RemoveTables(dump, excluded);

        result.ShouldNotContain("INSERT INTO group_item");
        result.ShouldContain("INSERT INTO client_contract");
    }

    [Test]
    public void RemoveTables_QuotedTableName_IsRemoved()
    {
        var dump = "INSERT INTO public.\"membership\" VALUES ('a');\n";

        var result = FakeSeedDumpTableFilter.RemoveTables(dump, MembershipOnly);

        result.ShouldNotContain("INSERT INTO");
    }

    [Test]
    public void RemoveTables_CommentsAndSetStatements_ArePreserved()
    {
        var dump = "-- header comment\nSET client_encoding = 'UTF8';\n" + InsertLine + "\n";

        var result = FakeSeedDumpTableFilter.RemoveTables(dump, MembershipOnly);

        result.ShouldContain("-- header comment");
        result.ShouldContain("SET client_encoding = 'UTF8';");
        result.ShouldNotContain("INSERT INTO public.membership");
    }

    [Test]
    public void RemoveTables_NoExcludedTables_ReturnsDumpUnchanged()
    {
        var dump = InsertLine + "\nINSERT INTO public.client VALUES ('x');\n";

        var result = FakeSeedDumpTableFilter.RemoveTables(dump, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        result.ShouldBe(dump);
    }

    [Test]
    public void RemoveTables_RealFakeSeedDump_DropsOnlyGroupItem()
    {
        var dump = StaticFakeDataLoader.LoadSeedDump("fake_seed_5000.sql");

        var filtered = FakeSeedDumpTableFilter.RemoveTables(dump, FakeSeedExcludedTables.ShiftsAndGroupsTables);

        CountInserts(filtered, "group_item").ShouldBe(0);
        CountInserts(filtered, "public.membership").ShouldBe(5000);
        CountInserts(filtered, "public.client").ShouldBe(5000);
        CountInserts(filtered, "public.address").ShouldBe(5000);
        CountInserts(filtered, "public.communication").ShouldBe(20150);
        CountInserts(filtered, "public.annotation").ShouldBe(7517);
        CountInserts(filtered, "client_contract").ShouldBe(3228);
    }

    private static int CountInserts(string sql, string tableName)
    {
        var prefix = "INSERT INTO " + tableName + " ";
        return sql.Split('\n').Count(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

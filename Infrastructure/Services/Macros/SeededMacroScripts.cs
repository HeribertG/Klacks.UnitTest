// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Reads the template macros shipped with Klacks straight from the seed code (MacrosSeed plus the
/// AddAllShiftAdditiveMacro migration), so tests work with the real scripts and names instead of copies.
/// AllShiftWednesdayBonusBlock builds a block appended to AllShift that adds a Wednesday surcharge on channel 13
/// and sets the result channel 1 to the original total plus that surcharge, recomputing the total from the IMPORT
/// symbols through the given function: the original FUNCTION of AllShift, or a copy of it under another name that the
/// block declares itself. WednesdayBonusReadingTheOriginal is the shorter block appended to AllShift that reads what
/// AllShift already computed: its total variable TotalBonus and its FUNCTION SegBonusForType.
/// ShippedScriptVariants lists every macro script literal Klacks has shipped (the MacrosSeed rows and the scripts the
/// macro-content migrations insert, rewrite or match), keyed by source and position so the keys do not depend on the
/// line endings of the checkout.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

public static class SeededMacroScripts
{
    private const string SqlEscapedQuote = "''";
    private const string SqlQuote = "'";
    private const string IdGroup = "id";
    private const string NameGroup = "name";
    private const string ContentGroup = "content";

    public const string OriginalFunctionName = "SegBonusForType";
    public const string CopiedFunctionName = "BlockSegBonus";

    private const string FunctionStart = "FUNCTION " + OriginalFunctionName;
    private const string FunctionEnd = "ENDFUNCTION";

    private const string WednesdayBonusMain =
        "DIM WednesdayBonus, NewTotal\n"
        + "WednesdayBonus = 0\n"
        + "IF Weekday = 3 THEN WednesdayBonus = Hour * 0.1 ENDIF\n"
        + "IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN\n"
        + "NewTotal = (F(FromHour, \"00:00\", Holiday, Weekday, 10) + F(\"00:00\", UntilHour, HolidayNextDay, (Weekday MOD 7) + 1, 10))"
        + " + (F(FromHour, \"00:00\", Holiday, Weekday, 11) + F(\"00:00\", UntilHour, HolidayNextDay, (Weekday MOD 7) + 1, 11))"
        + " + (F(FromHour, \"00:00\", Holiday, Weekday, 12) + F(\"00:00\", UntilHour, HolidayNextDay, (Weekday MOD 7) + 1, 12))"
        + " + (F(FromHour, \"00:00\", Holiday, Weekday, 13) + F(\"00:00\", UntilHour, HolidayNextDay, (Weekday MOD 7) + 1, 13))"
        + " + (F(FromHour, \"00:00\", Holiday, Weekday, 14) + F(\"00:00\", UntilHour, HolidayNextDay, (Weekday MOD 7) + 1, 14))\n"
        + "ELSE\n"
        + "NewTotal = F(FromHour, UntilHour, Holiday, Weekday, 10)"
        + " + F(FromHour, UntilHour, Holiday, Weekday, 11)"
        + " + F(FromHour, UntilHour, Holiday, Weekday, 12)"
        + " + F(FromHour, UntilHour, Holiday, Weekday, 13)"
        + " + F(FromHour, UntilHour, Holiday, Weekday, 14)\n"
        + "ENDIF\n"
        + "NewTotal = Round(NewTotal, 2) + WednesdayBonus\n"
        + "OUTPUT 13, WednesdayBonus\n"
        + "OUTPUT 1, NewTotal";

    private const string FunctionPlaceholder = "F(";

    public const string WednesdayBonusReadingTheOriginal =
        "DIM WednesdayBonus\n"
        + "WednesdayBonus = 0\n"
        + "IF Weekday = 3 THEN WednesdayBonus = Hour * 0.1 + " + OriginalFunctionName + "(FromHour, UntilHour, Holiday, Weekday, 10) ENDIF\n"
        + "OUTPUT 13, WednesdayBonus\n"
        + "OUTPUT 1, Round(TotalBonus, 2) + WednesdayBonus";

    private const string UpSuffix = ":Up";
    private const string DownSuffix = ":Down";
    private const string KeySeparator = ":";

    private static readonly Regex SqlLiteral = new(@"'(?<content>(?:[^']|'')*)'", RegexOptions.Compiled);

    private static readonly Regex OutputStatement = new(
        @"^\s*OUTPUT\s", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex SeedRow = new(
        @"SELECT\s+'(?<id>[^']*)',\s*'(?<name>[^']*)',\s*'(?<content>(?:[^']|'')*)'",
        RegexOptions.Compiled);

    public static string AllShiftScript() =>
        Rows().Single(row => row.Id == SeededMacroIds.AllShift).Content;

    public static string AllShiftWednesdayBonusBlock(string functionName)
    {
        var main = WednesdayBonusMain.Replace(FunctionPlaceholder, functionName + "(");
        if (functionName == OriginalFunctionName)
        {
            return main;
        }

        var script = AllShiftScript();
        var start = script.IndexOf(FunctionStart, StringComparison.Ordinal);
        var end = script.IndexOf(FunctionEnd, start, StringComparison.Ordinal) + FunctionEnd.Length;
        var copiedFunction = script[start..end].Replace(OriginalFunctionName, functionName);
        return copiedFunction + "\n" + main;
    }

    public static IReadOnlyDictionary<Guid, string> NamesById() =>
        Rows().ToDictionary(row => row.Id, row => row.Name);

    public static IReadOnlyList<(string Key, string Content)> ShippedScriptVariants()
    {
        var seedBuilder = new MigrationBuilder(null);
        MacrosSeed.SeedData(seedBuilder);
        var sources = new List<(string Name, IEnumerable<MigrationOperation> Operations)>
        {
            (nameof(MacrosSeed), seedBuilder.Operations)
        };

        foreach (var migration in MacroContentMigrations())
        {
            var name = migration.GetType().Name;
            sources.Add((name + UpSuffix, migration.UpOperations));
            sources.Add((name + DownSuffix, migration.DownOperations));
        }

        return sources
            .SelectMany(source => source.Operations
                .OfType<SqlOperation>()
                .SelectMany(operation => SqlLiteral.Matches(operation.Sql).Select(match => match.Groups[ContentGroup].Value))
                .Where(literal => OutputStatement.IsMatch(literal))
                .Select((literal, index) => (source.Name + KeySeparator + index, literal.Replace(SqlEscapedQuote, SqlQuote))))
            .ToList();
    }

    private static IEnumerable<Migration> MacroContentMigrations() =>
    [
        new AddSurchargeNightWindow(),
        new AddAllShiftAdditiveMacro(),
        new WireAbsenceMacrosToPercentVariable(),
        new SplitTrainingAndWirePaidAbsence()
    ];

    private static IEnumerable<(Guid Id, string Name, string Content)> Rows()
    {
        var builder = new MigrationBuilder(null);
        MacrosSeed.SeedData(builder);
        var operations = builder.Operations.Concat(new AddAllShiftAdditiveMacro().UpOperations);

        return operations
            .OfType<SqlOperation>()
            .Select(operation => SeedRow.Match(operation.Sql))
            .Where(match => match.Success)
            .Select(match => (
                Guid.Parse(match.Groups[IdGroup].Value),
                match.Groups[NameGroup].Value.Replace(SqlEscapedQuote, SqlQuote),
                match.Groups[ContentGroup].Value.Replace(SqlEscapedQuote, SqlQuote)));
    }
}

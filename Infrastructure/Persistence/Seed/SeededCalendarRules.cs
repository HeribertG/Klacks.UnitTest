// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Reads the national holiday rules of one country from the SQL that CalendarRulesSeed and
/// AdditionalCalendarRulesSeed really emit, and computes a year with the production HolidaysListCalculator.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Holidays;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

internal static class SeededCalendarRules
{
    private const string EnglishLanguage = "en";
    private const string EscapedApostrophe = "''";
    private const string Apostrophe = "'";

    private static readonly Regex SeedRow = new(
        "\\('(?<id>[0-9a-f-]{36})', '(?<rule>[^']*)', '(?<sub>[^']*)', (?<mandatory>true|false), (?<paid>true|false), "
        + "'(?<state>[^']*)', '(?<country>[^']*)', '(?:[^']|'')*', '(?<name>(?:[^']|'')*)'\\)",
        RegexOptions.Compiled);

    public static List<DateOnly> Compute(string country, int year)
    {
        return Compute(Load(country), year);
    }

    public static List<DateOnly> Compute(IEnumerable<CalendarRule> rules, int year)
    {
        var calculator = new HolidaysListCalculator { CurrentYear = year };
        calculator.AddRange(rules.ToList());
        calculator.ComputeHolidays();

        return calculator.HolidayList.Select(holiday => holiday.CurrentDate).OrderBy(date => date).ToList();
    }

    public static List<CalendarRule> Load(string country)
    {
        var builder = new MigrationBuilder(activeProvider: null);
        CalendarRulesSeed.SeedData(builder);
        AdditionalCalendarRulesSeed.SeedData(builder);

        var rules = new List<CalendarRule>();
        foreach (var sql in builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql))
        {
            foreach (Match match in SeedRow.Matches(sql))
            {
                if (match.Groups["country"].Value != country)
                {
                    continue;
                }

                using var name = JsonDocument.Parse(match.Groups["name"].Value.Replace(EscapedApostrophe, Apostrophe));
                rules.Add(new CalendarRule
                {
                    Id = Guid.Parse(match.Groups["id"].Value),
                    Rule = match.Groups["rule"].Value,
                    SubRule = match.Groups["sub"].Value,
                    IsMandatory = match.Groups["mandatory"].Value == "true",
                    IsPaid = match.Groups["paid"].Value == "true",
                    State = match.Groups["state"].Value,
                    Country = country,
                    Name = new MultiLanguage { En = name.RootElement.GetProperty(EnglishLanguage).GetString() },
                });
            }
        }

        return rules;
    }
}

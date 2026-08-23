// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the seeded group detail report template. The template ships as raw SQL, so nothing but a
/// test proves that its JSON parses, that its id does not collide with the other seeded templates,
/// that it stays single-section (a non-schedule source renders every row into every table) and that
/// the REPORT_DEFAULT_TEMPLATES merge keeps an existing administrator assignment.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Seed;

[TestFixture]
public class EditGroupReportTemplateSeedTests
{
    private const string SourceId = "edit-group";
    private const int WorkTableSectionType = 1;

    private static List<string> ApplyStatements()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        EditGroupReportTemplatesSql.Apply(builder);
        return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
    }

    private static JsonElement SectionsOfSeededTemplate()
    {
        var insert = ApplyStatements().Single(s => s.Contains("INSERT INTO public.report_templates"));
        var match = Regex.Match(insert, @"'(\[\{""Id"".*?\}\])',\s*'\[\]'", RegexOptions.Singleline);
        match.Success.ShouldBeTrue("the INSERT must carry a sections JSON array");
        return JsonDocument.Parse(match.Groups[1].Value).RootElement;
    }

    [Test]
    public void Apply_InsertsTheTemplateForTheEditGroupSource()
    {
        var insert = ApplyStatements().Single(s => s.Contains("INSERT INTO public.report_templates"));

        insert.ShouldContain($"'{EditGroupReportTemplatesSql.GroupDetailTemplateId}'");
        insert.ShouldContain($"'{SourceId}'");
        insert.ShouldContain(@"'[""details""]'");
        insert.ShouldContain("ON CONFLICT (id) DO NOTHING");
    }

    [Test]
    public void TemplateId_DoesNotCollideWithTheOtherSeededTemplates()
    {
        var taken = new[]
        {
            ClientAvailabilityReportTemplatesSql.ClientAvailabilityListTemplateId,
            ClientAvailabilityReportTemplatesSql.ClientAvailabilityDetailTemplateId,
        };

        taken.ShouldNotContain(EditGroupReportTemplatesSql.GroupDetailTemplateId);
        Guid.TryParse(EditGroupReportTemplatesSql.GroupDetailTemplateId, out _).ShouldBeTrue();
    }

    [Test]
    public void Sections_ContainExactlyOneRowRenderingTable()
    {
        var sections = SectionsOfSeededTemplate();

        var tables = sections.EnumerateArray()
            .Count(s => s.GetProperty("Type").GetInt32() == WorkTableSectionType);

        tables.ShouldBe(1, "a non-schedule source renders all rows into every table section");
    }

    [Test]
    public void Sections_TableColumnWidthsSumToOneHundred()
    {
        var table = SectionsOfSeededTemplate().EnumerateArray()
            .Single(s => s.GetProperty("Type").GetInt32() == WorkTableSectionType);

        var total = table.GetProperty("Fields").EnumerateArray()
            .Sum(f => f.GetProperty("Width").GetInt32());

        total.ShouldBe(100);
    }

    [Test]
    public void Sections_OnlyBindToGroupDetailFields()
    {
        var allowedPrefixes = new[] { "group.", "groupMember.", "report." };

        var bindings = SectionsOfSeededTemplate().EnumerateArray()
            .SelectMany(s => s.GetProperty("Fields").EnumerateArray())
            .Select(f => f.GetProperty("DataBinding").GetString()!)
            .ToList();

        bindings.ShouldNotBeEmpty();
        foreach (var binding in bindings)
        {
            allowedPrefixes.Any(binding.StartsWith).ShouldBeTrue($"unexpected data binding '{binding}'");
        }
    }

    [Test]
    public void DefaultTemplatesMerge_LetsTheExistingAssignmentWin()
    {
        var update = ApplyStatements().Single(s => s.Contains("UPDATE public.settings"));

        update.ShouldContain("::jsonb || value::jsonb");
        update.ShouldContain("REPORT_DEFAULT_TEMPLATES");
        update.ShouldContain($@"""{SourceId}"": ""{EditGroupReportTemplatesSql.GroupDetailTemplateId}""");
    }

    [Test]
    public void Remove_DeletesOnlyTheSeededTemplate()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        EditGroupReportTemplatesSql.Remove(builder);

        var sql = builder.Operations.OfType<SqlOperation>().Single().Sql;

        sql.ShouldContain("DELETE FROM public.report_templates");
        sql.ShouldContain(EditGroupReportTemplatesSql.GroupDetailTemplateId);
    }
}

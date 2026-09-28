// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the column width of ExportLog.Format against every key that is written into it: the export format keys
/// (ExportConstants.Format*), the payroll target-system keys (PayrollExportConstants.FormatKey*) and, because the
/// payroll hook copies it verbatim, the declared width of PayrollExportGroupConfig.TargetSystem. Live 2026-09-26 the
/// former width of 16 made every payroll export after a group seal fail with PostgreSQL 22001 on
/// datev-lug-bewegungsdaten (24 characters). The EF model is checked as well, so the attribute cannot drift from the
/// migrated column.
/// </summary>

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ExportLogFormatLengthGuardTests
{
    private const string ExportFormatPrefix = "Format";
    private const string PayrollFormatPrefix = "FormatKey";
    private const string ModelOnlyConnectionString = "Host=localhost;Port=1;Database=model-only";

    private static IEnumerable<(string Name, string Value)> StringConstants(Type type, string prefix) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(field => (field.Name, (string)field.GetRawConstantValue()!));

    private static int MaxLengthOf(Type type, string property) =>
        type.GetProperty(property)!.GetCustomAttribute<MaxLengthAttribute>()!.Length;

    [Test]
    public void EveryExportAndPayrollFormatKey_FitsIntoExportLogFormat()
    {
        var keys = StringConstants(typeof(ExportConstants), ExportFormatPrefix)
            .Concat(StringConstants(typeof(PayrollExportConstants), PayrollFormatPrefix))
            .ToList();

        keys.ShouldNotBeEmpty();
        keys.Where(key => key.Value.Length > ExportLogLimits.FormatMaxLength)
            .Select(key => $"{key.Name} = '{key.Value}' ({key.Value.Length})")
            .ShouldBeEmpty();
        keys.ShouldContain(key => key.Value == PayrollExportConstants.FormatKeyDatevLug);
    }

    [Test]
    public void ExportLogFormat_IsAtLeastAsWideAsThePayrollTargetSystem()
    {
        MaxLengthOf(typeof(ExportLog), nameof(ExportLog.Format)).ShouldBe(ExportLogLimits.FormatMaxLength);
        MaxLengthOf(typeof(PayrollExportGroupConfig), nameof(PayrollExportGroupConfig.TargetSystem))
            .ShouldBeLessThanOrEqualTo(ExportLogLimits.FormatMaxLength);
    }

    [Test]
    public void TheEfModel_MapsExportLogFormatWithTheWideLimit()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(ModelOnlyConnectionString)
            .Options;
        using var context = new DataBaseContext(options, Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>());

        var property = context.Model.FindEntityType(typeof(ExportLog))!.FindProperty(nameof(ExportLog.Format))!;

        property.GetMaxLength().ShouldBe(ExportLogLimits.FormatMaxLength);
    }
}

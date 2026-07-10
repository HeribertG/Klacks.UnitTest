// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Exports;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Domain.Models.Exports;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ExportFormatOverridePatchTests
{
    [Test]
    public void Parse_rejects_non_object_root()
    {
        Should.Throw<FormatException>(() => ExportFormatOverridePatch.Parse("[1,2]"));
    }

    [Test]
    public void Parse_rejects_non_string_values()
    {
        Should.Throw<FormatException>(() => ExportFormatOverridePatch.Parse("""{"dateFormat":42}"""));
    }

    [Test]
    public void Validate_reports_unknown_keys_for_family()
    {
        var errors = ExportFormatOverridePatch.Validate("""{"delimiter":","}""", ExportConstants.FormatFamilyOrder);

        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("delimiter");
        errors[0].ShouldContain(ExportConstants.FormatFamilyOrder);
    }

    [Test]
    public void Validate_reports_invalid_json_as_single_error()
    {
        var errors = ExportFormatOverridePatch.Validate("{not json", ExportConstants.FormatFamilyOrder);

        errors.Count.ShouldBe(1);
    }

    [Test]
    public void Validate_reports_empty_patch()
    {
        var errors = ExportFormatOverridePatch.Validate("{}", ExportConstants.FormatFamilyPayroll);

        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("no values");
    }

    [Test]
    public void Validate_accepts_payroll_keys_including_absence_mapping()
    {
        var patch = """{"delimiter":",","encoding":"utf-8","absenceMapping":{"x":{"WageType":"1"}}}""";

        ExportFormatOverridePatch.Validate(patch, ExportConstants.FormatFamilyPayroll).ShouldBeEmpty();
    }

    [Test]
    public void Validate_rejects_absence_mapping_for_order_family()
    {
        var errors = ExportFormatOverridePatch.Validate("""{"absenceMapping":{}}""", ExportConstants.FormatFamilyOrder);

        errors.ShouldContain(e => e.Contains(ExportOverrideConstants.KeyAbsenceMapping));
    }

    [Test]
    public void ApplyTo_options_overrides_only_present_keys()
    {
        var options = new ExportOptions { DateFormat = "dd.MM.yyyy", TimeFormat = "HH:mm", CurrencyCode = "EUR", Language = "de" };
        var patch = ExportFormatOverridePatch.Parse("""{"dateFormat":"yyyy/MM/dd","currencyCode":"CHF"}""");

        patch.ApplyTo(options);

        options.DateFormat.ShouldBe("yyyy/MM/dd");
        options.CurrencyCode.ShouldBe("CHF");
        options.TimeFormat.ShouldBe("HH:mm");
        options.Language.ShouldBe("de");
    }

    [Test]
    public void ApplyTo_config_overrides_scalars_and_replaces_absence_mapping()
    {
        var config = ExportSampleDataFactory.CreatePayrollSampleConfig("generic-payroll-csv");
        var patch = ExportFormatOverridePatch.Parse("""{"delimiter":"|","absenceMapping":{"abc":{"WageType":"9"}}}""");

        patch.ApplyTo(config);

        config.Delimiter.ShouldBe("|");
        config.Encoding.ShouldBe("windows-1252");
        config.AbsenceMappingJson.ShouldBe("""{"abc":{"WageType":"9"}}""");
    }
}

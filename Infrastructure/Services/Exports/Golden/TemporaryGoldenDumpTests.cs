// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// TEMPORARY: writes the rendered xlsx goldens of logo-bordro-tr and winmentor-ro as base64 to the
/// test progress log so they can be committed from a CI run. Remove before merging.
/// </summary>
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports.Golden;

[TestFixture]
public class TemporaryGoldenDumpTests
{
    [Test]
    public void Dump_xlsx_goldens()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        foreach (var formatter in new Klacks.Api.Domain.Interfaces.Exports.IPayrollExportFormatter[] { new LogoBordroTrExportFormatter(), new WinmentorRoExportFormatter() })
        {
            var result = formatter.Format(ExportSampleDataFactory.CreatePayrollSample(), ExportSampleDataFactory.CreatePayrollSampleConfig(formatter.FormatKey));
            TestContext.Progress.WriteLine($"GOLDEN-DUMP {formatter.FormatKey} {Convert.ToBase64String(result.Content)}");
        }
    }
}

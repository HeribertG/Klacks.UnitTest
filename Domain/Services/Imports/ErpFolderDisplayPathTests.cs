// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpFolderDisplayPath: trailing separators of either style are removed for display (independent
/// of the host OS the test runs on), roots keep their separator, and nothing else changes.
/// </summary>

using Klacks.Api.Domain.Services.Imports;

namespace Klacks.UnitTest.Domain.Services.Imports;

[TestFixture]
public class ErpFolderDisplayPathTests
{
    [TestCase(@"C:\ProgramData\Klacks\ErpImport\erp\orders\", @"C:\ProgramData\Klacks\ErpImport\erp\orders")]
    [TestCase(@"C:\ProgramData\Klacks\ErpImport\erp\orders", @"C:\ProgramData\Klacks\ErpImport\erp\orders")]
    [TestCase("/app/ErpImport/erp/orders/", "/app/ErpImport/erp/orders")]
    [TestCase("/app/ErpImport/erp/orders", "/app/ErpImport/erp/orders")]
    [TestCase("/opt/klacks/erp-import/erp/orders//", "/opt/klacks/erp-import/erp/orders")]
    [TestCase(@"C:\erp-import\erp\orders\/", @"C:\erp-import\erp\orders")]
    [TestCase(@"\\server\share\erp\", @"\\server\share\erp")]
    public void WithoutTrailingSeparator_RemovesOnlyTheTrailingSeparators(string path, string expected)
    {
        ErpFolderDisplayPath.WithoutTrailingSeparator(path).ShouldBe(expected);
    }

    [TestCase("/", "/")]
    [TestCase(@"C:\", @"C:\")]
    [TestCase("C:/", "C:/")]
    public void WithoutTrailingSeparator_KeepsTheSeparatorOfARoot(string path, string expected)
    {
        ErpFolderDisplayPath.WithoutTrailingSeparator(path).ShouldBe(expected);
    }

    [Test]
    public void WithoutTrailingSeparator_EmptyPath_StaysEmpty()
    {
        ErpFolderDisplayPath.WithoutTrailingSeparator(string.Empty).ShouldBe(string.Empty);
    }

    [Test]
    public void WithoutTrailingSeparator_AppendingAFileName_DoesNotDoubleTheSeparator()
    {
        var shown = ErpFolderDisplayPath.WithoutTrailingSeparator(@"C:\ProgramData\Klacks\ErpImport\erp\orders\");

        (shown + @"\orders.xml").ShouldBe(@"C:\ProgramData\Klacks\ErpImport\erp\orders\orders.xml");
    }
}

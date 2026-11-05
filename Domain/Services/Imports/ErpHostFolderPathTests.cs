// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpHostFolderPath: a blank hint means unknown, a Linux hint gets forward slashes, a Windows hint
/// (drive letter or backslash, as compose writes KLACKS_INSTALL_DIR/erp-import on Docker Desktop) gets backslashes,
/// and trailing separators never double up.
/// </summary>

using Klacks.Api.Domain.Services.Imports;

namespace Klacks.UnitTest.Domain.Services.Imports;

[TestFixture]
public class ErpHostFolderPathTests
{
    private const string Prefix = "erp/orders/";

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Combine_BlankHint_ReturnsNull(string? hint)
    {
        ErpHostFolderPath.Combine(hint, Prefix).ShouldBeNull();
    }

    [TestCase("/opt/klacks/erp-import", "/opt/klacks/erp-import/erp/orders")]
    [TestCase("/opt/klacks/erp-import/", "/opt/klacks/erp-import/erp/orders")]
    [TestCase(" /srv/erp ", "/srv/erp/erp/orders")]
    public void Combine_LinuxHint_UsesForwardSlashes(string hint, string expected)
    {
        ErpHostFolderPath.Combine(hint, Prefix).ShouldBe(expected);
    }

    [TestCase(@"C:\Klacks/erp-import", @"C:\Klacks\erp-import\erp\orders")]
    [TestCase(@"C:\Klacks\erp-import\", @"C:\Klacks\erp-import\erp\orders")]
    [TestCase("D:/data/erp", @"D:\data\erp\erp\orders")]
    public void Combine_WindowsHint_UsesBackslashes(string hint, string expected)
    {
        ErpHostFolderPath.Combine(hint, Prefix).ShouldBe(expected);
    }

    [Test]
    public void Combine_EmptyPrefix_ReturnsTheHostRoot()
    {
        ErpHostFolderPath.Combine("/opt/klacks/erp-import", "/").ShouldBe("/opt/klacks/erp-import");
    }
}

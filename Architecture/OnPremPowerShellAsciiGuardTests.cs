// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for the PowerShell scripts shipped to customers (Klacks.Api/deploy): every .ps1/.psm1 must be
/// pure ASCII unless it starts with a UTF-8 byte order mark. Windows PowerShell 5.1 reads a UTF-8 file
/// without BOM as ANSI; an em dash then decodes to characters that include a typographic quote, which
/// PowerShell accepts as a string delimiter, so whole code lines are silently swallowed. Incident
/// 2026-10-15: install.ps1 dropped the region setup and the update signing key this way, and every
/// Windows Docker install failed at `docker compose pull`.
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class OnPremPowerShellAsciiGuardTests
{
    private const string DeployDirectory = "deploy";
    private const int AsciiMaximum = 0x7F;

    private static readonly string[] PowerShellPatterns = ["*.ps1", "*.psm1"];
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    [Test]
    public void ShippedPowerShellScripts_MustBeAsciiOrCarryAUtf8Bom()
    {
        var deployRoot = Path.Combine(RepositoryRootLocator.ApiProject, DeployDirectory);
        Directory.Exists(deployRoot).ShouldBeTrue($"Deploy directory not found: {deployRoot}");

        var scripts = PowerShellPatterns
            .SelectMany(pattern => Directory.EnumerateFiles(deployRoot, pattern, SearchOption.AllDirectories))
            .ToList();
        scripts.ShouldNotBeEmpty();

        var problems = new StringBuilder();
        foreach (var script in scripts)
        {
            var bytes = File.ReadAllBytes(script);
            if (bytes.AsSpan().StartsWith(Utf8ByteOrderMark))
            {
                continue;
            }

            var firstNonAscii = Array.FindIndex(bytes, value => value > AsciiMaximum);
            if (firstNonAscii >= 0)
            {
                var line = 1 + bytes.Take(firstNonAscii).Count(value => value == (byte)'\n');
                problems.AppendLine($"{Path.GetRelativePath(deployRoot, script)}: non-ASCII byte in line {line}");
            }
        }

        problems.Length.ShouldBe(0,
            "Windows PowerShell 5.1 misreads UTF-8 without BOM. Replace the characters with ASCII " +
            "(e.g. '-' instead of an em dash):" + Environment.NewLine + problems);
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the copyright-header rule (.claude/rules/code-policies.md) for the Klacks.Api source tree: line 1 of every
/// .cs file is exactly the SPDX copyright line and nothing precedes it - in particular no UTF-8 byte-order mark
/// (EF BB BF), which editors add silently and which makes the first line differ from the expected text while looking
/// identical on screen.
///
/// The files are read as bytes and only the first bytes are compared, so the check does not depend on how a reader
/// decodes or strips a BOM. The line ends at LF, CR LF or the end of the file; text after the header line is
/// not looked at.
///
/// Scope: every .cs file under Klacks.Api except bin/, obj/, Migrations/ (generated, no header) and .claude/
/// (symlinks to other repositories, see SourceFileSizeGuardTests). The allowlist is empty and stays empty: add the
/// header (or strip the BOM) instead of listing a file.
/// </summary>

using System.Text;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class CopyrightHeaderGuardTests
{
    private const string HeaderLine = "// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only";
    private const string SourceFilePattern = "*.cs";
    private const int MinimumScannedFiles = 5000;
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    private const string Rule =
        "Line 1 of every Klacks.Api .cs file must be exactly '" + HeaderLine + "' with no byte-order mark before it. " +
        "Add the header, or strip the leading EF BB BF bytes; do not allowlist the file.";

    private static readonly byte[] HeaderBytes = Encoding.UTF8.GetBytes(HeaderLine);

    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}"
    ];

    private static readonly IReadOnlySet<string> AllowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<IReadOnlyList<string>> ScannedFiles = new(CollectScannedFiles);

    [Test]
    public void ApiSourceTree_IsScannedInFull()
    {
        ScannedFiles.Value.Count.ShouldBeGreaterThan(MinimumScannedFiles,
            "The scan found far fewer files than the Klacks.Api tree holds; the root lookup or the exclusions are wrong.");
    }

    [Test]
    public void EverySourceFile_StartsWithTheCopyrightHeaderAndNoByteOrderMark()
    {
        var root = RepositoryRootLocator.ApiProject;

        var offenders = ScannedFiles.Value
            .Where(path => !AllowedFiles.Contains(Path.GetRelativePath(root, path)))
            .Where(path => !StartsWithHeaderLine(ReadLeadingBytes(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(Rule + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Test]
    public void AllowedFiles_StaysEmpty()
    {
        AllowedFiles.ShouldBeEmpty("The allowlist is empty on purpose; fix the file instead of listing it.");
    }

    [TestCase(HeaderLine, ExpectedResult = true)]
    [TestCase(HeaderLine + "\n\nnamespace X;", ExpectedResult = true)]
    [TestCase(HeaderLine + "\r\n\r\nnamespace X;", ExpectedResult = true)]
    [TestCase(HeaderLine + "\r", ExpectedResult = true)]
    [TestCase("", ExpectedResult = false)]
    [TestCase("namespace X;", ExpectedResult = false)]
    [TestCase("\n" + HeaderLine, ExpectedResult = false)]
    [TestCase("// Copyright (c) Someone Else. SPDX-License-Identifier: AGPL-3.0-only", ExpectedResult = false)]
    [TestCase("// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: MIT", ExpectedResult = false)]
    [TestCase(HeaderLine + " ", ExpectedResult = false)]
    [TestCase(HeaderLine + "x", ExpectedResult = false)]
    public bool HeaderRule_AcceptsTheExactFirstLineOnly(string content)
    {
        return StartsWithHeaderLine(Encoding.UTF8.GetBytes(content));
    }

    [Test]
    public void HeaderRule_RejectsAUtf8ByteOrderMarkBeforeTheHeader()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(HeaderLine + "\n")).ToArray();

        StartsWithHeaderLine(withBom).ShouldBeFalse();
    }

    private static bool StartsWithHeaderLine(byte[] leadingBytes)
    {
        if (leadingBytes.Length < HeaderBytes.Length || !leadingBytes.AsSpan(0, HeaderBytes.Length).SequenceEqual(HeaderBytes))
        {
            return false;
        }

        if (leadingBytes.Length == HeaderBytes.Length)
        {
            return true;
        }

        var next = leadingBytes[HeaderBytes.Length];
        return next == LineFeed || next == CarriageReturn;
    }

    private static byte[] ReadLeadingBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[HeaderBytes.Length + 1];
        var read = 0;
        int chunk;
        while (read < buffer.Length && (chunk = stream.Read(buffer, read, buffer.Length - read)) > 0)
        {
            read += chunk;
        }

        return buffer.AsSpan(0, read).ToArray();
    }

    private static IReadOnlyList<string> CollectScannedFiles()
    {
        return Directory.EnumerateFiles(RepositoryRootLocator.ApiProject, SourceFilePattern, SearchOption.AllDirectories)
            .Where(path => !ExcludedPathFragments.Any(path.Contains))
            .ToList();
    }
}

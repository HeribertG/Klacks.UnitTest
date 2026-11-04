// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the shared zip-bomb guard: an archive within all limits passes; too many entries, too
/// many actually decompressed bytes and an entry above the compression ratio are reported as the matching
/// violation; a small, highly repetitive entry below the ratio threshold passes; no ratio limit means no
/// ratio check; a body that is no archive is reported as unreadable with the library failure attached.
/// </summary>
using System.IO.Compression;
using System.Text;
using Klacks.Api.Infrastructure.Services.Archives;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Archives;

[TestFixture]
public class ZipArchiveLimitGuardTests
{
    private const long OneMegabyte = 1024L * 1024;
    private const int Ratio = 100;

    [Test]
    public void Check_ArchiveWithinLimits_Passes()
    {
        var zip = BuildZip(("a.txt", Encoding.UTF8.GetBytes("hello")));

        var check = ZipArchiveLimitGuard.Check(zip, new ZipArchiveLimits(OneMegabyte, 10, Ratio, OneMegabyte));

        check.IsWithinLimits.ShouldBeTrue();
    }

    [Test]
    public void Check_TooManyEntries_IsReported()
    {
        var zip = BuildZip(("a.txt", [1]), ("b.txt", [2]), ("c.txt", [3]));

        var check = ZipArchiveLimitGuard.Check(zip, new ZipArchiveLimits(OneMegabyte, 2));

        check.Violation.ShouldBe(ZipArchiveLimitViolation.TooManyEntries);
    }

    [Test]
    public void Check_UnpackedBytesAboveCap_IsReported()
    {
        var zip = BuildZip(("big.bin", new byte[2 * OneMegabyte]));

        var check = ZipArchiveLimitGuard.Check(zip, new ZipArchiveLimits(OneMegabyte, 10));

        check.Violation.ShouldBe(ZipArchiveLimitViolation.UnpackedSizeExceeded);
    }

    [Test]
    public void Check_EntryAboveCompressionRatio_IsReported()
    {
        var zip = BuildZip(("zeros.bin", new byte[4 * OneMegabyte]));

        var check = ZipArchiveLimitGuard.Check(zip, new ZipArchiveLimits(100 * OneMegabyte, 10, Ratio, OneMegabyte));

        check.Violation.ShouldBe(ZipArchiveLimitViolation.CompressionRatioExceeded);
    }

    [Test]
    public void Check_SmallRepetitiveEntryBelowThreshold_Passes()
    {
        var zip = BuildZip(("spaces.txt", Encoding.UTF8.GetBytes(new string(' ', 64 * 1024))));

        var check = ZipArchiveLimitGuard.Check(zip, new ZipArchiveLimits(OneMegabyte, 10, Ratio, OneMegabyte));

        check.IsWithinLimits.ShouldBeTrue();
    }

    [Test]
    public void Check_WithoutRatioLimit_DoesNotCheckTheRatio()
    {
        var zip = BuildZip(("zeros.bin", new byte[4 * OneMegabyte]));

        var check = ZipArchiveLimitGuard.Check(zip, new ZipArchiveLimits(100 * OneMegabyte, 10));

        check.IsWithinLimits.ShouldBeTrue();
    }

    [Test]
    public void Check_NoArchive_IsUnreadable()
    {
        var check = ZipArchiveLimitGuard.Check(Encoding.UTF8.GetBytes("not a zip"), new ZipArchiveLimits(OneMegabyte, 10));

        check.Violation.ShouldBe(ZipArchiveLimitViolation.Unreadable);
        check.Cause.ShouldNotBeNull();
    }

    private static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }
}

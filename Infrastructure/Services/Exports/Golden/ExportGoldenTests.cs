// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.Services.Exports.Golden;

using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Domain.Interfaces.Exports;
using NUnit.Framework;
using Shouldly;

/// <summary>
/// Golden regression suite: renders the deterministic sample dataset through every registered
/// export formatter and compares the output byte-for-byte against the checked-in golden file
/// (xlsx: zip-entry-wise, ignoring the volatile docProps timestamps). When a customer reports a
/// broken format, translate the report into a formatter fix; the changed golden diff documents it.
/// Regenerate goldens with KLACKS_UPDATE_GOLDEN=1 and review the diff before committing.
/// </summary>
[TestFixture]
public class ExportGoldenTests
{
    private const string UpdateGoldenEnvVar = "KLACKS_UPDATE_GOLDEN";
    private const string GoldenExtension = ".golden";
    private const string CorePropsEntry = "docProps/core.xml";
    private const string PackageRelsEntry = "_rels/.rels";
    private const string VolatileEntrySuffix = ".psmdcp";
    private const string HexIdPlaceholder = "HEXID";
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly System.Text.RegularExpressions.Regex HexIdPattern = new("R[0-9a-f]{16}|[0-9a-f]{32}");

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public record GoldenCase(string CaseKey, Func<(byte[] Content, string Extension)> Render)
    {
        public override string ToString() => CaseKey;
    }

    public static IEnumerable<GoldenCase> AllFormatterCases()
    {
        var assembly = typeof(IExportFormatter).Assembly;

        foreach (var formatter in Instantiate<IExportFormatter>(assembly))
        {
            var f = formatter;
            yield return new GoldenCase(f.FormatKey, () => (f.Format(ExportSampleDataFactory.CreateOrderSample(), ExportSampleDataFactory.CreateSampleOptions()), f.FileExtension));
        }

        foreach (var formatter in Instantiate<IPayrollExportFormatter>(assembly))
        {
            var f = formatter;
            yield return new GoldenCase(f.FormatKey, () =>
            {
                var result = f.Format(ExportSampleDataFactory.CreatePayrollSample(), ExportSampleDataFactory.CreatePayrollSampleConfig(f.FormatKey));
                return (result.Content, f.FileExtension);
            });
        }

        foreach (var formatter in Instantiate<IClientPeriodExportFormatter>(assembly))
        {
            var f = formatter;
            yield return new GoldenCase(
                $"{ExportOverrideConstants.ClientPeriodFormatKeyPrefix}{f.FormatKey}",
                () => (f.Format(ExportSampleDataFactory.CreateClientPeriodSample(), ExportSampleDataFactory.CreateSampleOptions()), f.FileExtension));
        }
    }

    [TestCaseSource(nameof(AllFormatterCases))]
    public void Formatter_output_matches_golden_file(GoldenCase testCase)
    {
        var (content, extension) = testCase.Render();
        content.ShouldNotBeEmpty($"Formatter '{testCase.CaseKey}' produced no output for the sample dataset.");

        var goldenPath = Path.Combine(GoldenDirectory(), testCase.CaseKey + GoldenExtension);

        if (Environment.GetEnvironmentVariable(UpdateGoldenEnvVar) == "1")
        {
            Directory.CreateDirectory(GoldenDirectory());
            File.WriteAllBytes(goldenPath, content);
            Assert.Pass($"Golden file regenerated: {goldenPath}");
        }

        File.Exists(goldenPath).ShouldBeTrue(
            $"No golden file for format '{testCase.CaseKey}'. Run the suite once with {UpdateGoldenEnvVar}=1, review the generated file and commit it.");

        var golden = File.ReadAllBytes(goldenPath);

        if (content.Length >= ZipMagic.Length && content.Take(ZipMagic.Length).SequenceEqual(ZipMagic))
        {
            CompareZip(golden, content, testCase.CaseKey);
            return;
        }

        if (!golden.SequenceEqual(content))
        {
            var mismatch = FirstMismatch(golden, content);
            Assert.Fail(
                $"Output of '{testCase.CaseKey}' differs from golden file at byte {mismatch} " +
                $"(golden {golden.Length} bytes, actual {content.Length} bytes). " +
                $"If the change is intended, regenerate with {UpdateGoldenEnvVar}=1 and review the diff.");
        }
    }

    [Test]
    public void Every_golden_file_belongs_to_a_registered_formatter()
    {
        var knownKeys = AllFormatterCases().Select(c => c.CaseKey + GoldenExtension).ToHashSet();
        var orphans = Directory.Exists(GoldenDirectory())
            ? Directory.GetFiles(GoldenDirectory(), "*" + GoldenExtension).Select(Path.GetFileName).Where(f => !knownKeys.Contains(f!)).ToList()
            : [];

        orphans.ShouldBeEmpty("Golden files without a registered formatter (format removed? delete the golden file).");
    }

    private static void CompareZip(byte[] golden, byte[] actual, string caseKey)
    {
        var goldenEntries = ReadZipEntries(golden);
        var actualEntries = ReadZipEntries(actual);

        actualEntries.Keys.ShouldBe(goldenEntries.Keys, ignoreOrder: true,
            $"Zip entry set of '{caseKey}' differs from golden file.");

        foreach (var (name, goldenBytes) in goldenEntries)
        {
            if (name == CorePropsEntry || name.EndsWith(VolatileEntrySuffix, StringComparison.Ordinal))
            {
                continue;
            }

            actualEntries[name].SequenceEqual(goldenBytes).ShouldBeTrue(
                $"Zip entry '{name}' of '{caseKey}' differs from golden file. " +
                $"If intended, regenerate with {UpdateGoldenEnvVar}=1.");
        }
    }

    private static Dictionary<string, byte[]> ReadZipEntries(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            if (entry.FullName.EndsWith(VolatileEntrySuffix, StringComparison.Ordinal))
            {
                entries[NormalizeHexIds(entry.FullName)] = NormalizeHexIds(buffer.ToArray());
                continue;
            }

            entries[entry.FullName] = entry.FullName == PackageRelsEntry
                ? NormalizeHexIds(buffer.ToArray())
                : buffer.ToArray();
        }

        return entries;
    }

    private static string NormalizeHexIds(string value) => HexIdPattern.Replace(value, HexIdPlaceholder);

    private static byte[] NormalizeHexIds(byte[] value) =>
        Encoding.UTF8.GetBytes(HexIdPattern.Replace(Encoding.UTF8.GetString(value), HexIdPlaceholder));

    private static IEnumerable<T> Instantiate<T>(Assembly assembly)
    {
        var types = assembly.GetTypes()
            .Where(t => typeof(T).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName);

        foreach (var type in types)
        {
            yield return (T)Activator.CreateInstance(type)!;
        }
    }

    private static int FirstMismatch(byte[] a, byte[] b)
    {
        var max = Math.Min(a.Length, b.Length);
        for (var i = 0; i < max; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return max;
    }

    private static string GoldenDirectory([CallerFilePath] string sourceFile = "")
    {
        var testProjectRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "..", ".."));
        return Path.Combine(testProjectRoot, "TestData", "ExportGolden");
    }
}

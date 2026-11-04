// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard: Klacks.Api may only relax TLS certificate validation at the reviewed places.
/// Trigger 2026-11-04: the generic LLM provider accepted every certificate error for *.apertus.ai,
/// so a network attacker could read the provider API key and prompts with personal data. The provider's
/// certificate validates in the production image, the exception was removed.
///
/// Allowed: OAuth2Service (tolerates errors only for private/loopback IP hosts, e.g. a LAN appliance) and
/// the self API client (only behind the explicit SelfApi:AcceptAnyServerCertificate option).
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class TlsCertificateValidationGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const int MinimumScannedFiles = 500;

    private static readonly string[] ExcludedDirectorySegments = ["Migrations", "obj", "bin"];

    private static readonly string[] ValidationOverrideMarkers =
    [
        "RemoteCertificateValidationCallback",
        "ServerCertificateCustomValidationCallback",
        "DangerousAcceptAnyServerCertificateValidator",
        "ServerCertificateValidationCallback",
    ];

    private static readonly HashSet<string> AllowedFiles =
    [
        "Infrastructure/Services/Identity/OAuth2Service.cs",
        "Infrastructure/Extensions/DomainServiceCollectionExtensions.cs",
    ];

    [Test]
    public void ApiSources_MustNotRelaxCertificateValidationOutsideTheReviewedPlaces()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var violations = new StringBuilder();
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(segment => ExcludedDirectorySegments.Contains(segment)))
            {
                continue;
            }

            scannedFiles++;
            var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (AllowedFiles.Contains(normalized))
            {
                continue;
            }

            var lines = File.ReadAllText(file).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (ValidationOverrideMarkers.Any(marker => lines[i].Contains(marker, StringComparison.Ordinal)))
                {
                    violations.AppendLine($"  {normalized}:{i + 1} -> {lines[i].Trim()}");
                }
            }
        }

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scannedFiles} source files were scanned, so a green result would be meaningless.");

        violations.Length.ShouldBe(
            0,
            "TLS certificate validation must not be relaxed outside the reviewed places:" + Environment.NewLine + violations);
    }

    [Test]
    public void AllowedFiles_StillExist()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;

        foreach (var allowed in AllowedFiles)
        {
            File.Exists(Path.Combine(apiRoot, allowed)).ShouldBeTrue($"Allowlisted file {allowed} no longer exists; remove it from the list.");
        }
    }
}

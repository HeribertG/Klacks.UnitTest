// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard: no Klacks.Api source may build a request URI that carries an API key as a query
/// parameter ("?key=", "&amp;api_key=", "?auth_key=" ...). A key in the URI ends up wherever the URI is
/// written: provider debug logs, exception texts, proxies. Live trigger 2026-10-02: the Gemini key in
/// "generateContent?key=" filled the demo backend's debug log. Keys go into headers instead
/// (Google: GoogleApiConstants.ApiKeyHeaderName, DeepL/OpenRouteService: Authorization).
///
/// The files are read with File.ReadAllText on purpose: several provider sources contain control
/// characters (the Gemini tool tokens), which makes ripgrep treat them as binary and skip them.
///
/// Scope note: Klacks.Plugin.Messaging is not scanned. WeChat's token endpoint prescribes "secret" and
/// "access_token" as query parameters and Telegram the bot token in the path; there the request URI is
/// never logged (RateLimitRetryHandler logs the host only) and the HttpClient logging redacts it.
/// "token=" is not matched either, because the password reset link legitimately carries its token.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ApiKeyInQueryStringGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string MarkerDirectory = "Infrastructure";
    private const string SourceFilePattern = "*.cs";
    private const int MinimumScannedFiles = 500;

    private static readonly string[] ExcludedDirectorySegments = ["Migrations", "obj", "bin"];

    private static readonly Regex KeyQueryParameter =
        new(@"[?&](key|api_key|apikey|api-key|auth_key)=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Test]
    public void ApiSources_MustNotPutAnApiKeyIntoTheQueryString()
    {
        var apiRoot = LocateApiProject();
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
            var lines = File.ReadAllText(file).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (KeyQueryParameter.IsMatch(lines[i]))
                {
                    violations.AppendLine($"  {relative.Replace(Path.DirectorySeparatorChar, '/')}:{i + 1} -> {lines[i].Trim()}");
                }
            }
        }

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scannedFiles} source files were scanned, so a green result would be meaningless.");

        violations.Length.ShouldBe(
            0,
            "API keys must be sent in a request header, never as a query parameter:" + Environment.NewLine + violations);
    }

    [Test]
    public void ThePattern_CatchesTheShapeThatLeakedTheGeminiKey()
    {
        KeyQueryParameter.IsMatch("private const string GenerateEndpoint = \":generateContent?key=\";").ShouldBeTrue();
        KeyQueryParameter.IsMatch("var url = $\"{ApiUrl}&api_key={key}\";").ShouldBeTrue();
        KeyQueryParameter.IsMatch("var resetLink = $\"{baseUrl}/reset-password?token={rawToken}\";").ShouldBeFalse();
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, MarkerDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}

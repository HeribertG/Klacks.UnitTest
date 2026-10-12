// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard: every issuer of a pending confirmation names the channel the token belongs to. The store
/// interface defaults the channel to Chat, so a new issuer that forgets the argument would hold an MCP caller's
/// confirmation as a chat token, redeemable by an unrelated "ja" in the Klacks chat. A source scan finds each
/// Create call on a confirmation store in Klacks.Api and requires the statement to carry a channel argument.
/// Not covered: Create calls on a receiver with another name, which the receiver pattern does not match.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ConfirmationChannelIssuerGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SourceFilePattern = "*.cs";
    private const string MigrationsDirectory = "Migrations";
    private const string StatementEnd = ";";
    private const string ChannelMarker = "channel";
    private const int ExpectedIssuerCountAtLeast = 5;

    private static readonly Regex CreateCall =
        new(@"(?:[cC]onfirmationStore|\bstore)\.Create\(", RegexOptions.Compiled);

    [Test]
    public void EveryConfirmationIssuer_PassesAChannel()
    {
        var root = Path.Combine(RepositoryRootLocator.RequireRoot(), ApiProjectDirectory);
        var issuers = 0;
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, SourceFilePattern, SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + MigrationsDirectory + Path.DirectorySeparatorChar))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in CreateCall.Matches(text))
            {
                var end = text.IndexOf(StatementEnd, match.Index, StringComparison.Ordinal);
                var statement = text[match.Index..end];
                issuers++;
                if (!statement.Contains(ChannelMarker, StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        issuers.ShouldBeGreaterThanOrEqualTo(
            ExpectedIssuerCountAtLeast,
            "The scan found fewer confirmation issuers than exist today, so it cannot have inspected the real sources.");
        missing.ShouldBeEmpty(
            "These files create a pending confirmation without naming its channel: " + string.Join(", ", missing));
    }
}

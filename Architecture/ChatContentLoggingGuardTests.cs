// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the owner decision of 2026-10-18: chat contents never reach a log at Trace, Debug,
/// Information or Warning level. That covers what a user typed, what the assistant or any LLM answered (content,
/// reasoning, raw provider JSON), prompts, tool-call arguments and learned phrases or goals taken from a chat.
/// Log lines carry metadata only: user id, conversation id, lengths, skill names, counts, hashes.
///
/// A source scan is used because the violation is an argument expression inside a method body. Every
/// .Log{Level}( call in Klacks.Api is extracted with balanced parentheses (calls span several lines), string
/// literals are blanked (interpolation holes are kept, they are code), the arguments are split at top level and
/// each argument is matched against a deny list of the content carriers this code base actually uses. An
/// argument that only measures, tests or hashes a carrier (LengthForLog(), .Length, .Count, .Keys, Contains(...),
/// Hash(...)) is allowed.
///
/// Scope note — what this guard does NOT cover:
/// - Error and Critical calls. The owner decision names Information, Debug and Warning; provider error bodies
///   logged at Error level (BaseHttpProvider, AnthropicProvider, GeminiProvider) are technical answers.
/// - Content carried under a name outside the deny list, e.g. a new local variable "text" holding a message.
///   The list is deliberately specific to keep false positives at zero; extend it when a new carrier appears.
/// - result.Message / ex.Message: technical error texts of skills and exceptions, allowed on purpose.
/// - Projects outside Klacks.Api, in particular Klacks.Plugin.Messaging.
/// See docs/knowledge/chat-content-not-logged-2026-10-18.md.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ChatContentLoggingGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const int MinimumExtractedLogCalls = 1000;
    private const string KnowledgeEntry = "docs/knowledge/chat-content-not-logged-2026-10-18.md";

    private static readonly string[] ExcludedDirectorySegments = ["bin", "obj", "Migrations", ".claude"];

    private static readonly Regex LogCallStart = new(
        @"\.Log(?<level>Trace|Debug|Information|Warning)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AllowedMeasure = new(
        @"LengthForLog\(\)|\.Length\b|\.Count\b|\.Keys\b|Hash\(|\.(Contains|StartsWith|EndsWith)\(|\bIsNullOr(Empty|WhiteSpace)\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Name, Regex Pattern)[] ContentCarriers =
    [
        ("chat message of a request/context/command",
            new Regex(@"\b(request|context|command|ctx|input|turn\.Context)\.(Message|UserMessage|Prompt|SystemPrompt|Content|Reply|Answer)\b(?!\w)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("text or content of a messenger/inbound message",
            new Regex(@"\b\w*[mM]essage\.(Text|Content|Body)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("serialized object (prompt, tool arguments or LLM response)",
            new Regex(@"JsonSerializer\.Serialize\(", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("tool-call arguments",
            new Regex(@"\.(Parameters|Arguments|ArgumentsJson)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("LLM content, prompt or raw provider payload",
            new Regex(@"\b(rawContent|rawReasoning|reasoning|responseJson|requestJson|responseLine|systemPrompt|volatilePrompt|userQuery|userMessage|phrase|goal|reply|rawReply|prompt)\b(?!\w)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant))
    ];

    [Test]
    public void ApiLogCalls_MustNotWriteChatContents()
    {
        var scan = ScanApiProject();

        scan.ExtractedCalls.ShouldBeGreaterThan(
            MinimumExtractedLogCalls,
            $"Only {scan.ExtractedCalls} log calls were extracted from Klacks.Api. The scan cannot have read the " +
            "real sources, so a green result would be meaningless.");

        var report = new StringBuilder();
        foreach (var violation in scan.Violations)
        {
            report.AppendLine($"  {violation.File}:{violation.Line} [{violation.Carrier}] -> {violation.Argument}");
        }

        scan.Violations.ShouldBeEmpty(
            "These log calls write chat contents (user prompt, assistant/LLM reply, reasoning, raw provider JSON, " +
            "tool-call arguments, learned phrases or goals) into the log. Owner decision 2026-10-18: log metadata " +
            "only. Replace the content by its size (value.LengthForLog() from Klacks.Api.Domain.Logging), an id " +
            "(user, conversation, turn, plan, phrase), skill names, parameter names (Parameters.Keys), counts or a " +
            $"hash (MessageNormalizer.Hash / ShortHash). See {KnowledgeEntry}.{Environment.NewLine}{report}");
    }

    [TestCase("_logger.LogInformation(\"Processing assistant request for user {UserId}: {Message}\", userId, request.Message.ForLog());")]
    [TestCase("_logger.LogInformation(\"Processing LLM request from user {UserId}: {Message}\",\n    context.UserId, context.Message);")]
    [TestCase("_logger.LogInformation(\"Executing function {FunctionName} with parameters {Parameters}\", call.FunctionName, JsonSerializer.Serialize(call.Parameters));")]
    [TestCase("_logger.LogDebug(\"raw reply: {Reply}\", Truncate(reply, RawReplyLogLength));")]
    [TestCase("_logger.LogDebug($\"Reply was {rawContent}\");")]
    [TestCase("_logger.LogWarning(\"Learned phrase '{Phrase}'\", phrase);")]
    public void Matcher_FlagsKnownContentLeaks(string snippet)
    {
        var violations = FindViolations(snippet, "snippet.cs").Violations;

        violations.ShouldNotBeEmpty($"The guard no longer recognizes a known content leak: {snippet}");
    }

    [TestCase("_logger.LogInformation(\"Processing request for user {UserId} ({Length} chars)\", userId, request.Message.LengthForLog());")]
    [TestCase("_logger.LogInformation(\"Feedback for turn {TurnId} hash {Hash}\", request.TurnId, MessageNormalizer.Hash(request.UserMessage));")]
    [TestCase("_logger.LogWarning(\"Skill {Skill} failed: {Message}\", skillName, result.Message);")]
    [TestCase("_logger.LogWarning(ex, \"Failed for goal and phrase and reply {Id}\", plan.Id);")]
    [TestCase("_logger.LogInformation(\"Executing {Function} with {Names}\", call.FunctionName, string.Join(\",\", call.Parameters.Keys));")]
    [TestCase("_logger.LogError(\"API error: {Error}\", responseJson);")]
    public void Matcher_AllowsMetadataOnly(string snippet)
    {
        var violations = FindViolations(snippet, "snippet.cs").Violations;

        violations.ShouldBeEmpty($"The guard flags a metadata-only log call: {snippet}");
    }

    private static ScanResult ScanApiProject()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var violations = new List<Violation>();
        var extracted = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => ExcludedDirectorySegments.Contains(segment, StringComparer.Ordinal)))
            {
                continue;
            }

            var result = FindViolations(File.ReadAllText(file), relative);
            extracted += result.ExtractedCalls;
            violations.AddRange(result.Violations);
        }

        return new ScanResult(violations, extracted);
    }

    private static ScanResult FindViolations(string source, string relativePath)
    {
        var code = BlankStringLiterals(source);
        var violations = new List<Violation>();
        var extracted = 0;

        foreach (Match match in LogCallStart.Matches(code))
        {
            var openIndex = match.Index + match.Length - 1;
            var closeIndex = FindClosingParenthesis(code, openIndex);
            if (closeIndex < 0)
            {
                continue;
            }

            extracted++;
            var line = CountLines(code, match.Index);
            foreach (var argument in SplitTopLevelArguments(code[(openIndex + 1)..closeIndex]))
            {
                if (AllowedMeasure.IsMatch(argument))
                {
                    continue;
                }

                foreach (var (name, pattern) in ContentCarriers)
                {
                    if (pattern.IsMatch(argument))
                    {
                        violations.Add(new Violation(relativePath, line, name, argument));
                        break;
                    }
                }
            }
        }

        return new ScanResult(violations, extracted);
    }

    private static string BlankStringLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    output.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                var end = i + 1;
                while (end < source.Length && source[end] != '\'' && source[end] != '\n')
                {
                    end += source[end] == '\\' ? 2 : 1;
                }

                AppendBlank(output, source, i, Math.Min(end, source.Length - 1));
                i = end + 1;
                continue;
            }

            var prefixLength = 0;
            var interpolated = false;
            var verbatim = false;
            while (i + prefixLength < source.Length && (source[i + prefixLength] == '$' || source[i + prefixLength] == '@'))
            {
                interpolated |= source[i + prefixLength] == '$';
                verbatim |= source[i + prefixLength] == '@';
                prefixLength++;
            }

            if (i + prefixLength < source.Length && source[i + prefixLength] == '"' && (prefixLength > 0 || c == '"'))
            {
                i = ConsumeString(source, i, prefixLength, interpolated, verbatim, output);
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    private static int ConsumeString(
        string source, int start, int prefixLength, bool interpolated, bool verbatim, StringBuilder output)
    {
        var i = start + prefixLength + 1;
        output.Append(' ', prefixLength).Append('"');
        while (i < source.Length)
        {
            var c = source[i];
            if (!verbatim && c == '\\')
            {
                output.Append("  ");
                i += 2;
                continue;
            }

            if (c == '"')
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                {
                    output.Append("  ");
                    i += 2;
                    continue;
                }

                output.Append('"');
                return i + 1;
            }

            if (interpolated && c == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{')
                {
                    output.Append("  ");
                    i += 2;
                    continue;
                }

                var holeEnd = FindHoleEnd(source, i);
                output.Append(' ').Append(BlankStringLiterals(source[(i + 1)..holeEnd])).Append(' ');
                i = holeEnd + 1;
                continue;
            }

            output.Append(c == '\n' ? '\n' : ' ');
            i++;
        }

        return i;
    }

    private static int FindHoleEnd(string source, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return source.Length - 1;
    }

    private static void AppendBlank(StringBuilder output, string source, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            output.Append(source[i] == '\n' ? '\n' : ' ');
        }
    }

    private static int FindClosingParenthesis(string code, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevelArguments(string arguments)
    {
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in arguments)
        {
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }

            if (c == ',' && depth == 0)
            {
                yield return Normalize(current);
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        yield return Normalize(current);
    }

    private static string Normalize(StringBuilder argument) =>
        Regex.Replace(argument.ToString(), @"\s+", " ").Trim();

    private static int CountLines(string code, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (code[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private sealed record Violation(string File, int Line, string Carrier, string Argument);

    private sealed record ScanResult(IReadOnlyList<Violation> Violations, int ExtractedCalls);
}

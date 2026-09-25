// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the lexer-based OUTPUT channel scan: literal channels are collected in statement and
/// call form, computed channels are counted as non-literal, comments and string contents are ignored,
/// a lexer error fails the scan, and a comment on the last line without a line break (which crashes the
/// tokenizer) fails it with a fixed, actionable message instead of the raw .NET exception text. Also pins the set of channels the backend processes and proves that
/// every standard macro shipped by MacrosSeed passes the scan with supported literal channels only.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroOutputChannelInspectorTests
{
    private const int SeededMacroScriptCount = 8;
    private const string SqlEscapedQuote = "''";
    private const string SqlQuote = "'";
    private const string TrailingCommentMessageText = "comment on the last line of the script";
    private const string TrailingCommentActionText = "remove that comment or move it to an earlier line";
    private const string DotNetIndexWording = "index";

    private static readonly Regex SeedScriptContent =
        new(@"SELECT\s+'[^']*','[^']*','(?<content>(?:[^']|'')*)'", RegexOptions.Compiled);

    private MacroOutputChannelInspector _inspector = null!;

    [SetUp]
    public void SetUp()
    {
        _inspector = new MacroOutputChannelInspector();
    }

    [Test]
    public void Inspect_LiteralChannels_AreCollectedInOrder()
    {
        var scan = _inspector.Inspect("import hour\nOUTPUT 1, Hour\noutput 10, 0\nOUTPUT 14, Hour * 2");

        scan.Succeeded.ShouldBeTrue();
        scan.LiteralChannels.ShouldBe(new[] { 1, 10, 14 });
        scan.NonLiteralChannelCount.ShouldBe(0);
    }

    [Test]
    public void Inspect_CallForm_IsRecognised()
    {
        var scan = _inspector.Inspect("DIM x\nx = OUTPUT(12, 5)");

        scan.LiteralChannels.ShouldBe(new[] { 12 });
    }

    [Test]
    public void Inspect_VariableChannel_IsNonLiteral()
    {
        var scan = _inspector.Inspect("DIM c\nc = 10\nOUTPUT c, 5");

        scan.LiteralChannels.ShouldBeEmpty();
        scan.NonLiteralChannelCount.ShouldBe(1);
    }

    [Test]
    public void Inspect_ArithmeticChannel_IsNonLiteral()
    {
        var scan = _inspector.Inspect("OUTPUT 1 + 9, 5");

        scan.NonLiteralChannelCount.ShouldBe(1);
    }

    [Test]
    public void Inspect_ParenthesisedChannelFollowedByOperator_IsNonLiteral()
    {
        var scan = _inspector.Inspect("OUTPUT (10) + 90, 5");

        scan.LiteralChannels.ShouldBeEmpty();
        scan.NonLiteralChannelCount.ShouldBe(1);
    }

    [Test]
    public void Inspect_ChannelBeyondIntRange_IsNonLiteral()
    {
        var scan = _inspector.Inspect("OUTPUT 99999999999, 1");

        scan.LiteralChannels.ShouldBeEmpty();
        scan.NonLiteralChannelCount.ShouldBe(1);
    }

    [Test]
    public void Inspect_CommentAndStringContents_AreIgnored()
    {
        var scan = _inspector.Inspect("' OUTPUT 99, 1\nDIM s\ns = \"OUTPUT 98, 1\"\nOUTPUT 1, 0");

        scan.LiteralChannels.ShouldBe(new[] { 1 });
        scan.NonLiteralChannelCount.ShouldBe(0);
    }

    [Test]
    public void Inspect_CrLfLineEndingsAndTabs_AreHandled()
    {
        var scan = _inspector.Inspect("IF 1 > 0 THEN\r\n\tOUTPUT 10, 1\r\nENDIF\r\nOUTPUT 1, 0\r\n");

        scan.Succeeded.ShouldBeTrue();
        scan.LiteralChannels.ShouldBe(new[] { 10, 1 });
    }

    [Test]
    public void Inspect_ScriptWithoutOutput_SucceedsEmpty()
    {
        var scan = _inspector.Inspect("DIM a\na = 1");

        scan.Succeeded.ShouldBeTrue();
        scan.LiteralChannels.ShouldBeEmpty();
    }

    [Test]
    public void Inspect_UnknownSymbol_FailsTheScan()
    {
        var scan = _inspector.Inspect("OUTPUT 1, 0\n#");

        scan.Succeeded.ShouldBeFalse();
        scan.FailureMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [TestCase("OUTPUT 1, 0 ' note")]
    [TestCase("OUTPUT 1, 0\n' note")]
    [TestCase("' only a note")]
    public void Inspect_CommentOnLastLineWithoutNewline_FailsTheScanLikeTheCompiler_WithAFixedActionableMessage(string script)
    {
        var scan = _inspector.Inspect(script);

        scan.Succeeded.ShouldBeFalse();
        scan.FailureMessage.ShouldNotBeNull();
        scan.FailureMessage.ShouldContain(TrailingCommentMessageText);
        scan.FailureMessage.ShouldContain(TrailingCommentActionText);
        scan.FailureMessage.ShouldNotContain(nameof(ArgumentOutOfRangeException));
        scan.FailureMessage.ShouldNotContain(DotNetIndexWording, Case.Insensitive);
        Should.Throw<ArgumentOutOfRangeException>(() => CompiledScript.Compile(script));
    }

    [Test]
    public void Inspect_CommentOnLastLineFollowedByNewline_Succeeds()
    {
        var scan = _inspector.Inspect("OUTPUT 1, 0 ' note\n");

        scan.Succeeded.ShouldBeTrue(scan.FailureMessage);
        scan.LiteralChannels.ShouldBe(new[] { 1 });
    }

    [Test]
    public void SupportedChannels_AreResultAndTheFiveSurchargeChannels()
    {
        MacroOutputChannels.Supported.OrderBy(c => c).ShouldBe(new[] { 1, 10, 11, 12, 13, 14 });
    }

    [Test]
    public void Inspect_EverySeededStandardMacro_UsesOnlySupportedLiteralChannels()
    {
        var scripts = SeededMacroScripts();

        scripts.Count.ShouldBe(SeededMacroScriptCount);
        foreach (var script in scripts)
        {
            var scan = _inspector.Inspect(script);

            scan.Succeeded.ShouldBeTrue(scan.FailureMessage);
            scan.NonLiteralChannelCount.ShouldBe(0);
            scan.LiteralChannels.ShouldNotBeEmpty();
            scan.LiteralChannels.ShouldAllBe(channel => MacroOutputChannels.Supported.Contains(channel));
        }
    }

    private static List<string> SeededMacroScripts()
    {
        var builder = new MigrationBuilder(null);
        MacrosSeed.SeedData(builder);
        return builder.Operations
            .OfType<SqlOperation>()
            .Select(operation => SeedScriptContent.Match(operation.Sql))
            .Where(match => match.Success)
            .Select(match => match.Groups["content"].Value.Replace(SqlEscapedQuote, SqlQuote))
            .ToList();
    }
}

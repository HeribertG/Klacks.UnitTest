// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of LexicalAnalyser.GetNextSymbol over whole token streams read from a StringInputStream: every
/// operator and punctuation symbol, compound assignments, comparison look-ahead, numbers, identifiers and keywords,
/// strings with doubled quotes, statement delimiters, comments, positions, and the error cases (unknown symbol,
/// unterminated string at end of line or end of source). Errors are recorded on the InterpreterError and never thrown;
/// a symbol whose scan failed keeps its default token tokPlus with an empty text. A comment that is not followed by a
/// line break makes the input stream read before its start and throws (pinned as current behaviour).
/// </summary>

using Klacks.Api.Infrastructure.Scripting;
using ScriptSymbol = Klacks.Api.Infrastructure.Scripting.Symbol;
using Tokens = Klacks.Api.Infrastructure.Scripting.Symbol.Tokens;

namespace Klacks.UnitTest.Infrastructure.Scripting;

[TestFixture]
public class LexicalAnalyserCharacterizationTests
{
    private const int MaxSymbols = 100;

    private InterpreterError _error = null!;

    [SetUp]
    public void SetUp()
    {
        _error = new InterpreterError();
    }

    [Test]
    public void SingleCharacterOperatorsAndPunctuation_AreRecognised_WithTheirPositions()
    {
        var symbols = Tokenize("+ - * / & \\ % ^ ! ~ ( ) , =");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokPlus, "+"),
            (Tokens.tokMinus, "-"),
            (Tokens.tokMultiplication, "*"),
            (Tokens.tokDivision, "/"),
            (Tokens.tokStringConcat, "&"),
            (Tokens.tokDiv, "\\"),
            (Tokens.tokMod, "%"),
            (Tokens.tokPower, "^"),
            (Tokens.tokFactorial, "!"),
            (Tokens.tokNot, "~"),
            (Tokens.tokLeftParent, "("),
            (Tokens.tokRightParent, ")"),
            (Tokens.tokComma, ","),
            (Tokens.tokEq, "="),
            (Tokens.tokEof, ""),
        });
        symbols.Select(s => (s.Line, s.Col, s.Index)).ShouldBe(new[]
        {
            (1, 1, 1), (1, 3, 3), (1, 5, 5), (1, 7, 7), (1, 9, 9), (1, 11, 11), (1, 13, 13), (1, 15, 15),
            (1, 17, 17), (1, 19, 19), (1, 21, 21), (1, 23, 23), (1, 25, 25), (1, 27, 27), (1, 27, 28),
        });
        _error.Number.ShouldBe(0);
    }

    [Test]
    public void CompoundAssignments_AreRecognised()
    {
        var symbols = Tokenize("+= -= *= /= &= \\= %=");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokPlusEq, "+="),
            (Tokens.tokMinusEq, "-="),
            (Tokens.tokMultiplicationEq, "*="),
            (Tokens.tokDivisionEq, "/="),
            (Tokens.tokStringConcatEq, "&="),
            (Tokens.tokDivEq, "\\="),
            (Tokens.tokModEq, "%="),
            (Tokens.tokEof, ""),
        });
        symbols.Select(s => s.Col).ShouldBe(new[] { 1, 4, 7, 10, 13, 16, 19, 20 });
    }

    [Test]
    public void Comparisons_LookAheadOneCharacter_AndGiveItBack()
    {
        var symbols = Tokenize("< > <> <= >= <x");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokLt, "<"),
            (Tokens.tokGt, ">"),
            (Tokens.tokNotEq, "<>"),
            (Tokens.tokLEq, "<="),
            (Tokens.tokGEq, ">="),
            (Tokens.tokLt, "<"),
            (Tokens.tokIdentifier, "x"),
            (Tokens.tokEof, ""),
        });
        symbols[6].Col.ShouldBe(15);
    }

    [Test]
    public void Numbers_CarryTheirTextAndAnInvariantDoubleValue()
    {
        var symbols = Tokenize("12 3.25 7. 42");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokNumber, "12"),
            (Tokens.tokNumber, "3.25"),
            (Tokens.tokNumber, "7."),
            (Tokens.tokNumber, "42"),
            (Tokens.tokEof, ""),
        });
        symbols.Take(4).Select(s => (double)s.Value!).ShouldBe(new[] { 12d, 3.25d, 7d, 42d });
        symbols.Select(s => s.Col).ShouldBe(new[] { 1, 4, 9, 12, 13 });
    }

    [Test]
    public void Identifiers_AndKeywords_AreCaseInsensitive_AndKeepTheirSourceText()
    {
        var symbols = Tokenize("Dim x_1 = @Hours If iF then MOD div AndAlso");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokDim, "Dim"),
            (Tokens.tokIdentifier, "x_1"),
            (Tokens.tokEq, "="),
            (Tokens.tokIdentifier, "@Hours"),
            (Tokens.tokIf, "If"),
            (Tokens.tokIf, "iF"),
            (Tokens.tokThen, "then"),
            (Tokens.tokMod, "MOD"),
            (Tokens.tokDiv, "div"),
            (Tokens.tokAndAlso, "AndAlso"),
            (Tokens.tokEof, ""),
        });
        symbols.All(s => s.Value == null).ShouldBeTrue();
        symbols.Select(s => s.Col).ShouldBe(new[] { 1, 5, 9, 11, 18, 21, 24, 29, 33, 37, 43 });
    }

    [Test]
    public void Strings_UnfoldDoubledQuotes_AndCarryTheTextAsValue()
    {
        var symbols = Tokenize("\"ab\"\"c\" \"x\"");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokString, "ab\"c"),
            (Tokens.tokString, "x"),
            (Tokens.tokEof, ""),
        });
        symbols[0].Value.ShouldBe("ab\"c");
        symbols[1].Value.ShouldBe("x");
        symbols[1].Col.ShouldBe(9);
        _error.Number.ShouldBe(0);
    }

    [Test]
    public void ColonsAndLineBreaks_AreStatementDelimiters_AndAdvanceTheLine()
    {
        var symbols = Tokenize("a : b\r\nc\nd");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokIdentifier, "a"),
            (Tokens.tokStatementDelimiter, ":"),
            (Tokens.tokIdentifier, "b"),
            (Tokens.tokStatementDelimiter, "\n"),
            (Tokens.tokIdentifier, "c"),
            (Tokens.tokStatementDelimiter, "\n"),
            (Tokens.tokIdentifier, "d"),
            (Tokens.tokEof, ""),
        });
        symbols.Select(s => (s.Line, s.Col, s.Index)).ShouldBe(new[]
        {
            (1, 1, 1), (1, 3, 3), (1, 5, 5), (2, 0, 7), (2, 1, 8), (3, 0, 9), (3, 1, 10), (3, 1, 11),
        });
    }

    [Test]
    public void TabsAreBlanks_AndACommentIsSkippedUpToTheLineBreak()
    {
        Describe(Tokenize("\tq")).ShouldBe(new[]
        {
            (Tokens.tokIdentifier, "q"),
            (Tokens.tokEof, ""),
        });

        var symbols = Tokenize("x ' comment\ny");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokIdentifier, "x"),
            (Tokens.tokStatementDelimiter, "\n"),
            (Tokens.tokIdentifier, "y"),
            (Tokens.tokEof, ""),
        });
        symbols[1].Line.ShouldBe(2);
        symbols[1].Col.ShouldBe(0);
        symbols[1].Index.ShouldBe(12);
    }

    [Test]
    public void ACommentWithoutAFollowingLineBreak_Throws()
    {
        var analyser = Connect("x ' trailing");

        analyser.GetNextSymbol().Token.ShouldBe(Tokens.tokIdentifier);
        Should.Throw<ArgumentOutOfRangeException>(() => analyser.GetNextSymbol());
    }

    [Test]
    public void UnknownCharacter_IsRecordedAsError_AndTheSymbolKeepsItsDefaults()
    {
        var symbols = Tokenize("a # b");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokIdentifier, "a"),
            (Tokens.tokPlus, ""),
            (Tokens.tokIdentifier, "b"),
            (Tokens.tokEof, ""),
        });
        symbols[1].Value.ShouldBeNull();
        _error.Number.ShouldBe((int)InterpreterError.LexErrors.errUnknownSymbol);
        _error.Source.ShouldBe("LexicalAnalyser.GetNextSymbol");
        _error.Description.ShouldBe("Unknown symbol starting with character ASCII #");
        (_error.Line, _error.Col, _error.Index).ShouldBe((1, 3, 3));
    }

    [Test]
    public void StringOpenAtTheLineBreak_IsRecordedAsError_AndLexingGoesOnInTheNextLine()
    {
        var symbols = Tokenize("\"open\nx");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokPlus, ""),
            (Tokens.tokIdentifier, "x"),
            (Tokens.tokEof, ""),
        });
        symbols[1].Line.ShouldBe(2);
        _error.Number.ShouldBe((int)InterpreterError.LexErrors.errUnexpectedEOL);
        _error.Source.ShouldBe("LexAnalyser.nextSymbol");
        _error.Description.ShouldBe("String not closed; unexpected end of line encountered");
        (_error.Line, _error.Col, _error.Index).ShouldBe((2, 0, 6));
    }

    [Test]
    public void StringOpenAtTheEndOfTheSource_IsRecordedAsError()
    {
        var symbols = Tokenize("\"open");

        Describe(symbols).ShouldBe(new[]
        {
            (Tokens.tokPlus, ""),
            (Tokens.tokEof, ""),
        });
        _error.Number.ShouldBe((int)InterpreterError.LexErrors.errUnexpectedEOF);
        _error.Description.ShouldBe("String not closed; unexpected end of source");
        (_error.Line, _error.Col, _error.Index).ShouldBe((1, 5, 6));
    }

    [Test]
    public void EmptySource_GivesEofRightAway()
    {
        var symbols = Tokenize("");

        Describe(symbols).ShouldBe(new[] { (Tokens.tokEof, "") });
        _error.Number.ShouldBe(0);
    }

    private LexicalAnalyser Connect(string source) =>
        new LexicalAnalyser().Connect(new StringInputStream().Connect(source), _error);

    private List<ScriptSymbol> Tokenize(string source)
    {
        var analyser = Connect(source);
        var symbols = new List<ScriptSymbol>();
        for (var i = 0; i < MaxSymbols; i++)
        {
            var symbol = analyser.GetNextSymbol();
            symbols.Add(symbol);
            if (symbol.Token == Tokens.tokEof)
            {
                break;
            }
        }

        return symbols;
    }

    private static (Tokens, string)[] Describe(IEnumerable<ScriptSymbol> symbols) =>
        symbols.Select(s => (s.Token, s.Text)).ToArray();
}

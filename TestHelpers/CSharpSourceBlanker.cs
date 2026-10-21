// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Removes comments and the content of string and char literals from C# source while keeping every line
/// break, so source-scanning guards (LayerDependencyGuardTests, OneTypePerFileGuardTests) see code only and
/// still report correct line numbers. Handles //, /* */, regular, verbatim (@"", $@"") and raw ("""...""")
/// strings. Interpolation holes are not tracked: a nested string literal inside a hole of a multi-line
/// interpolated string can shift the reported line numbers of that file, and code inside a hole is not seen
/// at all - both err towards a false negative, never a false positive.
/// </summary>
public static class CSharpSourceBlanker
{
    /// <param name="text">Complete C# source text</param>
    /// <returns>The text without comments and literal contents; line breaks are preserved</returns>
    public static string BlankCommentsAndStrings(string text)
    {
        var result = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                index = SkipUntilLineEnd(text, index);
            }
            else if (current == '/' && next == '*')
            {
                index = SkipBlock(text, index + 2, "*/", result);
            }
            else if (current == '"')
            {
                index = SkipString(text, index, result);
            }
            else if (current == '\'')
            {
                index = SkipCharLiteral(text, index, result);
            }
            else
            {
                result.Append(current);
                index++;
            }
        }

        return result.ToString();
    }

    private static int SkipUntilLineEnd(string text, int index)
    {
        var lineEnd = text.IndexOf('\n', index);
        return lineEnd < 0 ? text.Length : lineEnd;
    }

    private static int SkipBlock(string text, int contentStart, string terminator, StringBuilder result)
    {
        var end = text.IndexOf(terminator, contentStart, StringComparison.Ordinal);
        var stop = end < 0 ? text.Length : end + terminator.Length;
        KeepLineBreaks(text, contentStart, stop, result);
        return stop;
    }

    private static int SkipString(string text, int index, StringBuilder result)
    {
        if (IsVerbatimPrefix(text, index))
        {
            var position = index + 1;
            while (position < text.Length)
            {
                if (text[position] == '"')
                {
                    if (position + 1 < text.Length && text[position + 1] == '"')
                    {
                        position += 2;
                        continue;
                    }

                    break;
                }

                position++;
            }

            var stop = Math.Min(position + 1, text.Length);
            KeepLineBreaks(text, index, stop, result);
            return stop;
        }

        var quotes = 0;
        while (index + quotes < text.Length && text[index + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            return SkipBlock(text, index + quotes, new string('"', quotes), result);
        }

        if (quotes == 2)
        {
            return index + 2;
        }

        var cursor = index + 1;
        while (cursor < text.Length && text[cursor] != '"' && text[cursor] != '\n')
        {
            cursor += text[cursor] == '\\' ? 2 : 1;
        }

        return Math.Min(cursor + 1, text.Length);
    }

    private static bool IsVerbatimPrefix(string text, int quoteIndex)
    {
        var position = quoteIndex - 1;
        while (position >= 0 && (text[position] == '$' || text[position] == '@'))
        {
            if (text[position] == '@')
            {
                return true;
            }

            position--;
        }

        return false;
    }

    private static int SkipCharLiteral(string text, int index, StringBuilder result)
    {
        var cursor = index + 1;
        if (cursor < text.Length && text[cursor] == '\\')
        {
            cursor += 2;
            while (cursor < text.Length && text[cursor] != '\'' && text[cursor] != '\n')
            {
                cursor++;
            }
        }
        else
        {
            cursor++;
        }

        if (cursor < text.Length && text[cursor] == '\'')
        {
            return cursor + 1;
        }

        result.Append(text[index]);
        return index + 1;
    }

    private static void KeepLineBreaks(string text, int start, int stop, StringBuilder result)
    {
        for (var position = start; position < stop; position++)
        {
            if (text[position] == '\n')
            {
                result.Append('\n');
            }
        }
    }
}

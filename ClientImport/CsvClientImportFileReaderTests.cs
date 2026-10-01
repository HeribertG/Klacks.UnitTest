// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// CSV reader of the employee import: encodings (UTF-8 with and without BOM, UTF-16 LE/BE BOM,
/// Windows-1252 fallback), delimiter sniffing, RFC-4180 quotes, all three line-break styles and
/// dropped blank lines.
/// </summary>

using System.Text;
using Klacks.Api.Infrastructure.Services.ClientImport;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class CsvClientImportFileReaderTests
{
    private readonly CsvClientImportFileReader _reader = new();

    [Test]
    public void Utf8WithBom_IsDecodedAndReported()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Vorname;Nachname\nJürg;Müller")).ToArray();

        var sheet = _reader.Read(bytes, null);

        sheet.Encoding.ShouldBe(CsvClientImportFileReader.Utf8Name);
        sheet.Rows[1].ShouldBe(["Jürg", "Müller"]);
    }

    [Test]
    public void Utf8WithoutBom_IsDecoded()
    {
        var sheet = _reader.Read(Encoding.UTF8.GetBytes("Vorname;Nachname\nZoë;Ørsted"), null);

        sheet.Encoding.ShouldBe(CsvClientImportFileReader.Utf8Name);
        sheet.Rows[1].ShouldBe(["Zoë", "Ørsted"]);
    }

    [Test]
    public void Utf16LittleEndianWithBom_IsDecoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("名;姓\n太郎;山田")).ToArray();

        var sheet = _reader.Read(bytes, null);

        sheet.Encoding.ShouldBe(CsvClientImportFileReader.Utf16Name);
        sheet.Rows[1].ShouldBe(["太郎", "山田"]);
    }

    [Test]
    public void Utf16BigEndianWithBom_IsDecoded()
    {
        var bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("a;b\nc;d")).ToArray();

        var sheet = _reader.Read(bytes, null);

        sheet.Encoding.ShouldBe(CsvClientImportFileReader.Utf16BigEndianName);
        sheet.Rows[1].ShouldBe(["c", "d"]);
    }

    [Test]
    public void InvalidUtf8_FallsBackToWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Vorname;Nachname;Ort\nJürg;Müller;Zürich");

        var sheet = _reader.Read(bytes, null);

        sheet.Encoding.ShouldBe(CsvClientImportFileReader.Windows1252Name);
        sheet.Rows[1].ShouldBe(["Jürg", "Müller", "Zürich"]);
    }

    [TestCase("a;b;c\n1;2;3", ";")]
    [TestCase("a,b,c\n1,2,3", ",")]
    [TestCase("a\tb\tc\n1\t2\t3", "\t")]
    [TestCase("a|b|c\n1|2|3", "|")]
    public void Delimiter_IsSniffed(string text, string expected)
    {
        var sheet = _reader.Read(Encoding.UTF8.GetBytes(text), null);

        sheet.Delimiter.ShouldBe(expected);
        sheet.Rows[1].ShouldBe(["1", "2", "3"]);
    }

    [Test]
    public void SemicolonWins_OverCommasInsideValues()
    {
        var sheet = _reader.Read(Encoding.UTF8.GetBytes("Name;Ort\nMüller, Hans;Bern\nMeier, Eva;Thun"), null);

        sheet.Delimiter.ShouldBe(";");
        sheet.Rows[1].ShouldBe(["Müller, Hans", "Bern"]);
    }

    [Test]
    public void QuotedFields_KeepDelimitersQuotesAndLineBreaks()
    {
        var sheet = _reader.Read(Encoding.UTF8.GetBytes("a,b,c\n\"x, y\",\"say \"\"hi\"\"\",\"line1\nline2\""), null);

        sheet.Rows[1].ShouldBe(["x, y", "say \"hi\"", "line1\nline2"]);
    }

    [TestCase("a;b\r\n1;2\r\n3;4")]
    [TestCase("a;b\n1;2\n3;4")]
    [TestCase("a;b\r1;2\r3;4")]
    public void AllLineBreakStyles_EndARecord(string text)
    {
        var sheet = _reader.Read(Encoding.UTF8.GetBytes(text), null);

        sheet.Rows.Count.ShouldBe(3);
        sheet.Rows[2].ShouldBe(["3", "4"]);
    }

    [Test]
    public void BlankLines_AreDropped()
    {
        var sheet = _reader.Read(Encoding.UTF8.GetBytes("a;b\n\n;\n1;2\n"), null);

        sheet.Rows.Count.ShouldBe(2);
    }

    [TestCase("list.csv", true)]
    [TestCase("LIST.TXT", true)]
    [TestCase("list.xlsx", false)]
    public void CanRead_ByExtension(string fileName, bool expected)
    {
        _reader.CanRead(fileName).ShouldBe(expected);
    }
}

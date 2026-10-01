// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parse step of the employee import end to end with the real readers and detector: a realistic
/// Windows-1252 semicolon CSV with a title line, the header row found below it, mapping, samples, date
/// order, name order and every file error code.
/// </summary>

using System.Text;
using Klacks.Api.Application.Commands.ClientImport;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.ClientImport;
using Klacks.Api.Application.Interfaces.ClientImport;
using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.ClientImport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ParseClientImportCommandHandlerTests
{
    private ParseClientImportCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(ClientImportTestData.Today);

        IClientImportFileReader[] readers = [new XlsxClientImportFileReader(), new CsvClientImportFileReader()];
        _handler = new ParseClientImportCommandHandler(
            readers, new ClientImportColumnDetector(ClientImportSynonymCatalog.LoadEmbedded()), clock,
            NullLogger<ParseClientImportCommandHandler>.Instance);
    }

    [Test]
    public async Task RealisticCsv_IsParsedWithMappingAndDateOrder()
    {
        const string csv = "Mitarbeiterliste 2026\r\n" +
                           "Anrede;Name;Vorname;Strasse;PLZ Ort;Geburtsdatum;Eintritt;E-Mail\r\n" +
                           "Frau;Müller;Anna;Bahnhofstr. 1;8000 Zürich;25.03.1985;01.04.2020;anna@example.com\r\n" +
                           "Herr;Gerber;Jürg;Seeweg 3;3000 Bern;02.11.1979;15.01.2018;juerg@example.com\r\n";
        var bytes = Encoding.GetEncoding(1252).GetBytes(csv);

        var result = await _handler.Handle(new ParseClientImportCommand(bytes, @"C:\temp\staff.csv", null), CancellationToken.None);

        result.Token.ShouldNotBe(Guid.Empty);
        result.FileName.ShouldBe("staff.csv");
        result.HeaderRowIndex.ShouldBe(1);
        result.Encoding.ShouldBe(CsvClientImportFileReader.Windows1252Name);
        result.Delimiter.ShouldBe(";");
        result.Rows.Count.ShouldBe(2);
        result.Rows[1][2].ShouldBe("Jürg");
        result.Columns[1].Samples.ShouldBe(["Müller", "Gerber"]);
        result.Mapping.Select(m => m.Target).ShouldBe([
            ClientImportTarget.Salutation, ClientImportTarget.LastName, ClientImportTarget.FirstName, ClientImportTarget.Street,
            ClientImportTarget.ZipCity, ClientImportTarget.Birthdate, ClientImportTarget.EntryDate, ClientImportTarget.Email]);
        result.DateFormat.ShouldBe(ClientImportDateFormat.DayMonthYear);
        result.DateFormatAmbiguous.ShouldBeFalse();
        result.NameOrder.ShouldBe(ClientImportNameOrder.FirstLast);
    }

    [Test]
    public async Task CommaSeparatedFullNames_SuggestLastFirst()
    {
        var bytes = Encoding.UTF8.GetBytes("Name;Ort\n\"Müller, Anna\";Bern\n\"Meier, Ben\";Thun");

        var result = await _handler.Handle(new ParseClientImportCommand(bytes, "a.csv", null), CancellationToken.None);

        result.Mapping[0].Target.ShouldBe(ClientImportTarget.FullName);
        result.NameOrder.ShouldBe(ClientImportNameOrder.LastFirst);
    }

    [Test]
    public async Task CombinedNameHeader_WithUnquotedCommaValues_IsAFullNameInLastFirstOrder()
    {
        const string csv = "Anrede;Name, Vorname;Geburtsdatum;E-Mail\r\n" +
                           "Frau;ImpTestMüller, Anna;25.03.1985;anna@example.com\r\n" +
                           "Herr;ImpTestGerber, Jürg;02.11.1979;juerg@example.com\r\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var result = await _handler.Handle(new ParseClientImportCommand(bytes, "staff.csv", null), CancellationToken.None);

        result.Delimiter.ShouldBe(";");
        result.Rows[0][1].ShouldBe("ImpTestMüller, Anna");
        result.Mapping.Select(m => m.Target).ShouldBe([
            ClientImportTarget.Salutation, ClientImportTarget.FullName, ClientImportTarget.Birthdate, ClientImportTarget.Email]);
        result.NameOrder.ShouldBe(ClientImportNameOrder.LastFirst);

        var names = ClientImportNameSplitter.Split(result.Rows[0][1], result.NameOrder);
        names.FirstName.ShouldBe("Anna");
        names.LastName.ShouldBe("ImpTestMüller");
    }

    [Test]
    public async Task CommaInMostButNotAllFullNames_StillSuggestsLastFirst()
    {
        var bytes = Encoding.UTF8.GetBytes("Name;Ort\n\"Müller, Anna\";Bern\n\"Meier, Ben\";Thun\n\"Gerber, Jürg\";Biel\n\"Keller, Eva\";Chur\nUeli Frei;Aarau");

        var result = await _handler.Handle(new ParseClientImportCommand(bytes, "a.csv", null), CancellationToken.None);

        result.NameOrder.ShouldBe(ClientImportNameOrder.LastFirst);
    }

    [Test]
    public async Task UnsupportedExtension_IsRejected()
    {
        (await Reject([1, 2, 3], "a.pdf")).ShouldBe(ClientImportErrorCodes.FileUnsupported);
    }

    [Test]
    public async Task EmptyFile_IsRejected()
    {
        (await Reject([], "a.csv")).ShouldBe(ClientImportErrorCodes.FileEmpty);
        (await Reject(Encoding.UTF8.GetBytes("Vorname;Nachname\n"), "a.csv")).ShouldBe(ClientImportErrorCodes.FileEmpty);
    }

    [Test]
    public async Task TooLargeFile_IsRejected()
    {
        (await Reject(new byte[ClientImportLimits.MaxFileBytes + 1], "a.csv")).ShouldBe(ClientImportErrorCodes.FileTooLarge);
    }

    [Test]
    public async Task NoHeaderRow_IsRejected()
    {
        (await Reject(Encoding.UTF8.GetBytes("1;2\n3;4"), "a.csv")).ShouldBe(ClientImportErrorCodes.NoHeaderRow);
    }

    [Test]
    public async Task TooManyRows_IsRejected()
    {
        var csv = new StringBuilder("Vorname;Nachname\n");
        for (var index = 0; index <= ClientImportLimits.MaxRows; index++)
        {
            csv.Append("A;B\n");
        }

        (await Reject(Encoding.UTF8.GetBytes(csv.ToString()), "a.csv")).ShouldBe(ClientImportErrorCodes.TooManyRows);
    }

    [Test]
    public async Task TooManyColumns_IsRejected()
    {
        var header = string.Join(";", Enumerable.Range(0, ClientImportLimits.MaxColumns + 1).Select(i => $"Spalte{i}"));

        (await Reject(Encoding.UTF8.GetBytes(header + "\nx"), "a.csv")).ShouldBe(ClientImportErrorCodes.TooManyColumns);
    }

    [Test]
    public async Task GridAboveTheCharacterBudget_IsRejected()
    {
        const int columns = 60;
        var cell = new string('x', ClientImportLimits.MaxCellLength);
        var rowCount = (int)(ClientImportLimits.MaxGridChars / (ClientImportLimits.MaxCellLength * columns)) + 1;
        var rows = new List<List<string>> { Enumerable.Range(0, columns).Select(i => $"Spalte{i}").ToList() };
        rows.AddRange(Enumerable.Range(0, rowCount).Select(_ => Enumerable.Repeat(cell, columns).ToList()));

        var reader = Substitute.For<IClientImportFileReader>();
        reader.CanRead(Arg.Any<string>()).Returns(true);
        reader.Read(Arg.Any<byte[]>(), Arg.Any<string?>()).Returns(new ClientImportSheet { Rows = rows });
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(ClientImportTestData.Today);
        var handler = new ParseClientImportCommandHandler(
            [reader], new ClientImportColumnDetector(ClientImportSynonymCatalog.LoadEmbedded()), clock,
            NullLogger<ParseClientImportCommandHandler>.Instance);

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(
            () => handler.Handle(new ParseClientImportCommand([1], "a.xlsx", null), CancellationToken.None));

        exception.Code.ShouldBe(ClientImportErrorCodes.FileTooLarge);
    }

    private async Task<string> Reject(byte[] content, string fileName)
    {
        var exception = await Should.ThrowAsync<ClientImportRejectedException>(
            () => _handler.Handle(new ParseClientImportCommand(content, fileName, null), CancellationToken.None));
        return exception.Code;
    }
}

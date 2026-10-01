// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Xlsx reader of the employee import: real date cells become ISO dates, whole numbers keep all digits,
/// formatted postcodes keep leading zeros, formulas are read from their stored result and never
/// evaluated, the sheet can be chosen, hidden sheets are not offered, blank
/// rows are dropped, broken files and zip bombs are rejected with their codes.
/// </summary>

using System.IO.Compression;
using ClosedXML.Excel;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.ClientImport;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class XlsxClientImportFileReaderTests
{
    private readonly XlsxClientImportFileReader _reader = new();

    [Test]
    public void Cells_AreReadAsDisplayedStrings()
    {
        var content = Workbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "Vorname";
            sheet.Cell(1, 2).Value = "Geburtsdatum";
            sheet.Cell(1, 3).Value = "Telefon";
            sheet.Cell(1, 4).Value = "PLZ";
            sheet.Cell(2, 1).Value = "Anna";
            sheet.Cell(2, 2).Value = new DateTime(1990, 3, 5, 0, 0, 0, DateTimeKind.Utc);
            sheet.Cell(2, 3).Value = 41791234567d;
            sheet.Cell(2, 4).Value = 1234d;
            sheet.Cell(2, 4).Style.NumberFormat.Format = "00000";
        });

        var sheet = _reader.Read(content, null);

        sheet.Rows[1].ShouldBe(["Anna", "1990-03-05", "41791234567", "01234"]);
    }

    [Test]
    public void FormulaCell_IsReadFromItsStoredResult()
    {
        var content = Workbook(sheet => sheet.Cell(1, 1).FormulaA1 = "=\"Muster\"&\"frau\"", evaluateFormulas: true);

        _reader.Read(content, null).Rows[0][0].ShouldBe("Musterfrau");
    }

    [Test]
    public void FormulaCell_WithoutStoredResult_IsNotEvaluatedAndReadsEmpty()
    {
        var content = Workbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "Vorname";
            sheet.Cell(1, 2).FormulaA1 = "=\"Muster\"&\"frau\"";
        });

        _reader.Read(content, null).Rows[0].ShouldBe(["Vorname", ""]);
    }

    [Test]
    public void SheetName_SelectsTheSheet_AndHiddenSheetsAreNotOffered()
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("First").Cell(1, 1).Value = "a";
        workbook.Worksheets.Add("Second").Cell(1, 1).Value = "b";
        workbook.Worksheets.Add("Hidden").Visibility = XLWorksheetVisibility.Hidden;

        var sheet = _reader.Read(Save(workbook), "Second");

        sheet.SheetNames.ShouldBe(["First", "Second"]);
        sheet.SheetName.ShouldBe("Second");
        sheet.Rows[0][0].ShouldBe("b");
    }

    [Test]
    public void BlankRows_AreDropped()
    {
        var content = Workbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "a";
            sheet.Cell(5, 1).Value = "b";
        });

        _reader.Read(content, null).Rows.Count.ShouldBe(2);
    }

    [Test]
    public void BrokenFile_IsRejectedAsUnsupported()
    {
        var exception = Should.Throw<ClientImportRejectedException>(() => _reader.Read([1, 2, 3, 4], null));

        exception.Code.ShouldBe(ClientImportErrorCodes.FileUnsupported);
    }

    [Test]
    public void ZipGuard_RejectsContentAboveTheUnpackedLimit()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("xl/big.xml", CompressionLevel.Optimal).Open();
            entry.Write(new byte[2 * 1024 * 1024]);
        }

        var exception = Should.Throw<ClientImportRejectedException>(
            () => ClientImportZipGuard.EnsureWithinLimits(buffer.ToArray(), 1024 * 1024, ClientImportLimits.MaxZipEntries));

        exception.Code.ShouldBe(ClientImportErrorCodes.UnpackedSizeExceeded);
    }

    [Test]
    public void ZipGuard_RejectsTooManyEntries()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < 5; index++)
            {
                archive.CreateEntry($"e{index}.xml");
            }
        }

        var exception = Should.Throw<ClientImportRejectedException>(
            () => ClientImportZipGuard.EnsureWithinLimits(buffer.ToArray(), ClientImportLimits.MaxUnpackedBytes, 4));

        exception.Code.ShouldBe(ClientImportErrorCodes.UnpackedSizeExceeded);
    }

    [Test]
    public void RepeatedSharedStrings_AboveTheCharacterBudget_AreRejected()
    {
        const int columns = 60;
        var text = new string('x', ClientImportLimits.MaxCellLength);
        var rowCount = (int)(ClientImportLimits.MaxGridChars / (ClientImportLimits.MaxCellLength * columns)) + 1;
        var content = Workbook(sheet =>
        {
            for (var row = 1; row <= rowCount; row++)
            {
                for (var column = 1; column <= columns; column++)
                {
                    sheet.Cell(row, column).Value = text;
                }
            }
        });

        var exception = Should.Throw<ClientImportRejectedException>(() => _reader.Read(content, null));

        exception.Code.ShouldBe(ClientImportErrorCodes.FileTooLarge);
    }

    [Test]
    public void UnreadableArchive_IsRejectedWithoutTheLibraryMessage()
    {
        var exception = Should.Throw<ClientImportRejectedException>(() => _reader.Read([1, 2, 3, 4], null));

        exception.Code.ShouldBe(ClientImportErrorCodes.FileUnsupported);
        exception.Message.ShouldBe("The file is not a readable xlsx archive.");
        exception.InnerException.ShouldNotBeNull();
    }

    [Test]
    public void TooManyColumns_IsRejected()
    {
        var content = Workbook(sheet => sheet.Cell(1, ClientImportLimits.MaxColumns + 1).Value = "x");

        var exception = Should.Throw<ClientImportRejectedException>(() => _reader.Read(content, null));

        exception.Code.ShouldBe(ClientImportErrorCodes.TooManyColumns);
    }

    private static byte[] Workbook(Action<IXLWorksheet> fill, bool evaluateFormulas = false)
    {
        using var workbook = new XLWorkbook();
        fill(workbook.Worksheets.Add("Sheet1"));
        return Save(workbook, evaluateFormulas);
    }

    private static byte[] Save(XLWorkbook workbook, bool evaluateFormulas = false)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream, new SaveOptions { EvaluateFormulasBeforeSaving = evaluateFormulas });
        return stream.ToArray();
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests verifying that ClientPeriodCsvExportFormatter exports work changes and expenses
/// (as Change/Expense records) like the XML/JSON formatters, with stable existing columns.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ClientPeriodCsvChangesExpensesTests
{
    private ClientPeriodCsvExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new ClientPeriodCsvExportFormatter();
    }

    [Test]
    public void Header_AppendsNewColumnsAtEnd()
    {
        var header = ReadLines()[0].Split(';');

        header.Length.ShouldBe(17);
        header[10].ShouldBe("Information");
        header[11].ShouldBe("OnCall");
        header.Skip(12).ShouldBe(["ChangeType", "Amount", "Taxable", "ToInvoice", "ReplaceEmployee"]);
    }

    [Test]
    public void AllRows_HaveSameColumnCountAsHeader()
    {
        foreach (var line in ReadLines())
        {
            line.Split(';').Length.ShouldBe(17, line);
        }
    }

    [Test]
    public void ChangeRow_IsWritten()
    {
        var cells = ReadLines().First(l => l.StartsWith("Change;")).Split(';');

        cells[4].ShouldBe("2026-01-10");
        cells[6].ShouldBe("09:00");
        cells[7].ShouldBe("10:00");
        cells[8].ShouldBe("1.00");
        cells[10].ShouldBe("change desc");
        cells[15].ShouldBe("1");
        cells[16].ShouldBe("Replacement, Rita");
    }

    [Test]
    public void ExpenseRow_IsWritten()
    {
        var cells = ReadLines().First(l => l.StartsWith("Expense;")).Split(';');

        cells[4].ShouldBe("2026-01-10");
        cells[10].ShouldBe("taxi");
        cells[13].ShouldBe("12.50");
        cells[14].ShouldBe("1");
    }

    private string[] ReadLines()
    {
        var data = new ClientPeriodExportData
        {
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = "Employee, Eve",
                    ClientIdNumber = 7,
                    ClientType = EntityTypeEnum.Employee,
                    WorkEntries =
                    [
                        new ClientWorkExportEntry
                        {
                            WorkId = Guid.NewGuid(),
                            WorkDate = new DateOnly(2026, 1, 10),
                            StartTime = new TimeOnly(8, 0),
                            EndTime = new TimeOnly(16, 0),
                            WorkTime = 8m,
                            Changes =
                            [
                                new WorkChangeExportEntry
                                {
                                    ChangeTime = 1m,
                                    StartTime = new TimeOnly(9, 0),
                                    EndTime = new TimeOnly(10, 0),
                                    Description = "change desc",
                                    ReplaceEmployeeName = "Replacement, Rita",
                                    ToInvoice = true,
                                },
                            ],
                            Expenses =
                            [
                                new ExpensesExportEntry { Amount = 12.5m, Description = "taxi", Taxable = true },
                            ],
                        },
                    ],
                },
            ],
        };

        var text = Encoding.UTF8.GetString(_formatter.Format(data, new ExportOptions())).TrimStart('﻿');
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }
}

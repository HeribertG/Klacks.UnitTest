// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for XmlTextSanitizer and for the XML formatters surviving characters that are
/// invalid in XML 1.0 (control characters, lone surrogates, U+FFFE/U+FFFF).
/// </summary>
using System.Text;
using System.Xml.Linq;
using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class XmlTextSanitizerTests
{
    private const string Dirty = "a\u0001b\u0008c\uD800d\uDC00e￾f￿g\u000Bh";

    [Test]
    public void Clean_RemovesInvalidCharacters_KeepsValidOnes()
    {
        XmlTextSanitizer.Clean(Dirty).ShouldBe("abcdefgh");
        XmlTextSanitizer.Clean("t\tn\nr\rü€").ShouldBe("t\tn\nr\rü€");
        XmlTextSanitizer.Clean("x😀y").ShouldBe("x😀y");
    }

    [Test]
    public void Clean_HandlesNullAndEmpty()
    {
        XmlTextSanitizer.Clean(null).ShouldBe(string.Empty);
        XmlTextSanitizer.Clean(string.Empty).ShouldBe(string.Empty);
    }

    [Test]
    public void OrderXml_DoesNotThrow_ForControlCharactersInFreeText()
    {
        var data = new OrderExportData
        {
            Orders =
            [
                new OrderGroup
                {
                    OrderShiftId = Guid.NewGuid(),
                    OrderName = Dirty,
                    OrderAbbreviation = Dirty,
                    CustomerName = Dirty,
                    WorkEntries =
                    [
                        new WorkExportEntry
                        {
                            EmployeeName = Dirty,
                            Information = Dirty,
                            Changes = [new WorkChangeExportEntry { Description = Dirty, ReplaceEmployeeName = Dirty }],
                            Breaks = [new BreakExportEntry { AbsenceName = Dirty }],
                        },
                    ],
                },
            ],
        };

        var bytes = new XmlExportFormatter().Format(data, new ExportOptions { CurrencyCode = Dirty });
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        doc.Root!.Element("Order")!.Element("Name")!.Value.ShouldBe("abcdefgh");
    }

    [Test]
    public void ClientPeriodXml_DoesNotThrow_ForControlCharactersInFreeText()
    {
        var data = new ClientPeriodExportData
        {
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = Dirty,
                    ClientType = EntityTypeEnum.Employee,
                    WorkEntries =
                    [
                        new ClientWorkExportEntry
                        {
                            Information = Dirty,
                            Changes = [new WorkChangeExportEntry { Description = Dirty, ReplaceEmployeeName = Dirty }],
                            Expenses = [new ExpensesExportEntry { Description = Dirty }],
                            Breaks = [new BreakExportEntry { AbsenceName = Dirty }],
                        },
                    ],
                    Absences = [new BreakExportEntry { AbsenceName = Dirty }],
                    PeriodHours = [new ClientPeriodHoursExportEntry { PaymentInterval = Dirty }],
                },
            ],
        };

        var bytes = new ClientPeriodXmlExportFormatter().Format(data, new ExportOptions { CurrencyCode = Dirty });
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        doc.Root!.Element("Client")!.Element("Name")!.Value.ShouldBe("abcdefgh");
    }
}

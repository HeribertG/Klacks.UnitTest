// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for XmlExportFormatter verifying that the ERP reference elements
/// (SourceSystemId, ExternalOrderReference, ExternalCustomerReference) are written when
/// populated and omitted when empty.
/// </summary>
using System.Text;
using System.Xml.Linq;
using Shouldly;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class XmlExportFormatterErpReferencesTests
{
    private XmlExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new XmlExportFormatter();
    }

    [Test]
    public void Format_WritesErpReferenceElements_WhenPopulated()
    {
        var data = BuildData(order =>
        {
            order.SourceSystemId = "erp-main";
            order.ExternalOrderReference = "PO-4711";
            order.CustomerExternalReference = "CUST-42";
        });

        var order = ParseFirstOrder(data);

        order.Element("SourceSystemId")!.Value.ShouldBe("erp-main");
        order.Element("ExternalOrderReference")!.Value.ShouldBe("PO-4711");
        order.Element("Customer")!.Element("ExternalCustomerReference")!.Value.ShouldBe("CUST-42");
    }

    [Test]
    public void Format_WritesErpReferenceElements_DirectlyAfterAbbreviation()
    {
        var data = BuildData(order =>
        {
            order.SourceSystemId = "erp-main";
            order.ExternalOrderReference = "PO-4711";
        });

        var order = ParseFirstOrder(data);
        var elementNames = order.Elements().Select(e => e.Name.LocalName).ToList();

        var abbreviationIndex = elementNames.IndexOf("Abbreviation");
        elementNames[abbreviationIndex + 1].ShouldBe("SourceSystemId");
        elementNames[abbreviationIndex + 2].ShouldBe("ExternalOrderReference");
    }

    [Test]
    public void Format_OmitsErpReferenceElements_WhenNull()
    {
        var data = BuildData(_ => { });

        var order = ParseFirstOrder(data);

        order.Element("SourceSystemId").ShouldBeNull();
        order.Element("ExternalOrderReference").ShouldBeNull();
        order.Element("Customer")!.Element("ExternalCustomerReference").ShouldBeNull();
    }

    [Test]
    public void Format_OmitsErpReferenceElements_WhenEmpty()
    {
        var data = BuildData(order =>
        {
            order.SourceSystemId = string.Empty;
            order.ExternalOrderReference = string.Empty;
            order.CustomerExternalReference = string.Empty;
        });

        var order = ParseFirstOrder(data);

        order.Element("SourceSystemId").ShouldBeNull();
        order.Element("ExternalOrderReference").ShouldBeNull();
        order.Element("Customer")!.Element("ExternalCustomerReference").ShouldBeNull();
    }

    private XElement ParseFirstOrder(OrderExportData data)
    {
        var bytes = _formatter.Format(data, new ExportOptions { CurrencyCode = "EUR" });
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        return xml.Root!.Elements("Order").First();
    }

    private static OrderExportData BuildData(Action<OrderGroup> customize)
    {
        var order = new OrderGroup
        {
            OrderShiftId = Guid.NewGuid(),
            OrderName = "Night Watch",
            OrderAbbreviation = "NW",
            CustomerId = Guid.NewGuid(),
            CustomerNumber = 77,
            CustomerName = "Muster, Anna",
            WorkEntries =
            [
                new WorkExportEntry
                {
                    WorkId = Guid.NewGuid(),
                    EmployeeId = Guid.NewGuid(),
                    EmployeeName = "Worker, One",
                    EmployeeIdNumber = 1,
                    WorkDate = new DateOnly(2026, 1, 10),
                    StartTime = new TimeOnly(8, 0),
                    EndTime = new TimeOnly(16, 0),
                    WorkTime = 8m,
                    Surcharges = 0m,
                },
            ],
        };

        customize(order);

        return new OrderExportData
        {
            Orders = [order],
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
        };
    }
}

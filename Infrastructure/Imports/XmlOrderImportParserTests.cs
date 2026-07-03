using System.Text;
using Klacks.Api.Infrastructure.Services.Imports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Imports;

[TestFixture]
public class XmlOrderImportParserTests
{
    private XmlOrderImportParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _parser = new XmlOrderImportParser();
    }

    private static Stream ToStream(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    [Test]
    public void Parse_ValidBatch_ReturnsOrdersWithoutErrors()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-1</ExternalOrderReference>
                <Description>Morning care visit</Description>
                <Customer>
                  <ExternalCustomerReference>CUST-1</ExternalCustomerReference>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                    <City>Basel</City>
                    <State>BS</State>
                    <Country>CH</Country>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <UntilDate>2026-12-31</UntilDate>
                <StartTime>07:00</StartTime>
                <EndTime>15:00</EndTime>
                <IsTimeRange>true</IsTimeRange>
                <Duration>
                  <Hours>0</Hours>
                  <Minutes>20</Minutes>
                </Duration>
                <Weekdays monday="true" tuesday="true" wednesday="true" thursday="true" friday="true" saturday="false" sunday="false" />
                <Quantity>1</Quantity>
                <SumEmployees>1</SumEmployees>
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeFalse();
        result.SchemaVersion.ShouldBe(1);
        result.Orders.Count.ShouldBe(1);

        var order = result.Orders[0];
        order.SourceSystemId.ShouldBe("erp-1");
        order.ExternalOrderReference.ShouldBe("ORD-1");
        order.Description.ShouldBe("Morning care visit");
        order.DurationMinutes.ShouldBe(20);
        order.Customer.Company.ShouldBe("Spitex Musterhausen");
        order.Customer.ExternalCustomerReference.ShouldBe("CUST-1");
        order.Customer.State.ShouldBe("BS");
        order.FromDate.ShouldBe(new DateOnly(2026, 8, 1));
        order.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
        order.StartTime.ShouldBe(new TimeOnly(7, 0));
        order.EndTime.ShouldBe(new TimeOnly(15, 0));
        order.IsMonday.ShouldBeTrue();
        order.IsSaturday.ShouldBeFalse();
    }

    [Test]
    public void Parse_TimeRangeOrderWithoutDuration_ReturnsErrorAndSkipsOrder()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-1</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>08:00</StartTime>
                <EndTime>15:00</EndTime>
                <IsTimeRange>true</IsTimeRange>
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeTrue();
        result.Orders.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.Field == "Duration");
    }

    [Test]
    public void Parse_DurationWithNegativeHours_ReturnsErrorAndSkipsOrder()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-1</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>08:00</StartTime>
                <EndTime>15:00</EndTime>
                <Duration>
                  <Hours>-1</Hours>
                  <Minutes>20</Minutes>
                </Duration>
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeTrue();
        result.Orders.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.Field == "Duration");
    }

    [Test]
    public void Parse_DurationWithZeroTotal_ReturnsErrorAndSkipsOrder()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-1</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>08:00</StartTime>
                <EndTime>15:00</EndTime>
                <Duration>
                  <Hours>0</Hours>
                  <Minutes>0</Minutes>
                </Duration>
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeTrue();
        result.Orders.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.Field == "Duration");
    }

    [Test]
    public void Parse_DurationWithMinutesAboveSixty_ReturnsTotalMinutes()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-1</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>08:00</StartTime>
                <EndTime>15:00</EndTime>
                <Duration>
                  <Minutes>90</Minutes>
                </Duration>
                <Weekdays monday="true" />
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeFalse();
        result.Orders.Count.ShouldBe(1);
        result.Orders[0].DurationMinutes.ShouldBe(90);
    }

    [Test]
    public void Parse_DurationWithHoursAndMinutes_SumsToTotalMinutes()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-1</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>08:00</StartTime>
                <EndTime>15:00</EndTime>
                <Duration>
                  <Hours>1</Hours>
                  <Minutes>30</Minutes>
                </Duration>
                <Weekdays monday="true" />
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeFalse();
        result.Orders.Count.ShouldBe(1);
        result.Orders[0].DurationMinutes.ShouldBe(90);
    }

    [Test]
    public void Parse_UnsupportedSchemaVersion_ReturnsErrorAndNoOrders()
    {
        var xml = """<ErpOrderImport schemaVersion="99" sourceSystemId="erp-1"></ErpOrderImport>""";

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeTrue();
        result.Orders.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.Field == "schemaVersion");
    }

    [Test]
    public void Parse_MissingSourceSystemId_ReturnsErrorAndNoOrders()
    {
        var xml = """<ErpOrderImport schemaVersion="1"></ErpOrderImport>""";

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeTrue();
        result.Orders.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.Field == "sourceSystemId");
    }

    [Test]
    public void Parse_OneInvalidOrderAmongValidOnes_SkipsOnlyTheInvalidOne()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-VALID</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>07:00</StartTime>
                <EndTime>15:00</EndTime>
                <Weekdays monday="true" />
              </Order>
              <Order>
                <ExternalOrderReference>ORD-INVALID</ExternalOrderReference>
                <Customer>
                  <Company>Fehlende Adresse AG</Company>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>07:00</StartTime>
                <EndTime>15:00</EndTime>
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.HasErrors.ShouldBeTrue();
        result.Orders.Count.ShouldBe(1);
        result.Orders[0].ExternalOrderReference.ShouldBe("ORD-VALID");
        result.Errors.ShouldContain(e => e.Field == "Address");
    }

    [Test]
    public void Parse_MalformedXml_ReturnsError()
    {
        var result = _parser.Parse(ToStream("<ErpOrderImport schemaVersion=\"1\""));

        result.HasErrors.ShouldBeTrue();
        result.Orders.ShouldBeEmpty();
    }

    [Test]
    public void Parse_OrderWithoutAnyWeekday_IsRejectedWithStampedReference()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-NO-DAYS</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>07:00</StartTime>
                <EndTime>15:00</EndTime>
                <Weekdays monday="false" sunday="false" />
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.Orders.ShouldBeEmpty();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Field.ShouldBe("Weekdays");
        error.ExternalOrderReference.ShouldBe("ORD-NO-DAYS");
    }

    [Test]
    public void Parse_MissingWeekdaysElement_IsRejected()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-NO-DAYS</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>07:00</StartTime>
                <EndTime>15:00</EndTime>
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.Orders.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.Field == "Weekdays");
    }

    [Test]
    public void Parse_DuplicateExternalOrderReferenceInFile_RejectsTheDuplicate()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-TWICE</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <StartTime>07:00</StartTime>
                <EndTime>15:00</EndTime>
                <Weekdays monday="true" />
              </Order>
              <Order>
                <ExternalOrderReference>ORD-TWICE</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-09-01</FromDate>
                <StartTime>08:00</StartTime>
                <EndTime>16:00</EndTime>
                <Weekdays tuesday="true" />
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        var order = result.Orders.ShouldHaveSingleItem("only the first occurrence may be imported");
        order.FromDate.ShouldBe(new DateOnly(2026, 8, 1));
        var error = result.Errors.ShouldHaveSingleItem();
        error.Field.ShouldBe("ExternalOrderReference");
        error.ExternalOrderReference.ShouldBe("ORD-TWICE");
        error.Message.ShouldContain("Duplicate");
    }

    [Test]
    public void Parse_UntilDateBeforeFromDate_IsRejected()
    {
        var xml = """
            <ErpOrderImport schemaVersion="1" sourceSystemId="erp-1">
              <Order>
                <ExternalOrderReference>ORD-BACKWARDS</ExternalOrderReference>
                <Customer>
                  <Company>Spitex Musterhausen</Company>
                  <Address>
                    <Street>Hauptstrasse 5</Street>
                    <Zip>4000</Zip>
                  </Address>
                </Customer>
                <FromDate>2026-08-01</FromDate>
                <UntilDate>2026-07-01</UntilDate>
                <StartTime>07:00</StartTime>
                <EndTime>15:00</EndTime>
                <Weekdays monday="true" />
              </Order>
            </ErpOrderImport>
            """;

        var result = _parser.Parse(ToStream(xml));

        result.Orders.ShouldBeEmpty();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Field.ShouldBe("UntilDate");
        error.ExternalOrderReference.ShouldBe("ORD-BACKWARDS");
    }
}

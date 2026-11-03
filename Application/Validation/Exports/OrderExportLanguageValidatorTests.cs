// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the Language validation of the order export queries.
/// </summary>
using Shouldly;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Application.Validation.Exports;

namespace Klacks.UnitTest.Application.Validation.Exports;

[TestFixture]
public class OrderExportLanguageValidatorTests
{
    [TestCase("de", true)]
    [TestCase("en-US", true)]
    [TestCase("xx", true)]
    [TestCase("zh-Hant-TW", true)]
    [TestCase("", false)]
    [TestCase("de;DROP", false)]
    [TestCase("this is not a language tag", false)]
    public void OrderExport_ValidatesLanguageShape(string language, bool expectedValid)
    {
        var query = new CreateOrderExportQuery(new OrderExportFilter { Language = language });

        new CreateOrderExportQueryValidator().Validate(query).IsValid.ShouldBe(expectedValid);
    }

    [TestCase("de", true)]
    [TestCase("", false)]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void OrderRangeExport_ValidatesLanguageShape(string language, bool expectedValid)
    {
        var query = new CreateOrderRangeExportQuery(new OrderRangeExportFilter { Language = language });

        new CreateOrderRangeExportQueryValidator().Validate(query).IsValid.ShouldBe(expectedValid);
    }
}

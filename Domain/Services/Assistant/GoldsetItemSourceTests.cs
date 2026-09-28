// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins how an item id of any learning goldset maps back to its default-goldset source item.
/// </summary>
namespace Klacks.UnitTest.Domain.Services.Assistant;

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetItemSourceTests
{
    [TestCase("ts-011", "ts-011")]
    [TestCase("i18n-ja--ts-011", "ts-011")]
    [TestCase("i18n-zh-CN--w05-coverage-add-ai-memory", "w05-coverage-add-ai-memory")]
    [TestCase("para-ts-011-2", "ts-011")]
    [TestCase("para-w05-coverage-add-ai-memory-3", "w05-coverage-add-ai-memory")]
    [TestCase("i18n-ja-ts-011", "i18n-ja-ts-011")]
    public void Resolve_MapsDerivedIdsToTheirSource(string itemId, string expected)
    {
        GoldsetItemSource.Resolve(itemId).ShouldBe(expected);
    }
}

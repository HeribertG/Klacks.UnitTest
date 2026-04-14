// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using FluentAssertions;
using Klacks.Api.Application.Klacksy;
using NUnit.Framework;

[TestFixture]
public class NavigationTargetCacheServiceTests
{
    [Test]
    public void FindBySynonym_returns_targets_matching_token_in_locale()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        [{
          "targetId":"llm-provider","route":"/settings","labelKey":"settings.llm",
          "synonyms":{"de":["llm provider","ki anbieter"]},"synonymStatus":"reviewed"
        }]
        """);
        var sut = new NavigationTargetCacheService(tempFile, pluginFolder: null);

        sut.FindBySynonym("ki anbieter", "de").Should().HaveCount(1);
        sut.FindBySynonym("unknown", "de").Should().BeEmpty();
        sut.GetById("llm-provider").Should().NotBeNull();
    }
}

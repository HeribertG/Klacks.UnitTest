// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for EdgeTtsSecMsGecGenerator: v2 token must match the edge-tts 7.3.1 reference output.
/// </summary>

using Klacks.Api.Infrastructure.Services.Assistant;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class EdgeTtsSecMsGecGeneratorTests
{
    private const string TokenAt1792922782 =
        "9FB854FCF6214398-844BB26CA0B5B5D09B0A54BF4E622BDB3C20626D4E2E952AAC70A93538C42F8A";

    [TestCase(0L, "52BC7A17B50DFEC9-28C4394775C8751CD2F6859C030C06DB1489ACAB3B3A7651831FC26895177ED6")]
    [TestCase(1735689600L, "54404AFE67C9B44A-E72E51D8B4F5F75E9A96B7E20D7877227455BC310433BD30C817CD54A1D9313E")]
    [TestCase(1792922782L, TokenAt1792922782)]
    [TestCase(1792922999L, TokenAt1792922782)]
    public void Generate_MatchesReferenceImplementation(long unixSeconds, string expected)
    {
        EdgeTtsSecMsGecGenerator.Generate(unixSeconds).ShouldBe(expected);
    }

    [Test]
    public void Generate_ChangesAcrossFiveMinuteWindowBoundary()
    {
        var before = EdgeTtsSecMsGecGenerator.Generate(1792922999L);
        var after = EdgeTtsSecMsGecGenerator.Generate(1792923000L);

        after.ShouldNotBe(before);
    }

    [TestCase(0L)]
    [TestCase(1735689600L)]
    [TestCase(1792922782L)]
    public void Generate_ReturnsCs64DashSha256UpperHex(long unixSeconds)
    {
        System.Text.RegularExpressions.Regex.IsMatch(
            EdgeTtsSecMsGecGenerator.Generate(unixSeconds), "^[0-9A-F]{16}-[0-9A-F]{64}$").ShouldBeTrue();
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

[TestFixture]
public class PseudonymStreamRestorerTests
{
    private PseudonymStreamRestorer _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var map = Map(Dictionary([Employee(17, "Anna", "Meier"), Employee(18, "Jürg", "Müller", GenderEnum.Male)]));
        map.MarkEmitted("MA-0017");
        map.MarkEmitted("MA-0018-N");
        _sut = new PseudonymStreamRestorer(map, new List<string>());
    }

    private string Run(params string[] chunks)
    {
        var output = chunks.SelectMany(chunk => _sut.Push(chunk)).ToList();
        output.AddRange(_sut.Flush());
        return string.Concat(output);
    }

    [Test]
    public void TokenSplitAcrossChunks_IsRestored()
    {
        Run("Hallo M", "A-00", "17 und MA-0", "018-N.").ShouldBe("Hallo Anna Meier und Müller.");
    }

    [Test]
    public void StreamEndingInsideAToken_IsFlushed()
    {
        Run("Gruss an MA-00", "17").ShouldBe("Gruss an Anna Meier");
        SetUp();
        Run("Gruss an MA-00").ShouldBe("Gruss an MA-00");
    }

    [Test]
    public void PlainTextChunks_PassThroughWithoutWaitingForTheEnd()
    {
        _sut.Push("Der Dienst ist").ShouldBe(["Der Dienst "]);
        _sut.Push(" geplant.").ShouldBe(["ist geplant."]);
    }

    [Test]
    public void ToolDeltas_AreHeldAndEmittedOncePerIndexWithRestoredArguments()
    {
        string Delta(int index, string name, string args) =>
            LLMStreamingTokens.ToolCallPrefix + JsonSerializer.Serialize(new { toolCall = true, index, name, arguments = args });

        var output = new List<string>();
        output.AddRange(_sut.Push(Delta(0, "search_", "{\"name\":\"MA-")));
        output.AddRange(_sut.Push(Delta(0, "clients", "0017-N\"}")));
        output.AddRange(_sut.Push(Delta(1, "add_note", "{\"text\":\"MA-0018 krank\"}")));
        output.ShouldBeEmpty();

        output.AddRange(_sut.Push(LLMStreamingTokens.ToolCallEnd));

        output.Count.ShouldBe(3);
        output[2].ShouldBe(LLMStreamingTokens.ToolCallEnd);
        using var first = JsonDocument.Parse(output[0][LLMStreamingTokens.ToolCallPrefix.Length..]);
        first.RootElement.GetProperty("index").GetInt32().ShouldBe(0);
        first.RootElement.GetProperty("toolCall").GetBoolean().ShouldBeTrue();
        first.RootElement.GetProperty("name").GetString().ShouldBe("search_clients");
        JsonDocument.Parse(first.RootElement.GetProperty("arguments").GetString()!)
            .RootElement.GetProperty("name").GetString().ShouldBe("Meier");
        using var second = JsonDocument.Parse(output[1][LLMStreamingTokens.ToolCallPrefix.Length..]);
        JsonDocument.Parse(second.RootElement.GetProperty("arguments").GetString()!)
            .RootElement.GetProperty("text").GetString().ShouldBe("Jürg Müller krank");
    }

    [Test]
    public void ToolDeltasWithoutEndMarker_AreReleasedAtFlush()
    {
        _sut.Push(LLMStreamingTokens.ToolCallPrefix + "{\"index\":0,\"name\":\"x\",\"arguments\":\"{}\"}").ShouldBeEmpty();

        _sut.Flush().Single().ShouldStartWith(LLMStreamingTokens.ToolCallPrefix);
    }
}

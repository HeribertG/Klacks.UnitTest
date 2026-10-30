// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Reading the audience of a broadcast skill call: exactly one of group and id numbers, in the shapes a model call and a stored
/// confirmation carry (a confirmation comes back as JsonElement), and the refusals, including the empty group id the UI uses as
/// its "all groups" marker.
/// </summary>

using System.Text.Json;
using Klacks.Plugin.Messaging.Application.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class BroadcastAudienceParametersTests
{
    private static Dictionary<string, object> Params(string name, object value) => new() { [name] = value };

    private static object Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public void AGroupId_IsRead()
    {
        var id = Guid.NewGuid();

        BroadcastAudienceParameters.Parse(Params("groupId", id.ToString())).GroupId.ShouldBe(id);
    }

    [Test]
    public void AGroupIdFromAStoredConfirmation_IsRead()
    {
        var id = Guid.NewGuid();

        BroadcastAudienceParameters.Parse(Params("groupId", Json($"\"{id}\""))).GroupId.ShouldBe(id);
    }

    [TestCase("[5002, 5043]")]
    [TestCase("[\"5002\", \"5043\", 5002]")]
    [TestCase("\"5002, 5043\"")]
    public void IdNumbersAsJson_AreRead(string json) =>
        BroadcastAudienceParameters.Parse(Params("idNumbers", Json(json))).IdNumbers.ShouldBe(new[] { 5002, 5043 });

    [Test]
    public void IdNumbersAsAList_AreRead() =>
        BroadcastAudienceParameters.Parse(Params("idNumbers", new List<object> { 5002, "5043" })).IdNumbers.ShouldBe(new[] { 5002, 5043 });

    [Test]
    public void NoAudience_IsRefused() =>
        Should.Throw<ArgumentException>(() => BroadcastAudienceParameters.Parse(new Dictionary<string, object>()));

    [Test]
    public void BothAudiences_AreRefused() =>
        Should.Throw<ArgumentException>(() => BroadcastAudienceParameters.Parse(
            new Dictionary<string, object> { ["groupId"] = Guid.NewGuid().ToString(), ["idNumbers"] = "5002" }));

    [Test]
    public void TheEmptyGroupId_IsRefused() =>
        Should.Throw<ArgumentException>(() => BroadcastAudienceParameters.Parse(Params("groupId", Guid.Empty.ToString())));

    [Test]
    public void ABrokenNumber_IsRefused() =>
        Should.Throw<ArgumentException>(() => BroadcastAudienceParameters.Parse(Params("idNumbers", "5002, Hans")));

    [Test]
    public void AnEmptyText_IsRefused() =>
        Should.Throw<ArgumentException>(() => SendBroadcastParameters.Parse(new Dictionary<string, object>
        {
            ["groupId"] = Guid.NewGuid().ToString(),
            ["content"] = "   "
        }));
}

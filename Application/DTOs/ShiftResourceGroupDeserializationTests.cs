// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Reproduces the group-wipe bug: a shift PUT receives groups but the round-trip drops the group id.
/// If SimpleGroupResource.Id does not survive camelCase deserialization the GroupItems sync sees an
/// empty GroupId for every incoming group, matches nothing, and soft-deletes all existing groups.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.DTOs;

[TestFixture]
public class ShiftResourceGroupDeserializationTests
{
    [Test]
    public void Group_Id_Should_Survive_CamelCase_Deserialization()
    {
        var gid = Guid.Parse("706e2414-9aa4-46e3-8143-a49eca1f0a44");
        const string json =
            "{\"groups\":[{\"description\":\"\",\"id\":\"706e2414-9aa4-46e3-8143-a49eca1f0a44\"," +
            "\"name\":\"Westschweiz\",\"validFrom\":\"0001-01-01T00:00:00\",\"validUntil\":null," +
            "\"paymentInterval\":0,\"calendarSelectionId\":null,\"calendarSelection\":null}]}";

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var resource = JsonSerializer.Deserialize<ShiftResource>(json, options)!;

        resource.Groups.Count.ShouldBe(1);
        resource.Groups[0].Id.ShouldBe(gid, "SimpleGroupResource.Id must deserialize from camelCase 'id'");
    }
}

// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the three-state wire contract of ClientAvailabilityResource: reads always carry true/false, and on the
/// write side a JSON "isAvailable": null (or an absent property) arrives as null, which the bulk update treats as
/// "remove the slot".
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;

namespace Klacks.UnitTest.Mappers;

[TestFixture]
public class ClientAvailabilityThreeStateMapperTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ClientAvailabilityMapper _mapper = new();

    [TestCase(true)]
    [TestCase(false)]
    public void ToResource_AlwaysCarriesTheStoredBoolean(bool stored)
    {
        var entity = new ClientAvailability
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Date = new DateOnly(2026, 11, 9), Hour = 8, IsAvailable = stored,
        };

        var resource = _mapper.ToResource(entity);

        resource.IsAvailable.ShouldBe(stored);
        resource.IsAvailable.HasValue.ShouldBeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ToEntity_MapsAProvidedValue(bool value)
    {
        var resource = new ClientAvailabilityResource
        {
            ClientId = Guid.NewGuid(), Date = new DateOnly(2026, 11, 9), Hour = 8, IsAvailable = value,
        };

        _mapper.ToEntity(resource).IsAvailable.ShouldBe(value);
    }

    [Test]
    public void Deserialize_ExplicitNull_IsNull()
    {
        var resource = JsonSerializer.Deserialize<ClientAvailabilityResource>(
            """{"clientId":"6f1c0d3e-0000-4000-8000-000000000001","date":"2026-11-09","hour":8,"isAvailable":null}""",
            WebJson)!;

        resource.IsAvailable.ShouldBeNull();
    }

    [Test]
    public void Deserialize_AbsentProperty_IsRefused()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ClientAvailabilityResource>(
            """{"clientId":"6f1c0d3e-0000-4000-8000-000000000001","date":"2026-11-09","hour":8}""",
            WebJson));
    }

    [TestCase(true, "true")]
    [TestCase(false, "false")]
    public void Serialize_ReadResource_WritesTheBoolean(bool value, string expectedJson)
    {
        var json = JsonSerializer.Serialize(new ClientAvailabilityResource { IsAvailable = value }, WebJson);

        json.ShouldContain($"\"isAvailable\":{expectedJson}");
    }
}

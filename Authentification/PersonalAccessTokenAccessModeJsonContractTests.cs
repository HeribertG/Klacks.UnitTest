// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the JSON contract between Klacks.Ui and the token endpoints: the access mode travels as the strings
/// "Read"/"Write" (the Angular model's PersonalAccessTokenAccessMode), a create request without it stays null so
/// the handler applies the Read default, and numbers are not what the API emits.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Authentification;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class PersonalAccessTokenAccessModeJsonContractTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [TestCase("Write", PersonalAccessTokenAccessMode.Write)]
    [TestCase("Read", PersonalAccessTokenAccessMode.Read)]
    public void CreateRequest_ReadsTheModeFromItsName(string wireValue, PersonalAccessTokenAccessMode expected)
    {
        var request = JsonSerializer.Deserialize<CreatePersonalAccessTokenRequest>(
            $$"""{"name":"agent","accessMode":"{{wireValue}}"}""", WebOptions);

        request!.AccessMode.ShouldBe(expected);
    }

    [Test]
    public void CreateRequest_WithoutMode_StaysNull()
    {
        var request = JsonSerializer.Deserialize<CreatePersonalAccessTokenRequest>("""{"name":"agent"}""", WebOptions);

        request!.AccessMode.ShouldBeNull();
    }

    [Test]
    public void ListItem_SerializesTheModeAsString()
    {
        var item = new PersonalAccessTokenListItemDto(
            Guid.NewGuid(), "agent", "klacks_pat_ab", null, null, null, PersonalAccessTokenAccessMode.Read);

        JsonSerializer.Serialize(item, WebOptions).ShouldContain("\"accessMode\":\"Read\"");
    }

    [Test]
    public void CreatedDto_SerializesTheModeAsString()
    {
        var created = new PersonalAccessTokenCreatedDto(
            Guid.NewGuid(), "agent", "klacks_pat_ab", DateTime.UtcNow, "secret", PersonalAccessTokenAccessMode.Write);

        JsonSerializer.Serialize(created, WebOptions).ShouldContain("\"accessMode\":\"Write\"");
    }
}

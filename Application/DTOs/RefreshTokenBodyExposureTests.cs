// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The refresh token travels only in the HttpOnly cookie: the session responses (TokenResource for login, cookie refresh
/// and the OAuth2 callback, AuthenticatedResult for the password change) must not serialize it into the body, while the
/// access token stays in the body. Only MigrationTokenResource, the answer to a body-token refresh of a client from before
/// the cookie, carries it as "refreshToken", once.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Application.DTOs;

[TestFixture]
public class RefreshTokenBodyExposureTests
{
    private const string RefreshToken = "secret-refresh-token";
    private const string AccessToken = "access-token";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void TokenResource_DoesNotSerializeTheRefreshToken()
    {
        var json = JsonSerializer.Serialize(new TokenResource { Token = AccessToken, RefreshToken = RefreshToken }, WebOptions);

        json.ShouldNotContain(RefreshToken);
        json.ShouldNotContain("refreshToken");
        json.ShouldContain(AccessToken);
    }

    [Test]
    public void MigrationTokenResource_SerializesTheRefreshTokenOnceUnderItsOldName()
    {
        var json = JsonSerializer.Serialize(new MigrationTokenResource { Token = AccessToken, RefreshToken = RefreshToken }, WebOptions);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("refreshToken").GetString().ShouldBe(RefreshToken);
        document.RootElement.EnumerateObject().Count(p => p.Value.ToString() == RefreshToken).ShouldBe(1);
        json.ShouldContain(AccessToken);
    }

    [Test]
    public void AuthenticatedResult_DoesNotSerializeTheRefreshToken()
    {
        var json = JsonSerializer.Serialize(new AuthenticatedResult { Token = AccessToken, RefreshToken = RefreshToken }, WebOptions);

        json.ShouldNotContain(RefreshToken);
        json.ShouldNotContain("refreshToken");
        json.ShouldContain(AccessToken);
    }
}

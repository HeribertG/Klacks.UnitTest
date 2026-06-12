// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Authentication;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthAuthorizationCodeStoreTests
{
    private const string Code = "code-1";
    private const string UnknownCode = "code-unknown";

    private OAuthAuthorizationCodeStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new OAuthAuthorizationCodeStore();
    }

    [Test]
    public void Consume_UnknownCode_ReturnsNull()
    {
        _store.Consume(UnknownCode).ShouldBeNull();
    }

    [Test]
    public void Consume_StoredCode_ReturnsDataExactlyOnce()
    {
        var data = Data();
        _store.Store(Code, data);

        _store.Consume(Code).ShouldBe(data);
        _store.Consume(Code).ShouldBeNull();
    }

    private static OAuthAuthorizationCodeData Data()
    {
        return new OAuthAuthorizationCodeData(
            UserId: "user-1",
            ClientId: "client-1",
            ClientName: "Claude",
            RedirectUri: "https://claude.ai/api/mcp/auth_callback",
            CodeChallenge: "challenge",
            Scope: null);
    }
}

using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.Bots;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Infrastructure.Authentication;

/// <summary>
/// Guards the security property that makes the Klacks bot token scheme safe to use: stacking
/// [Authorize(AuthenticationSchemes=...)] on a class derived from BaseController (which itself
/// pins the JWT scheme) unions the accepted schemes instead of replacing them, which would let
/// any ordinary JWT-authenticated user reach the bot-only endpoint too. If a future refactor
/// moves BotQueryController onto BaseController for consistency, this test must fail loudly.
/// </summary>
[TestFixture]
public class BotQueryControllerAuthorizationTests
{
    [Test]
    public void BotQueryController_DoesNotInheritBaseController()
    {
        typeof(BaseController).IsAssignableFrom(typeof(BotQueryController)).ShouldBeFalse(
            "BotQueryController must not inherit BaseController: BaseController pins the JWT scheme, " +
            "and a second [Authorize(AuthenticationSchemes=...)] on a derived class unions schemes " +
            "instead of replacing them, letting JWT-authenticated users reach the bot-only endpoint.");
    }

    [Test]
    public void BotQueryController_PinsOnlyTheBotTokenScheme()
    {
        var attr = typeof(BotQueryController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.ShouldNotBeNull(
            "BotQueryController must have exactly one [Authorize(AuthenticationSchemes = KlacksBotTokenConstants.SchemeName)].");

        attr!.AuthenticationSchemes.ShouldBe(
            KlacksBotTokenConstants.SchemeName,
            "BotQueryController must pin exactly the KlacksBotToken scheme -- any other or additional " +
            "scheme (e.g. JWT) would let non-bot callers reach this read-only bot endpoint.");
    }
}

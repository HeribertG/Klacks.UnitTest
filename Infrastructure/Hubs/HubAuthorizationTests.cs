using Shouldly;
using Klacks.Api.Infrastructure.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Reflection;

namespace Klacks.UnitTest.Infrastructure.Hubs;

[TestFixture]
public class HubAuthorizationTests
{
    public static IEnumerable<Type> AllHubTypes()
    {
        return typeof(WorkNotificationHub).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(Hub).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
    }

    [TestCaseSource(nameof(AllHubTypes))]
    public void Hub_MustHaveExplicitJwtBearerAuthorize(Type hubType)
    {
        var attr = hubType.GetCustomAttribute<AuthorizeAttribute>();

        attr.ShouldNotBeNull(
            $"{hubType.Name} must have [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]. " +
            "Plain [Authorize] without an explicit scheme uses the runtime default, which AddIdentity overrides to cookie auth.");

        attr!.AuthenticationSchemes.ShouldBe(
            JwtBearerDefaults.AuthenticationScheme,
            $"{hubType.Name} must pin to JwtBearerDefaults.AuthenticationScheme — " +
            "a missing or wrong scheme causes 401 for every JWT client.");
    }
}

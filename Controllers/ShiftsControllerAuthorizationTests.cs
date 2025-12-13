using FluentAssertions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.v1.UserBackend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace UnitTest.Controllers;

[TestFixture]
public class ShiftsControllerAuthorizationTests
{
    [Test]
    public void PostBatchCuts_ShouldHaveAuthorizeAttributeWithAdminAndAuthorisedRoles()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("PostBatchCuts", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        authorizeAttribute.Should().NotBeNull("PostBatchCuts should require authorization");
        authorizeAttribute!.Roles.Should().Be($"{Roles.Admin},{Roles.Authorised}",
            "Only Admin and Authorised users should be able to perform batch cuts");
    }

    [Test]
    public void PostResetCuts_ShouldHaveAuthorizeAttributeWithAdminAndAuthorisedRoles()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("PostResetCuts", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        authorizeAttribute.Should().NotBeNull("PostResetCuts should require authorization");
        authorizeAttribute!.Roles.Should().Be($"{Roles.Admin},{Roles.Authorised}",
            "Only Admin and Authorised users should be able to reset cuts");
    }

    [Test]
    public void GetResetDateRange_ShouldNotHaveRoleBasedAuthorization()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("GetResetDateRange", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        if (authorizeAttribute != null)
        {
            authorizeAttribute.Roles.Should().BeNullOrEmpty(
                "GetResetDateRange should only require JWT authentication, not specific roles");
        }
    }

    [Test]
    public void GetCutList_ShouldNotHaveRoleBasedAuthorization()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("GetCutList", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        if (authorizeAttribute != null)
        {
            authorizeAttribute.Roles.Should().BeNullOrEmpty(
                "GetCutList should only require JWT authentication, not specific roles");
        }
    }

    [Test]
    public void GetSimpleList_ShouldNotHaveRoleBasedAuthorization()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("GetSimpleList", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        if (authorizeAttribute != null)
        {
            authorizeAttribute.Roles.Should().BeNullOrEmpty(
                "GetSimpleList should only require JWT authentication, not specific roles");
        }
    }

    [Test]
    public void PostBatchCuts_ShouldHaveHttpPostAttribute()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("PostBatchCuts", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpPostAttribute = methodInfo?.GetCustomAttribute<HttpPostAttribute>();

        // Assert
        httpPostAttribute.Should().NotBeNull();
        httpPostAttribute!.Template.Should().Be("Cuts/Batch");
    }

    [Test]
    public void PostResetCuts_ShouldHaveHttpPostAttribute()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("PostResetCuts", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpPostAttribute = methodInfo?.GetCustomAttribute<HttpPostAttribute>();

        // Assert
        httpPostAttribute.Should().NotBeNull();
        httpPostAttribute!.Template.Should().Be("Cuts/Reset");
    }

    [Test]
    public void GetResetDateRange_ShouldHaveHttpGetAttribute()
    {
        // Arrange
        var methodInfo = typeof(ShiftsController)
            .GetMethod("GetResetDateRange", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpGetAttribute = methodInfo?.GetCustomAttribute<HttpGetAttribute>();

        // Assert
        httpGetAttribute.Should().NotBeNull();
        httpGetAttribute!.Template.Should().Be("Cuts/Reset/DateRange/{originalId}");
    }

    [Test]
    public void ShiftsController_ShouldInheritFromInputBaseController()
    {
        // Arrange & Act
        var baseType = typeof(ShiftsController).BaseType;

        // Assert
        baseType.Should().NotBeNull();
        baseType!.Name.Should().Be("InputBaseController`1",
            "ShiftsController should inherit authorization from InputBaseController");
    }
}

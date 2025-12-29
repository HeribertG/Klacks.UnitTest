using FluentAssertions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace UnitTest.Controllers;

[TestFixture]
public class InputBaseControllerAuthorizationTests
{
    [Test]
    public void Post_ShouldHaveAuthorizeAttributeWithAdminAndAuthorisedRoles()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Post", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        authorizeAttribute.Should().NotBeNull();
        authorizeAttribute!.Roles.Should().Be($"{Roles.Admin},{Roles.Authorised}");
    }

    [Test]
    public void Put_ShouldHaveAuthorizeAttributeWithAdminAndAuthorisedRoles()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Put", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        authorizeAttribute.Should().NotBeNull();
        authorizeAttribute!.Roles.Should().Be($"{Roles.Admin},{Roles.Authorised}");
    }

    [Test]
    public void Delete_ShouldHaveAuthorizeAttributeWithAdminAndAuthorisedRoles()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        authorizeAttribute.Should().NotBeNull();
        authorizeAttribute!.Roles.Should().Be($"{Roles.Admin},{Roles.Authorised}");
    }

    [Test]
    public void Get_ShouldNotHaveRoleBasedAuthorizeAttribute()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Get", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        if (authorizeAttribute != null)
        {
            authorizeAttribute.Roles.Should().BeNullOrEmpty("GET should only require JWT authentication, not specific roles");
        }
    }

    [Test]
    public void Post_ShouldHaveHttpPostAttribute()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Post", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpPostAttribute = methodInfo?.GetCustomAttribute<HttpPostAttribute>();

        // Assert
        httpPostAttribute.Should().NotBeNull();
    }

    [Test]
    public void Put_ShouldHaveHttpPutAttribute()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Put", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpPutAttribute = methodInfo?.GetCustomAttribute<HttpPutAttribute>();

        // Assert
        httpPutAttribute.Should().NotBeNull();
    }

    [Test]
    public void Delete_ShouldHaveHttpDeleteAttribute()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpDeleteAttribute = methodInfo?.GetCustomAttribute<HttpDeleteAttribute>();

        // Assert
        httpDeleteAttribute.Should().NotBeNull();
    }

    [Test]
    public void Get_ShouldHaveHttpGetAttribute()
    {
        // Arrange
        var methodInfo = typeof(InputBaseController<>)
            .GetMethod("Get", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpGetAttribute = methodInfo?.GetCustomAttribute<HttpGetAttribute>();

        // Assert
        httpGetAttribute.Should().NotBeNull();
    }
}

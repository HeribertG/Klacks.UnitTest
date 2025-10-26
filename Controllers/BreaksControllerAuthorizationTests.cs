using FluentAssertions;
using Klacks.Api.Presentation.Controllers.v1.UserBackend;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace UnitTest.Controllers;

[TestFixture]
public class BreaksControllerAuthorizationTests
{
    [Test]
    public void Post_ShouldHaveAuthorizeAttributeWithJwtOnly()
    {
        // Arrange
        var methodInfo = typeof(BreaksController)
            .GetMethod("Post", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttributes = methodInfo?.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToList();

        // Assert
        authorizeAttributes.Should().NotBeNull();
        authorizeAttributes.Should().HaveCount(1, "Post should have exactly one Authorize attribute");

        var authorizeAttribute = authorizeAttributes![0];
        authorizeAttribute.AuthenticationSchemes.Should().Be(JwtBearerDefaults.AuthenticationScheme,
            "Post should use JWT authentication");
        authorizeAttribute.Roles.Should().BeNullOrEmpty(
            "Post should not require specific roles - any authenticated JWT user can create breaks");
    }

    [Test]
    public void Put_ShouldHaveAuthorizeAttributeWithJwtOnly()
    {
        // Arrange
        var methodInfo = typeof(BreaksController)
            .GetMethod("Put", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttributes = methodInfo?.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToList();

        // Assert
        authorizeAttributes.Should().NotBeNull();
        authorizeAttributes.Should().HaveCount(1, "Put should have exactly one Authorize attribute");

        var authorizeAttribute = authorizeAttributes![0];
        authorizeAttribute.AuthenticationSchemes.Should().Be(JwtBearerDefaults.AuthenticationScheme,
            "Put should use JWT authentication");
        authorizeAttribute.Roles.Should().BeNullOrEmpty(
            "Put should not require specific roles - any authenticated JWT user can update breaks");
    }

    [Test]
    public void Delete_ShouldHaveAuthorizeAttributeWithJwtOnly()
    {
        // Arrange
        var methodInfo = typeof(BreaksController)
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttributes = methodInfo?.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToList();

        // Assert
        authorizeAttributes.Should().NotBeNull();
        authorizeAttributes.Should().HaveCount(1, "Delete should have exactly one Authorize attribute");

        var authorizeAttribute = authorizeAttributes![0];
        authorizeAttribute.AuthenticationSchemes.Should().Be(JwtBearerDefaults.AuthenticationScheme,
            "Delete should use JWT authentication");
        authorizeAttribute.Roles.Should().BeNullOrEmpty(
            "Delete should not require specific roles - any authenticated JWT user can delete breaks");
    }

    [Test]
    public void Post_ShouldOverrideBaseControllerMethod()
    {
        // Arrange
        var derivedMethod = typeof(BreaksController)
            .GetMethod("Post", BindingFlags.Public | BindingFlags.Instance);

        var baseMethod = typeof(InputBaseController<>)
            .MakeGenericType(typeof(Klacks.Api.Presentation.DTOs.Schedules.BreakResource))
            .GetMethod("Post", BindingFlags.Public | BindingFlags.Instance);

        // Act & Assert
        derivedMethod.Should().NotBeNull();
        baseMethod.Should().NotBeNull();

        derivedMethod!.DeclaringType.Should().Be(typeof(BreaksController),
            "BreaksController.Post should override InputBaseController.Post");

        derivedMethod.GetBaseDefinition().Should().BeSameAs(baseMethod,
            "BreaksController.Post should properly override the base method");
    }

    [Test]
    public void Put_ShouldOverrideBaseControllerMethod()
    {
        // Arrange
        var derivedMethod = typeof(BreaksController)
            .GetMethod("Put", BindingFlags.Public | BindingFlags.Instance);

        var baseMethod = typeof(InputBaseController<>)
            .MakeGenericType(typeof(Klacks.Api.Presentation.DTOs.Schedules.BreakResource))
            .GetMethod("Put", BindingFlags.Public | BindingFlags.Instance);

        // Act & Assert
        derivedMethod.Should().NotBeNull();
        baseMethod.Should().NotBeNull();

        derivedMethod!.DeclaringType.Should().Be(typeof(BreaksController),
            "BreaksController.Put should override InputBaseController.Put");

        derivedMethod.GetBaseDefinition().Should().BeSameAs(baseMethod,
            "BreaksController.Put should properly override the base method");
    }

    [Test]
    public void Delete_ShouldOverrideBaseControllerMethod()
    {
        // Arrange
        var derivedMethod = typeof(BreaksController)
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        var baseMethod = typeof(InputBaseController<>)
            .MakeGenericType(typeof(Klacks.Api.Presentation.DTOs.Schedules.BreakResource))
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        // Act & Assert
        derivedMethod.Should().NotBeNull();
        baseMethod.Should().NotBeNull();

        derivedMethod!.DeclaringType.Should().Be(typeof(BreaksController),
            "BreaksController.Delete should override InputBaseController.Delete");

        derivedMethod.GetBaseDefinition().Should().BeSameAs(baseMethod,
            "BreaksController.Delete should properly override the base method");
    }

    [Test]
    public void Post_ShouldHaveHttpPostAttribute()
    {
        // Arrange
        var methodInfo = typeof(BreaksController)
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
        var methodInfo = typeof(BreaksController)
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
        var methodInfo = typeof(BreaksController)
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpDeleteAttribute = methodInfo?.GetCustomAttribute<HttpDeleteAttribute>();

        // Assert
        httpDeleteAttribute.Should().NotBeNull();
    }

    [Test]
    public void BreaksController_ShouldInheritFromInputBaseController()
    {
        // Arrange & Act
        var baseType = typeof(BreaksController).BaseType;

        // Assert
        baseType.Should().NotBeNull();
        baseType!.Name.Should().Be("InputBaseController`1",
            "BreaksController should inherit from InputBaseController");
    }

    [Test]
    public void GetClientList_ShouldNotHaveRoleBasedAuthorization()
    {
        // Arrange
        var methodInfo = typeof(BreaksController)
            .GetMethod("GetClientList", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var authorizeAttribute = methodInfo?.GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        if (authorizeAttribute != null)
        {
            authorizeAttribute.Roles.Should().BeNullOrEmpty(
                "GetClientList should only require JWT authentication, not specific roles");
        }
    }
}

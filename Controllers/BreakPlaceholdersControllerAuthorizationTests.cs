using FluentAssertions;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace UnitTest.Controllers;

[TestFixture]
public class BreakPlaceholdersControllerAuthorizationTests
{
    [Test]
    public void Post_ShouldHaveAuthorizeAttributeWithJwtOnly()
    {
        // Arrange
        var methodInfo = typeof(BreakPlaceholdersController)
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
        var methodInfo = typeof(BreakPlaceholdersController)
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
        var methodInfo = typeof(BreakPlaceholdersController)
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
        var derivedMethod = typeof(BreakPlaceholdersController)
            .GetMethod("Post", BindingFlags.Public | BindingFlags.Instance);

        var baseMethod = typeof(InputBaseController<>)
            .MakeGenericType(typeof(Klacks.Api.Presentation.DTOs.Schedules.BreakPlaceholderResource))
            .GetMethod("Post", BindingFlags.Public | BindingFlags.Instance);

        // Act & Assert
        derivedMethod.Should().NotBeNull();
        baseMethod.Should().NotBeNull();

        derivedMethod!.DeclaringType.Should().Be(typeof(BreakPlaceholdersController),
            "BreakPlaceholdersController.Post should override InputBaseController.Post");

        derivedMethod.GetBaseDefinition().Should().BeSameAs(baseMethod,
            "BreakPlaceholdersController.Post should properly override the base method");
    }

    [Test]
    public void Put_ShouldOverrideBaseControllerMethod()
    {
        // Arrange
        var derivedMethod = typeof(BreakPlaceholdersController)
            .GetMethod("Put", BindingFlags.Public | BindingFlags.Instance);

        var baseMethod = typeof(InputBaseController<>)
            .MakeGenericType(typeof(Klacks.Api.Presentation.DTOs.Schedules.BreakPlaceholderResource))
            .GetMethod("Put", BindingFlags.Public | BindingFlags.Instance);

        // Act & Assert
        derivedMethod.Should().NotBeNull();
        baseMethod.Should().NotBeNull();

        derivedMethod!.DeclaringType.Should().Be(typeof(BreakPlaceholdersController),
            "BreakPlaceholdersController.Put should override InputBaseController.Put");

        derivedMethod.GetBaseDefinition().Should().BeSameAs(baseMethod,
            "BreakPlaceholdersController.Put should properly override the base method");
    }

    [Test]
    public void Delete_ShouldOverrideBaseControllerMethod()
    {
        // Arrange
        var derivedMethod = typeof(BreakPlaceholdersController)
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        var baseMethod = typeof(InputBaseController<>)
            .MakeGenericType(typeof(Klacks.Api.Presentation.DTOs.Schedules.BreakPlaceholderResource))
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        // Act & Assert
        derivedMethod.Should().NotBeNull();
        baseMethod.Should().NotBeNull();

        derivedMethod!.DeclaringType.Should().Be(typeof(BreakPlaceholdersController),
            "BreakPlaceholdersController.Delete should override InputBaseController.Delete");

        derivedMethod.GetBaseDefinition().Should().BeSameAs(baseMethod,
            "BreakPlaceholdersController.Delete should properly override the base method");
    }

    [Test]
    public void Post_ShouldHaveHttpPostAttribute()
    {
        // Arrange
        var methodInfo = typeof(BreakPlaceholdersController)
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
        var methodInfo = typeof(BreakPlaceholdersController)
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
        var methodInfo = typeof(BreakPlaceholdersController)
            .GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance);

        // Act
        var httpDeleteAttribute = methodInfo?.GetCustomAttribute<HttpDeleteAttribute>();

        // Assert
        httpDeleteAttribute.Should().NotBeNull();
    }

    [Test]
    public void BreakPlaceholdersController_ShouldInheritFromInputBaseController()
    {
        // Arrange & Act
        var baseType = typeof(BreakPlaceholdersController).BaseType;

        // Assert
        baseType.Should().NotBeNull();
        baseType!.Name.Should().Be("InputBaseController`1",
            "BreakPlaceholdersController should inherit from InputBaseController");
    }

    [Test]
    public void GetClientList_ShouldNotHaveRoleBasedAuthorization()
    {
        // Arrange
        var methodInfo = typeof(BreakPlaceholdersController)
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

using NUnit.Framework;
using NSubstitute;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTest.Base;

public abstract class BaseControllerTests<TController>
    where TController : ControllerBase
{
    protected IMediator MockMediator { get; private set; }
    protected ILogger<TController> MockLogger { get; private set; }
    protected TController Controller { get; private set; }

    [SetUp]
    public virtual void Setup()
    {
        MockMediator = Substitute.For<IMediator>();
        MockLogger = Substitute.For<ILogger<TController>>();
        Controller = CreateController();
        
        SetupControllerContext();
    }

    protected abstract TController CreateController();

    protected virtual void SetupControllerContext()
    {
        var httpContext = new DefaultHttpContext();
        
        var userClaims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "testuser@example.com"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        
        var identity = new ClaimsIdentity(userClaims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        
        httpContext.User = principal;
        
        Controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    protected virtual Guid GetCurrentUserId()
    {
        var userIdClaim = Controller.User.FindFirst(ClaimTypes.NameIdentifier);
        return Guid.Parse(userIdClaim?.Value ?? Guid.NewGuid().ToString());
    }

    [Test]
    public virtual void Controller_ShouldHaveCorrectAttributes()
    {
        // Act
        var controllerType = typeof(TController);
        
        // Assert
        var routeAttribute = Attribute.GetCustomAttribute(controllerType, typeof(RouteAttribute));
        var apiControllerAttribute = Attribute.GetCustomAttribute(controllerType, typeof(ApiControllerAttribute));
        
        Assert.That(routeAttribute, Is.Not.Null, "Controller should have [Route] attribute");
        Assert.That(apiControllerAttribute, Is.Not.Null, "Controller should have [ApiController] attribute");
    }

    protected virtual void AssertOkResult<T>(IActionResult result, T expectedData = default)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        
        var okResult = result as OkObjectResult;
        Assert.That(okResult?.StatusCode, Is.EqualTo(200));
        
        if (expectedData != null)
        {
            Assert.That(okResult?.Value, Is.Not.Null);
        }
    }

    protected virtual void AssertBadRequestResult(IActionResult result, string expectedError = null)
    {
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        
        var badRequestResult = result as BadRequestObjectResult;
        Assert.That(badRequestResult?.StatusCode, Is.EqualTo(400));
        
        if (!string.IsNullOrEmpty(expectedError))
        {
            var errorMessage = badRequestResult?.Value?.ToString();
            Assert.That(errorMessage, Does.Contain(expectedError).IgnoreCase);
        }
    }

    protected virtual void AssertNotFoundResult(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>().Or.InstanceOf<NotFoundResult>());
        
        if (result is NotFoundObjectResult notFoundObject)
        {
            Assert.That(notFoundObject.StatusCode, Is.EqualTo(404));
        }
    }

    protected virtual void AssertUnauthorizedResult(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>().Or.InstanceOf<UnauthorizedResult>());
        
        if (result is UnauthorizedObjectResult unauthorizedObject)
        {
            Assert.That(unauthorizedObject.StatusCode, Is.EqualTo(401));
        }
    }

    [TearDown]
    public virtual void TearDown()
    {
        MockMediator?.ClearSubstitute();
        MockLogger?.ClearSubstitute();
        Controller?.Dispose();
    }
}
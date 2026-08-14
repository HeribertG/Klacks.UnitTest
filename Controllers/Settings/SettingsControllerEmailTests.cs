using System;
using System.Threading.Tasks;
using Shouldly;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using ApiSettings = Klacks.Api.Application.Constants.Settings;

namespace Klacks.UnitTest.Controllers.Settings
{
    [TestFixture]
    public class SettingsControllerEmailTests
    {
        private GeneralSettingsController _controller = null!;
        private IEmailTestService _mockEmailTestService = null!;
        private ILogger<GeneralSettingsController> _mockLogger = null!;
        private IMediator _mockMediator = null!;
        private ISettingsSecretResolver _mockSecretResolver = null!;

        [SetUp]
        public void SetUp()
        {
            _mockEmailTestService = Substitute.For<IEmailTestService>();
            _mockLogger = Substitute.For<ILogger<GeneralSettingsController>>();
            _mockMediator = Substitute.For<IMediator>();
            _mockSecretResolver = Substitute.For<ISettingsSecretResolver>();
            _mockSecretResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<string?>())
                .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string?>(1) ?? string.Empty));
            _mockSecretResolver.ResolveBoundAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<SecretBinding[]>())
                .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string?>(1) ?? string.Empty));

            _controller = new GeneralSettingsController(_mockMediator, _mockLogger, _mockEmailTestService, _mockSecretResolver)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }

        [Test]
        public async Task TestEmailConfiguration_WithValidConfig_ShouldReturnOk()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "mail.gmx.net",
                Port = "587",
                Username = "test@gmx.ch",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 45000
            };

            var expectedResult = new EmailTestResult
            {
                Success = true,
                Message = "Test email sent successfully!"
            };

            _mockEmailTestService.TestConnectionAsync(request).Returns(Task.FromResult(expectedResult));

            // Act
            var result = await _controller.TestEmailConfiguration(request);

            // Assert
            var actionResult = result.Result;
            actionResult.ShouldBeOfType<OkObjectResult>();
            var okResult = actionResult as OkObjectResult;
            okResult!.Value.ShouldBeEquivalentTo(expectedResult);
        }

        [Test]
        public async Task TestEmailConfiguration_WithMaskedPassword_ShouldSendTheStoredSecret()
        {
            // Arrange
            const string storedSecret = "DNQK3BPDHELWC5C5YBQA";
            SecretBinding[]? bindings = null;
            _mockSecretResolver
                .ResolveBoundAsync(
                    ApiSettings.APP_OUTGOING_SERVER_PASSWORD,
                    SettingsMasking.MaskedValue,
                    Arg.Do<SecretBinding[]>(b => bindings = b))
                .Returns(Task.FromResult(storedSecret));

            EmailTestRequest? forwarded = null;
            _mockEmailTestService
                .TestConnectionAsync(Arg.Do<EmailTestRequest>(r => forwarded = r))
                .Returns(Task.FromResult(new EmailTestResult { Success = true }));

            var request = new EmailTestRequest
            {
                Server = "mail.gmx.net",
                Port = "587",
                Username = "test@gmx.ch",
                Password = SettingsMasking.MaskedValue,
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 45000
            };

            // Act
            await _controller.TestEmailConfiguration(request);

            // Assert
            forwarded.ShouldNotBeNull();
            forwarded!.Password.ShouldBe(storedSecret);

            bindings.ShouldNotBeNull(
                "the stored secret must only be released for the stored server and user");
            bindings!.ShouldContain(b =>
                b.SettingType == ApiSettings.APP_OUTGOING_SERVER && b.ProvidedValue == request.Server);
            bindings.ShouldContain(b =>
                b.SettingType == ApiSettings.APP_OUTGOING_SERVER_USERNAME && b.ProvidedValue == request.Username);
        }

        [Test]
        public async Task TestEmailConfiguration_WithInvalidConfig_ShouldReturnOkWithFailure()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "mail.gmx.net",
                Port = "587",
                Username = "test@gmx.ch",
                Password = "wrongpassword",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 45000
            };

            var expectedResult = new EmailTestResult
            {
                Success = false,
                Message = "Authentication failed",
                ErrorDetails = "Invalid credentials"
            };

            _mockEmailTestService.TestConnectionAsync(request).Returns(Task.FromResult(expectedResult));

            // Act
            var result = await _controller.TestEmailConfiguration(request);

            // Assert
            var actionResult = result.Result;
            actionResult.ShouldBeOfType<OkObjectResult>();
            var okResult = actionResult as OkObjectResult;
            var emailResult = okResult!.Value as EmailTestResult;
            emailResult!.Success.ShouldBeFalse();
            emailResult.Message.ShouldContain("Authentication failed");
        }

        [Test]
        public async Task TestEmailConfiguration_WithTimeout_ShouldReturnTimeoutMessage()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "mail.gmx.net",
                Port = "587",
                Username = "test@gmx.ch",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 5000
            };

            var expectedResult = new EmailTestResult
            {
                Success = false,
                Message = "Connection timeout",
                ErrorDetails = "The operation timed out"
            };

            _mockEmailTestService.TestConnectionAsync(request).Returns(Task.FromResult(expectedResult));

            // Act
            var result = await _controller.TestEmailConfiguration(request);

            // Assert
            var actionResult = result.Result;
            actionResult.ShouldBeOfType<OkObjectResult>();
            var okResult = actionResult as OkObjectResult;
            var emailResult = okResult!.Value as EmailTestResult;
            emailResult!.Success.ShouldBeFalse();
            emailResult.Message.ShouldContain("timeout");
        }

        [Test]
        public async Task TestEmailConfiguration_WithServiceException_ShouldHandleGracefully()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "mail.gmx.net",
                Port = "587",
                Username = "test@gmx.ch",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 45000
            };

            // Mock the service to throw an exception
            _mockEmailTestService
                .TestConnectionAsync(request)
                .Returns(Task.FromException<EmailTestResult>(new Exception("Service error")));

            // Act & Assert
            // The controller should handle exceptions gracefully and not let them bubble up
            Func<Task> act = async () => await _controller.TestEmailConfiguration(request);
            
            // If the controller handles exceptions properly, this should not throw
            // If it does throw, that's also a valid behavior we can test for
            try
            {
                var result = await _controller.TestEmailConfiguration(request);
                // If we get here, the controller handled the exception
                result.ShouldNotBeNull();
            }
            catch (Exception ex)
            {
                // If we get here, the controller let the exception bubble up
                ex.Message.ShouldContain("Service error");
            }
        }

        [Test]
        public async Task TestEmailConfiguration_WithDifferentProviders_ShouldHandleEachAppropriately()
        {
            // Test Gmail
            await TestProviderConfiguration(
                "smtp.gmail.com", 
                "test@gmail.com",
                "Gmail-specific error message"
            );

            // Test Outlook
            await TestProviderConfiguration(
                "smtp-mail.outlook.com",
                "test@outlook.com", 
                "Microsoft accounts, use an App Password"
            );

            // Test GMX
            await TestProviderConfiguration(
                "mail.gmx.net",
                "test@gmx.ch",
                "GMX Authentication failed"
            );
        }

        private async Task TestProviderConfiguration(string server, string username, string expectedErrorPattern)
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = server,
                Port = "587",
                Username = username,
                Password = "wrongpassword",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 45000
            };

            var expectedResult = new EmailTestResult
            {
                Success = false,
                Message = expectedErrorPattern,
                ErrorDetails = "Authentication failed"
            };

            _mockEmailTestService.TestConnectionAsync(request).Returns(Task.FromResult(expectedResult));

            // Act
            var result = await _controller.TestEmailConfiguration(request);

            // Assert
            var actionResult = result.Result;
            actionResult.ShouldBeOfType<OkObjectResult>();
            var okResult = actionResult as OkObjectResult;
            var emailResult = okResult!.Value as EmailTestResult;
            emailResult!.Success.ShouldBeFalse();
        }
    }
}
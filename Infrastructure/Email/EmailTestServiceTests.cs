using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Application.DTOs.Settings;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Email
{
    [TestFixture]
    public class EmailTestServiceTests
    {
        private IEmailTestService _emailTestService;
        private ILogger<EmailTestService> _mockLogger;

        [SetUp]
        public void SetUp()
        {
            _mockLogger = Substitute.For<ILogger<EmailTestService>>();
            _emailTestService = new EmailTestService(_mockLogger);
        }

        #region Validation Tests

        [Test]
        public async Task TestConnectionAsync_WithMissingServer_ShouldReturnFailure()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "",
                Port = "587",
                Username = "test@example.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Missing required fields");
        }

        [Test]
        public async Task TestConnectionAsync_WithMissingPort_ShouldReturnFailure()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "",
                Username = "test@example.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Missing required fields");
        }

        [Test]
        public async Task TestConnectionAsync_WithInvalidPort_ShouldReturnFailure()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "not-a-number",
                Username = "test@example.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Invalid port number");
        }

        [Test]
        public async Task TestConnectionAsync_WithMissingUsername_ShouldReturnFailure()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "587",
                Username = "",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Missing required fields");
        }

        [Test]
        public async Task TestConnectionAsync_WithMissingPassword_ShouldReturnFailure()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "587",
                Username = "test@example.com",
                Password = "",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Missing required fields");
        }

        #endregion

        #region Authentication Type Tests

        [Test]
        public async Task TestConnectionAsync_WithGmailAndNoAuth_ShouldReturnAuthRequired()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "587",
                Username = "test@gmail.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "<None>",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Authentication required");
            result.Message.Should().Contain("LOGIN");
        }

        [Test]
        public async Task TestConnectionAsync_WithGmxAndNoAuth_ShouldReturnAuthRequired()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "mail.gmx.net",
                Port = "587",
                Username = "test@gmx.ch",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "<None>",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Authentication required");
        }

        [Test]
        public async Task TestConnectionAsync_WithOutlookAndNoAuth_ShouldReturnAuthRequired()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp-mail.outlook.com",
                Port = "587",
                Username = "test@outlook.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "<None>",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Authentication required");
        }

        [Test]
        public async Task TestConnectionAsync_WithHotmailAndNoAuth_ShouldReturnAuthRequired()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.hotmail.com",
                Port = "587",
                Username = "test@hotmail.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "<None>",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Authentication required");
        }

        [Test]
        public async Task TestConnectionAsync_WithYahooAndNoAuth_ShouldReturnAuthRequired()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.mail.yahoo.com",
                Port = "587",
                Username = "test@yahoo.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "<None>",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Authentication required");
        }

        [Test]
        public async Task TestConnectionAsync_WithLoginAuthAndEmptyPassword_ShouldReturnMissingCredentials()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "587",
                Username = "test@gmail.com",
                Password = "",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Missing required fields");
        }

        #endregion

        #region Port Configuration Tests

        [Test]
        public async Task TestConnectionAsync_WithPort465_ShouldEnableSSL()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "465",
                Username = "test@gmail.com",
                Password = "password",
                EnableSSL = false,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString().Contains("SecureSocketOptions=SslOnConnect")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception, string>>()
            );
        }

        [Test]
        public async Task TestConnectionAsync_WithPort587_ShouldUseStartTLS()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "587",
                Username = "test@gmail.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString().Contains("SecureSocketOptions=StartTls")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception, string>>()
            );
        }

        #endregion

        #region Edge Case Tests

        [Test]
        public async Task TestConnectionAsync_WithWhitespaceInPort_ShouldTrimAndWork()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = " 587 ", // Port with whitespace
                Username = "test@gmail.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 10000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            // Should parse the port successfully after trimming
            result.Message.Should().NotContain("Invalid port number");
        }

        [Test]
        public async Task TestConnectionAsync_WithVeryLowTimeout_ShouldStillAttemptConnection()
        {
            // Arrange
            var request = new EmailTestRequest
            {
                Server = "smtp.gmail.com",
                Port = "587",
                Username = "test@gmail.com",
                Password = "password",
                EnableSSL = true,
                AuthenticationType = "LOGIN",
                Timeout = 1000
            };

            // Act
            var result = await _emailTestService.TestConnectionAsync(request);

            // Assert
            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString().Contains("Connecting to SMTP server")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception, string>>()
            );
        }

        #endregion
    }
}
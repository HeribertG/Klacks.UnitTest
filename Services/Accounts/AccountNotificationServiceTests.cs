using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Services.Accounts;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AccountNotificationServiceTests
{
    private AccountNotificationService _notificationService;
    private IEmailService _mockEmailService;
    private ILogger<AccountNotificationService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        // Arrange
        _mockEmailService = Substitute.For<IEmailService>();
        _mockLogger = Substitute.For<ILogger<AccountNotificationService>>();

        _notificationService = new AccountNotificationService(_mockEmailService, _mockLogger);
    }

    [Test]
    public async Task SendEmailAsync_WithValidParameters_ShouldReturnSuccessMessage()
    {
        // Arrange
        _mockEmailService.CanSendEmailAsync().Returns(true);
        _mockEmailService.SendMail("test@example.com", "Test Email", "This is a test message").Returns("OK");

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        result.ShouldBe("OK");
    }

    [Test]
    public async Task SendEmailAsync_WhenEmailServiceNotAvailable_ShouldReturnNotAvailableMessage()
    {
        // Arrange
        _mockEmailService.CanSendEmailAsync().Returns(false);

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        result.ShouldBe("Email configuration not available");
    }

    [Test]
    public async Task SendEmailAsync_WithNullEmail_ShouldThrowException()
    {
        // Arrange
        string? email = null;

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await _notificationService.SendEmailAsync("Test Email", email!, "This is a test message"));
    }

    [Test]
    public async Task SendEmailAsync_WhenSendMailThrows_ShouldReturnFailureMessage()
    {
        // Arrange
        _mockEmailService.CanSendEmailAsync().Returns(true);
        _mockEmailService.SendMail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new InvalidOperationException("SMTP failure"));

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        result.ShouldContain("Email sending failed");
    }

    [Test]
    public async Task SendEmailAsync_ShouldLogInformationOnStart()
    {
        // Arrange
        _mockEmailService.CanSendEmailAsync().Returns(true);
        _mockEmailService.SendMail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns("OK");

        // Act
        await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        _mockLogger.Received(1).Log(
            Microsoft.Extensions.Logging.LogLevel.Information,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("Attempting to send email to")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task SendEmailAsync_ShouldLogInformationOnSuccess()
    {
        // Arrange
        _mockEmailService.CanSendEmailAsync().Returns(true);
        _mockEmailService.SendMail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns("OK");

        // Act
        await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        _mockLogger.Received(1).Log(
            Microsoft.Extensions.Logging.LogLevel.Information,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("Email sent successfully")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}

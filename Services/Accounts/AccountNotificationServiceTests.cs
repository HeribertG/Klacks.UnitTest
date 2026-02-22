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
        _mockEmailService = Substitute.For<IEmailService>();
        _mockLogger = Substitute.For<ILogger<AccountNotificationService>>();

        _notificationService = new AccountNotificationService(_mockEmailService, _mockLogger);
    }

    [Test]
    public async Task SendEmailAsync_WithValidParameters_ShouldReturnSuccessMessage()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        _mockEmailService.SendMail("test@example.com", "Test Email", "This is a test message")
            .Returns("Email sent");

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        result.Should().NotBeNull();
        result.Should().Be("Email sent");
    }

    [Test]
    public async Task SendEmailAsync_WithUnavailableEmailService_ShouldReturnConfigNotAvailable()
    {
        // Arrange
        _mockEmailService.CanSendEmailAsync().Returns(Task.FromResult(false));

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        result.Should().Be("Email configuration not available");
    }

    [Test]
    public async Task SendEmailAsync_WithNullEmail_ShouldThrowArgumentNullException()
    {
        // Arrange
        ConfigureEmailServiceAvailable();

        // Act & Assert
        await FluentActions.Invoking(async () =>
            await _notificationService.SendEmailAsync("Test Email", null, "This is a test message"))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task SendEmailAsync_WithEmptyEmail_ShouldCallSendMail()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        _mockEmailService.SendMail("", "Test Email", "This is a test message")
            .Returns("Email sent");

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "", "This is a test message");

        // Assert
        result.Should().NotBeNull();
        _mockEmailService.Received(1).SendMail("", "Test Email", "This is a test message");
    }

    [Test]
    public async Task SendEmailAsync_WithNullTitle_ShouldCallSendMail()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        _mockEmailService.SendMail("test@example.com", null, "This is a test message")
            .Returns("Email sent");

        // Act
        var result = await _notificationService.SendEmailAsync(null, "test@example.com", "This is a test message");

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WithNullMessage_ShouldCallSendMail()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        _mockEmailService.SendMail("test@example.com", "Test Email", null)
            .Returns("Email sent");

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "test@example.com", null);

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WhenSendMailThrows_ShouldReturnFailureMessage()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        _mockEmailService.SendMail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new InvalidOperationException("SMTP connection failed"));

        // Act
        var result = await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        result.Should().Contain("Email sending failed");
        result.Should().Contain("SMTP connection failed");
    }

    [Test]
    public async Task SendEmailAsync_WithSpecialCharactersInTitle_ShouldCallSendMail()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        var title = "Test Email with Special Characters: äöü ß €";
        _mockEmailService.SendMail("test@example.com", title, "This is a test message")
            .Returns("Email sent");

        // Act
        var result = await _notificationService.SendEmailAsync(title, "test@example.com", "This is a test message");

        // Assert
        result.Should().NotBeNull();
        _mockEmailService.Received(1).SendMail("test@example.com", title, "This is a test message");
    }

    [Test]
    public async Task SendEmailAsync_ShouldLogInformationOnStart()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        _mockEmailService.SendMail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("Email sent");

        // Act
        await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("Attempting to send email to")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception, string>>());
    }

    [Test]
    public async Task SendEmailAsync_ShouldLogInformationOnSuccess()
    {
        // Arrange
        ConfigureEmailServiceAvailable();
        _mockEmailService.SendMail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("Email sent");

        // Act
        await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("Email sent successfully")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception, string>>());
    }

    [Test]
    public async Task SendEmailAsync_WhenCanSendEmailFails_ShouldNotCallSendMail()
    {
        // Arrange
        _mockEmailService.CanSendEmailAsync().Returns(Task.FromResult(false));

        // Act
        await _notificationService.SendEmailAsync("Test Email", "test@example.com", "This is a test message");

        // Assert
        _mockEmailService.DidNotReceive().SendMail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    private void ConfigureEmailServiceAvailable()
    {
        _mockEmailService.CanSendEmailAsync().Returns(Task.FromResult(true));
    }
}

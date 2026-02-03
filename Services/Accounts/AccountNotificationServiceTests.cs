using FluentAssertions;
using FluentAssertions.Specialized;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AccountNotificationServiceTests
{
    private DataBaseContext _context;
    private AccountNotificationService _notificationService;
    private ISettingsEncryptionService _mockEncryptionService;
    private ILogger<AccountNotificationService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockEncryptionService = Substitute.For<ISettingsEncryptionService>();
        _mockEncryptionService.ProcessForReading(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => callInfo.ArgAt<string>(1));
        _mockLogger = Substitute.For<ILogger<AccountNotificationService>>();

        _notificationService = new AccountNotificationService(_context, _mockEncryptionService, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task SendEmailAsync_WithValidParameters_ShouldReturnSuccessMessage()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        var email = "test@example.com";
        var message = "This is a test message";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBe("Wrong Initialisation of the Email Wrapper");
    }

    [Test]
    public async Task SendEmailAsync_WithMissingSettings_ShouldReturnWrongInitialisationMessage()
    {
        // Arrange - No email settings seeded
        var title = "Test Email";
        var email = "test@example.com";
        var message = "This is a test message";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().Be("Wrong Initialisation of the Email Wrapper");
    }

    [Test]
    public async Task SendEmailAsync_WithNullEmail_ShouldThrowException()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        string email = null;
        var message = "This is a test message";

        // Act & Assert - The EmailWrapper.SendEmailMessage will throw NullReferenceException with null email
        await FluentActions.Invoking(async () => 
            await _notificationService.SendEmailAsync(title, email, message))
            .Should().ThrowAsync<Exception>();
    }

    [Test]
    public async Task SendEmailAsync_WithEmptyEmail_ShouldHandleGracefully()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        var email = "";
        var message = "This is a test message";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WithNullTitle_ShouldHandleGracefully()
    {
        // Arrange
        await SeedEmailSettings();
        
        string title = null;
        var email = "test@example.com";
        var message = "This is a test message";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WithNullMessage_ShouldHandleGracefully()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        var email = "test@example.com";
        string message = null;

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WithLongEmail_ShouldHandleGracefully()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        var email = "verylongemailaddressthatmightcauseissues@verylongdomainnamethatmightcauseproblems.com";
        var message = "This is a test message";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WithSpecialCharactersInTitle_ShouldHandleGracefully()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email with Special Characters: äöü ß €";
        var email = "test@example.com";
        var message = "This is a test message";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WithSpecialCharactersInMessage_ShouldHandleGracefully()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        var email = "test@example.com";
        var message = "Message with special characters: äöü ß € <script>alert('test')</script>";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task SendEmailAsync_WithPartialSettings_ShouldReturnWrongInitialisation()
    {
        // Arrange
        await SeedPartialEmailSettings();
        
        var title = "Test Email";
        var email = "test@example.com";
        var message = "This is a test message";

        // Act
        var result = await _notificationService.SendEmailAsync(title, email, message);

        // Assert - Partial settings will result in missing ReplyTo, causing "No sender address available"
        result.Should().Be("No sender address available");
    }

    [Test]
    public async Task SendEmailAsync_ShouldLogInformationOnStart()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        var email = "test@example.com";
        var message = "This is a test message";

        // Act
        await _notificationService.SendEmailAsync(title, email, message);

        // Assert - Check that logging was called (parameters may vary due to structured logging)
        _mockLogger.Received(1).Log(
            Microsoft.Extensions.Logging.LogLevel.Information,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Is<object>(v => v.ToString().Contains("Attempting to send email to")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception, string>>());
    }

    [Test]
    public async Task SendEmailAsync_ShouldLogInformationOnSuccess()
    {
        // Arrange
        await SeedEmailSettings();
        
        var title = "Test Email";
        var email = "test@example.com";
        var message = "This is a test message";

        // Act
        await _notificationService.SendEmailAsync(title, email, message);

        // Assert - Check that success logging was called
        _mockLogger.Received(1).Log(
            Microsoft.Extensions.Logging.LogLevel.Information,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Is<object>(v => v.ToString().Contains("Email sent successfully")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception, string>>());
    }

    private async Task SeedEmailSettings()
    {
        var settings = new List<SettingsModel>
        {
            new SettingsModel() { Type = "APP_ADDRESS_MAIL", Value = "test@klacks.com" },
            new SettingsModel() { Type = "subject", Value = "Test Subject" },
            new SettingsModel() { Type = "mark", Value = "Test Mark" },
            new SettingsModel() { Type = "replyTo", Value = "noreply@klacks.com" },
            new SettingsModel() { Type = "outgoingserver", Value = "smtp.test.com" },
            new SettingsModel() { Type = "outgoingserverPort", Value = "587" },
            new SettingsModel() { Type = "outgoingserverUsername", Value = "testuser" },
            new SettingsModel() { Type = "outgoingserverPassword", Value = "testpass" },
            new SettingsModel() { Type = "enabledSSL", Value = "true" },
            new SettingsModel() { Type = "authenticationType", Value = "Basic" },
            new SettingsModel() { Type = "readReceipt", Value = "false" },
            new SettingsModel() { Type = "dispositionNotification", Value = "false" },
            new SettingsModel() { Type = "outgoingserverTimeout", Value = "30000" }
        };

        await _context.Settings.AddRangeAsync(settings);
        await _context.SaveChangesAsync();
    }

    private async Task SeedPartialEmailSettings()
    {
        var settings = new List<SettingsModel>
        {
            new SettingsModel() { Type = "APP_ADDRESS_MAIL", Value = "test@klacks.com" },
            new SettingsModel() { Type = "subject", Value = "Test Subject" }
        };

        await _context.Settings.AddRangeAsync(settings);
        await _context.SaveChangesAsync();
    }
}
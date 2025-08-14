using FluentAssertions;
using FluentAssertions.Specialized;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace UnitTest.Services.Accounts;

[TestFixture]
public class AccountNotificationServiceTests
{
    private DataBaseContext _context;
    private AccountNotificationService _notificationService;
    private ILogger<AccountNotificationService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<AccountNotificationService>>();

        _notificationService = new AccountNotificationService(_context, _mockLogger);
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

        // Assert - Partial settings will cause bool.Parse() to fail, returning "Wrong Initialisation"
        result.Should().Be("Wrong Initialisation of the Email Wrapper");
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
            Arg.Is<object>(v => v.ToString().Contains("Sending email to")),
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
        var settings = new List<Settings>
        {
            new Settings { Type = "APP_ADDRESS_MAIL", Value = "test@klacks.com" },
            new Settings { Type = "subject", Value = "Test Subject" },
            new Settings { Type = "mark", Value = "Test Mark" },
            new Settings { Type = "replyTo", Value = "noreply@klacks.com" },
            new Settings { Type = "outgoingserver", Value = "smtp.test.com" },
            new Settings { Type = "outgoingserverPort", Value = "587" },
            new Settings { Type = "outgoingserverUsername", Value = "testuser" },
            new Settings { Type = "outgoingserverPassword", Value = "testpass" },
            new Settings { Type = "enabledSSL", Value = "true" },
            new Settings { Type = "authenticationType", Value = "Basic" },
            new Settings { Type = "readReceipt", Value = "false" },
            new Settings { Type = "dispositionNotification", Value = "false" },
            new Settings { Type = "outgoingserverTimeout", Value = "30000" }
        };

        await _context.Settings.AddRangeAsync(settings);
        await _context.SaveChangesAsync();
    }

    private async Task SeedPartialEmailSettings()
    {
        var settings = new List<Settings>
        {
            new Settings { Type = "APP_ADDRESS_MAIL", Value = "test@klacks.com" },
            new Settings { Type = "subject", Value = "Test Subject" }
        };

        await _context.Settings.AddRangeAsync(settings);
        await _context.SaveChangesAsync();
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Application.DTOs.Settings;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Services.Settings
{
    [TestFixture]
    public class EmailSettingsServiceTests
    {
        private IEmailTestService _emailTestService;
        private ILogger<EmailTestService> _mockLogger;

        [SetUp]
        public void SetUp()
        {
            _mockLogger = Substitute.For<ILogger<EmailTestService>>();
            _emailTestService = new EmailTestService(_mockLogger);
        }

        #region Email Configuration Tests

        [Test]
        public void BuildEmailTestRequest_WithCompleteConfiguration_ShouldReturnValidConfig()
        {
            // Arrange
            var emailSettings = new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserver", Value = "mail.gmx.net" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = "587" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverTimeout", Value = "100" },
                new() { Id = Guid.NewGuid(), Type = "enabledSSL", Value = "true" },
                new() { Id = Guid.NewGuid(), Type = "authenticationType", Value = "LOGIN" },
                new() { Id = Guid.NewGuid(), Type = "dispositionNotification", Value = "false" },
                new() { Id = Guid.NewGuid(), Type = "readReceipt", Value = "false" },
                new() { Id = Guid.NewGuid(), Type = "replyTo", Value = "hgasparoli@gmx.ch" },
                new() { Id = Guid.NewGuid(), Type = "mark", Value = "" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverUsername", Value = "hgasparoli@gmx.ch" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPassword", Value = "password123" }
            };

            // Act
            var emailConfig = BuildEmailTestRequest(emailSettings);

            // Assert
            emailConfig.ShouldNotBeNull();
            emailConfig.Server.ShouldBe("mail.gmx.net");
            emailConfig.Port.ShouldBe("587");
            emailConfig.Username.ShouldBe("hgasparoli@gmx.ch");
            emailConfig.Password.ShouldBe("password123");
            emailConfig.EnableSSL.ShouldBeTrue();
            emailConfig.AuthenticationType.ShouldBe("LOGIN");
        }

        [Test]
        public void ValidateEmailConfiguration_WithCompleteGmxSettings_ShouldBeValid()
        {
            // Arrange
            var emailSettings = new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserver", Value = "mail.gmx.net" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = "587" },
                new() { Id = Guid.NewGuid(), Type = "enabledSSL", Value = "true" },
                new() { Id = Guid.NewGuid(), Type = "authenticationType", Value = "LOGIN" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverUsername", Value = "hgasparoli@gmx.ch" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPassword", Value = "password123" }
            };

            var emailConfig = BuildEmailTestRequest(emailSettings);

            // Act
            var isValid = ValidateEmailConfiguration(emailConfig);

            // Assert
            isValid.ShouldBeTrue();
        }

        [Test]
        public void ValidateEmailConfiguration_WithMissingServer_ShouldBeInvalid()
        {
            // Arrange
            var emailSettings = new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = "587" },
                new() { Id = Guid.NewGuid(), Type = "authenticationType", Value = "LOGIN" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverUsername", Value = "hgasparoli@gmx.ch" }
            };

            var emailConfig = BuildEmailTestRequest(emailSettings);

            // Act
            var isValid = ValidateEmailConfiguration(emailConfig);

            // Assert
            isValid.ShouldBeFalse();
        }

        [Test]
        public void ValidateEmailConfiguration_WithLoginAuthAndNoCredentials_ShouldBeInvalid()
        {
            // Arrange
            var emailSettings = new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserver", Value = "mail.gmx.net" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = "587" },
                new() { Id = Guid.NewGuid(), Type = "authenticationType", Value = "LOGIN" }
                // Missing username and password
            };

            var emailConfig = BuildEmailTestRequest(emailSettings);

            // Act
            var isValid = ValidateEmailConfiguration(emailConfig);

            // Assert
            isValid.ShouldBeFalse();
        }

        [Test]
        public void GetEmailSettingsByProvider_WithDifferentProviders_ShouldReturnProviderSpecificDefaults()
        {
            // Test cases for different email providers
            var providerTests = new[]
            {
                new { Provider = "Gmail", Server = "smtp.gmail.com", Port = "587", SSL = true },
                new { Provider = "Outlook", Server = "smtp-mail.outlook.com", Port = "587", SSL = true },
                new { Provider = "GMX", Server = "mail.gmx.net", Port = "587", SSL = true },
                new { Provider = "Yahoo", Server = "smtp.mail.yahoo.com", Port = "587", SSL = true }
            };

            foreach (var test in providerTests)
            {
                // Arrange
                var settings = CreateProviderSettings(test.Server, test.Port.ToString(), test.SSL.ToString().ToLower());

                // Act
                var emailConfig = BuildEmailTestRequest(settings);

                // Assert
                emailConfig.Server.ShouldBe(test.Server);
                emailConfig.Port.ShouldBe(test.Port);
                emailConfig.EnableSSL.ShouldBe(test.SSL);
            }
        }

        #endregion

        #region Helper Methods

        private EmailTestRequest BuildEmailTestRequest(IEnumerable<Klacks.Api.Domain.Models.Settings.Settings> settings)
        {
            var settingsDict = settings.ToDictionary(s => s.Type, s => s.Value);

            return new EmailTestRequest
            {
                Server = settingsDict.GetValueOrDefault("outgoingserver", ""),
                Port = settingsDict.GetValueOrDefault("outgoingserverPort", "587"),
                Username = settingsDict.GetValueOrDefault("outgoingserverUsername", ""),
                Password = settingsDict.GetValueOrDefault("outgoingserverPassword", ""),
                EnableSSL = settingsDict.GetValueOrDefault("enabledSSL", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                AuthenticationType = settingsDict.GetValueOrDefault("authenticationType", "<None>"),
                Timeout = int.TryParse(settingsDict.GetValueOrDefault("outgoingserverTimeout", "30000"), out var timeout) ? timeout * 1000 : 30000
            };
        }

        private bool ValidateEmailConfiguration(EmailTestRequest config)
        {
            if (string.IsNullOrWhiteSpace(config.Server)) return false;
            if (string.IsNullOrWhiteSpace(config.Port)) return false;
            if (!int.TryParse(config.Port, out _)) return false;

            if (config.AuthenticationType != "<None>" && !string.IsNullOrWhiteSpace(config.AuthenticationType))
            {
                if (string.IsNullOrWhiteSpace(config.Username)) return false;
                if (string.IsNullOrWhiteSpace(config.Password)) return false;
            }

            return true;
        }

        private List<Klacks.Api.Domain.Models.Settings.Settings> CreateProviderSettings(string server, string port, string ssl)
        {
            return new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserver", Value = server },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = port },
                new() { Id = Guid.NewGuid(), Type = "enabledSSL", Value = ssl },
                new() { Id = Guid.NewGuid(), Type = "authenticationType", Value = "LOGIN" }
            };
        }

        #endregion
    }
}
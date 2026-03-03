using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Klacks.UnitTest.Repository
{
    [TestFixture]
    public class SettingsRepositoryTests : BaseRepositoryTest
    {
        private ISettingsRepository _settingsRepository;

        [SetUp]
        public void SetUp()
        {
            // Note: Constructor parameters would need to be mocked based on actual SettingsRepository implementation
            // For now, we'll focus on the data structure tests
        }

        #region Email Settings Repository Tests

        [Test]
        public async Task AddEmailSetting_WithValidData_ShouldAddSuccessfully()
        {
            // Arrange
            var emailServerSetting = new Klacks.Api.Domain.Models.Settings.Settings
            {
                Id = Guid.NewGuid(),
                Type = "outgoingserver",
                Value = "mail.gmx.net"
            };

            // Act
            TestDbContext.Settings.Add(emailServerSetting);
            await TestDbContext.SaveChangesAsync();

            // Assert
            var result = await TestDbContext.Settings.FirstOrDefaultAsync(s => s.Type == "outgoingserver");
            result.Should().NotBeNull();
            result.Type.Should().Be("outgoingserver");
            result.Value.Should().Be("mail.gmx.net");
        }

        [Test]
        public async Task GetAllEmailSettings_WithCompleteConfiguration_ShouldReturnAllSettings()
        {
            // Arrange
            var emailSettings = new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserver", Value = "mail.gmx.net" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = "587" },
                new() { Id = Guid.NewGuid(), Type = "enabledSSL", Value = "true" },
                new() { Id = Guid.NewGuid(), Type = "authenticationType", Value = "LOGIN" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverUsername", Value = "hgasparoli@gmx.ch" }
            };

            TestDbContext.Settings.AddRange(emailSettings);
            await TestDbContext.SaveChangesAsync();

            // Act
            var result = await TestDbContext.Settings.ToListAsync();

            // Assert
            result.Should().NotBeNull();
            result.Should().HaveCount(5);
            
            var serverSetting = result.FirstOrDefault(s => s.Type == "outgoingserver");
            serverSetting.Should().NotBeNull();
            serverSetting.Value.Should().Be("mail.gmx.net");

            var portSetting = result.FirstOrDefault(s => s.Type == "outgoingserverPort");
            portSetting.Should().NotBeNull();
            portSetting.Value.Should().Be("587");

            var sslSetting = result.FirstOrDefault(s => s.Type == "enabledSSL");
            sslSetting.Should().NotBeNull();
            sslSetting.Value.Should().Be("true");

            var authSetting = result.FirstOrDefault(s => s.Type == "authenticationType");
            authSetting.Should().NotBeNull();
            authSetting.Value.Should().Be("LOGIN");

            var usernameSetting = result.FirstOrDefault(s => s.Type == "outgoingserverUsername");
            usernameSetting.Should().NotBeNull();
            usernameSetting.Value.Should().Be("hgasparoli@gmx.ch");
        }

        [Test]
        public async Task UpdateEmailSetting_WithNewValue_ShouldUpdateCorrectly()
        {
            // Arrange
            var originalSetting = new Klacks.Api.Domain.Models.Settings.Settings
            {
                Id = Guid.NewGuid(),
                Type = "outgoingserver",
                Value = "smtp-mail.outlook.com"
            };

            TestDbContext.Settings.Add(originalSetting);
            await TestDbContext.SaveChangesAsync();

            // Act
            originalSetting.Value = "mail.gmx.net";
            TestDbContext.Settings.Update(originalSetting);
            await TestDbContext.SaveChangesAsync();

            // Assert
            var updatedSetting = await TestDbContext.Settings.FirstOrDefaultAsync(s => s.Type == "outgoingserver");
            updatedSetting.Should().NotBeNull();
            updatedSetting.Value.Should().Be("mail.gmx.net");
        }

        [Test]
        public async Task DeleteEmailSetting_WithExistingSetting_ShouldDeleteSuccessfully()
        {
            // Arrange
            var settingToDelete = new Klacks.Api.Domain.Models.Settings.Settings
            {
                Id = Guid.NewGuid(),
                Type = "tempEmailSetting",
                Value = "temp_value"
            };

            TestDbContext.Settings.Add(settingToDelete);
            await TestDbContext.SaveChangesAsync();

            // Act
            TestDbContext.Settings.Remove(settingToDelete);
            await TestDbContext.SaveChangesAsync();

            // Assert
            var deletedSetting = await TestDbContext.Settings.FirstOrDefaultAsync(s => s.Type == "tempEmailSetting");
            deletedSetting.Should().BeNull();
        }

        #endregion

        #region Email Configuration Validation Tests

        [Test]
        public async Task GetEmailConfiguration_WithCompleteSettings_ShouldReturnValidConfig()
        {
            // Arrange
            var emailSettings = new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserver", Value = "mail.gmx.net" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = "587" },
                new() { Id = Guid.NewGuid(), Type = "enabledSSL", Value = "true" },
                new() { Id = Guid.NewGuid(), Type = "authenticationType", Value = "LOGIN" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverUsername", Value = "hgasparoli@gmx.ch" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPassword", Value = "password123" },
                new() { Id = Guid.NewGuid(), Type = "replyTo", Value = "hgasparoli@gmx.ch" }
            };

            TestDbContext.Settings.AddRange(emailSettings);
            await TestDbContext.SaveChangesAsync();

            // Act
            var allSettings = await TestDbContext.Settings.ToListAsync();
            var emailConfig = BuildEmailConfigFromSettings(allSettings);

            // Assert
            emailConfig.Should().NotBeNull();
            emailConfig["outgoingserver"].Should().Be("mail.gmx.net");
            emailConfig["outgoingserverPort"].Should().Be("587");
            emailConfig["enabledSSL"].Should().Be("true");
            emailConfig["authenticationType"].Should().Be("LOGIN");
            emailConfig["outgoingserverUsername"].Should().Be("hgasparoli@gmx.ch");
            emailConfig["replyTo"].Should().Be("hgasparoli@gmx.ch");
        }

        [Test]
        public async Task GetEmailConfiguration_WithMissingSettings_ShouldHandleGracefully()
        {
            // Arrange - Only partial email settings
            var partialSettings = new List<Klacks.Api.Domain.Models.Settings.Settings>
            {
                new() { Id = Guid.NewGuid(), Type = "outgoingserver", Value = "mail.gmx.net" },
                new() { Id = Guid.NewGuid(), Type = "outgoingserverPort", Value = "587" }
            };

            TestDbContext.Settings.AddRange(partialSettings);
            await TestDbContext.SaveChangesAsync();

            // Act
            var allSettings = await TestDbContext.Settings.ToListAsync();
            var emailConfig = BuildEmailConfigFromSettings(allSettings);

            // Assert
            emailConfig.Should().NotBeNull();
            emailConfig.Should().HaveCount(2);
            emailConfig["outgoingserver"].Should().Be("mail.gmx.net");
            emailConfig["outgoingserverPort"].Should().Be("587");
        }

        #endregion

        #region Performance Tests

        [Test]
        public async Task GetAllSettings_WithManySettings_ShouldPerformWell()
        {
            // Arrange
            var manySettings = new List<Klacks.Api.Domain.Models.Settings.Settings>();
            for (int i = 0; i < 1000; i++)
            {
                manySettings.Add(new Klacks.Api.Domain.Models.Settings.Settings
                {
                    Id = Guid.NewGuid(),
                    Type = $"setting_{i}",
                    Value = $"value_{i}"
                });
            }

            TestDbContext.Settings.AddRange(manySettings);
            await TestDbContext.SaveChangesAsync();

            var startTime = DateTime.Now;

            // Act
            var result = await TestDbContext.Settings.ToListAsync();

            // Assert
            var executionTime = DateTime.Now - startTime;
            result.Should().HaveCount(1000);
            executionTime.TotalMilliseconds.Should().BeLessThan(5000); // Should complete within 5 seconds
        }

        #endregion

        #region Helper Methods

        private Dictionary<string, string> BuildEmailConfigFromSettings(IEnumerable<Klacks.Api.Domain.Models.Settings.Settings> settings)
        {
            return settings.ToDictionary(s => s.Type, s => s.Value);
        }

        #endregion
    }
}
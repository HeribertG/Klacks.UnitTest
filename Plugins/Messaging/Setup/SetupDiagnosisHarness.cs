// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Runs the real MessagingSetupDiagnosticsService for exactly one provider backed by a real provider adapter,
/// with every repository and reader stubbed to "nothing observed yet". Used by the per-provider leak tests,
/// which must assert on the redacted report rather than on the adapter's raw diagnosis.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services.Setup;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Domain.Models.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

public static class SetupDiagnosisHarness
{
    public static async Task<ProviderSetupReport> DiagnoseSingleAsync(IMessagingProviderAdapter adapter, string configJson)
    {
        var provider = new MessagingProvider
        {
            Id = Guid.NewGuid(),
            Name = $"{adapter.ProviderType.ToLowerInvariant()}-main",
            DisplayName = adapter.ProviderType,
            ProviderType = adapter.ProviderType,
            IsEnabled = true,
            ConfigJson = configJson,
        };

        var providerRepository = Substitute.For<IMessagingProviderRepository>();
        providerRepository.GetAllAsync().Returns(new[] { provider });

        var adapterFactory = Substitute.For<IMessagingProviderAdapterFactory>();
        adapterFactory.Create(adapter.ProviderType).Returns(adapter);

        var tracker = Substitute.For<IMessagingInboundActivityTracker>();
        tracker.GetSnapshot(Arg.Any<Guid>()).Returns(new InboundActivitySnapshot(null, null, []));

        var messageRepository = Substitute.For<IMessageRepository>();
        messageRepository
            .GetMessagesAsync(Arg.Any<Guid?>(), Arg.Any<MessageDirection?>(), Arg.Any<string?>(), Arg.Any<MessageScope?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<Message>());

        var ownerReader = Substitute.For<IOwnerMessengerReader>();
        ownerReader.GetByTypeAsync(Arg.Any<MessengerType>(), Arg.Any<CancellationToken>()).Returns((OwnerMessengerEntry?)null);

        var employeeReader = Substitute.For<IEmployeeClientReader>();
        employeeReader.GetAllEmployeesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<EmployeeClientInfo>());

        var service = new MessagingSetupDiagnosticsService(
            providerRepository, adapterFactory, tracker, messageRepository, Substitute.For<IMessengerContactRepository>(),
            ownerReader, employeeReader, NullLogger<MessagingSetupDiagnosticsService>.Instance, () => DateTime.UtcNow);

        var report = await service.DiagnoseAsync();
        return report.Providers.Single();
    }
}

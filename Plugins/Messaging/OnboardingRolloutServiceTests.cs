// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class OnboardingRolloutServiceTests
{
    private IEmployeeClientReader _employeeReader = null!;
    private IOnboardingSendService _sendService = null!;
    private IPluginSettingsWriter _settingsWriter = null!;
    private OnboardingRolloutService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _employeeReader = Substitute.For<IEmployeeClientReader>();
        _sendService = Substitute.For<IOnboardingSendService>();
        _settingsWriter = Substitute.For<IPluginSettingsWriter>();
        var logger = Substitute.For<ILogger<OnboardingRolloutService>>();
        _sut = new OnboardingRolloutService(_employeeReader, _sendService, _settingsWriter, logger);
    }

    [Test]
    public async Task ExecuteAsync_Sends_To_All_Employees_And_Persists_Completion()
    {
        var employees = new List<EmployeeClientInfo>
        {
            new(Guid.NewGuid(), "A", null, "a@test"),
            new(Guid.NewGuid(), "B", null, "b@test"),
        };
        _employeeReader.GetAllEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmployeeClientInfo>>(employees));
        _sendService.SendAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OnboardingSendResult.Success));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var sent = await _sut.ExecuteAsync("{\"BotToken\":\"t\"}", cts.Token);

        sent.ShouldBe(2);
        await _sendService.Received(2).SendAsync(Arg.Any<Guid>(), "{\"BotToken\":\"t\"}", Arg.Any<CancellationToken>());
        await _settingsWriter.Received(1).SetSettingAsync(
            TelegramOnboardingConstants.RolloutCompletedSettingKey,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_Counts_Only_Successful_Sends()
    {
        var employees = new List<EmployeeClientInfo>
        {
            new(Guid.NewGuid(), "A", null, "a@test"),
            new(Guid.NewGuid(), "B", null, null),
            new(Guid.NewGuid(), "C", null, "c@test"),
        };
        _employeeReader.GetAllEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmployeeClientInfo>>(employees));
        _sendService.SendAsync(employees[0].ClientId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OnboardingSendResult.Success));
        _sendService.SendAsync(employees[1].ClientId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OnboardingSendResult.NoContactChannel));
        _sendService.SendAsync(employees[2].ClientId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OnboardingSendResult.Success));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var sent = await _sut.ExecuteAsync("{}", cts.Token);

        sent.ShouldBe(2);
    }
}

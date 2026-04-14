// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Text;
using FluentAssertions;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class OnboardingSendServiceTests
{
    private const string BotConfig = "{\"BotToken\":\"test-token\"}";

    private IEmployeeClientReader _employeeReader = null!;
    private ITelegramOnboardingTokenRepository _tokenRepo = null!;
    private IMessengerContactRepository _contactRepo = null!;
    private IPluginEmailSender _emailSender = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private TelegramMessagingProvider _telegramProvider = null!;
    private HttpClient _httpClient = null!;
    private IMemoryCache _cache = null!;
    private OnboardingSendService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _employeeReader = Substitute.For<IEmployeeClientReader>();
        _tokenRepo = Substitute.For<ITelegramOnboardingTokenRepository>();
        _contactRepo = Substitute.For<IMessengerContactRepository>();
        _emailSender = Substitute.For<IPluginEmailSender>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();

        _httpClient = new HttpClient(new StubGetMeHandler("klacks_bot"));
        _cache = new MemoryCache(new MemoryCacheOptions());
        var providerLogger = Substitute.For<ILogger<TelegramMessagingProvider>>();
        _telegramProvider = new TelegramMessagingProvider(_httpClient, _cache, providerLogger);

        var logger = Substitute.For<ILogger<OnboardingSendService>>();
        _sut = new OnboardingSendService(
            _employeeReader,
            _tokenRepo,
            _contactRepo,
            _emailSender,
            _telegramProvider,
            _unitOfWork,
            logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _cache.Dispose();
    }

    [Test]
    public async Task SendAsync_Returns_NotEmployee_When_Client_Not_Found_Or_Not_Employee()
    {
        var clientId = Guid.NewGuid();
        _employeeReader.GetEmployeeAsync(clientId, Arg.Any<CancellationToken>()).Returns((EmployeeClientInfo?)null);

        var result = await _sut.SendAsync(clientId, BotConfig);

        result.Should().Be(OnboardingSendResult.NotEmployee);
    }

    [Test]
    public async Task SendAsync_Returns_AlreadyLinked_When_Contact_Exists()
    {
        var clientId = Guid.NewGuid();
        _employeeReader.GetEmployeeAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new EmployeeClientInfo(clientId, "Jane", null, "jane@example.com"));
        _contactRepo.GetByClientAndTypeAsync(clientId, MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new MessengerContact { ClientId = clientId, Type = MessengerType.Telegram, Value = "123" });

        var result = await _sut.SendAsync(clientId, BotConfig);

        result.Should().Be(OnboardingSendResult.AlreadyLinked);
    }

    [Test]
    public async Task SendAsync_Returns_NoContactChannel_When_Neither_Cell_Nor_Email_Present()
    {
        var clientId = Guid.NewGuid();
        _employeeReader.GetEmployeeAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new EmployeeClientInfo(clientId, "Jane", null, null));

        var result = await _sut.SendAsync(clientId, BotConfig);

        result.Should().Be(OnboardingSendResult.NoContactChannel);
    }

    [Test]
    public async Task SendAsync_Sends_Email_When_PrivateMail_Present_And_Returns_Success()
    {
        var clientId = Guid.NewGuid();
        _employeeReader.GetEmployeeAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new EmployeeClientInfo(clientId, "Jane", null, "jane@example.com"));
        _emailSender.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await _sut.SendAsync(clientId, BotConfig);

        result.Should().Be(OnboardingSendResult.Success);
        await _tokenRepo.Received(1).InvalidateAllForClientAsync(clientId, Arg.Any<CancellationToken>());
        await _tokenRepo.Received(1).AddAsync(Arg.Any<TelegramOnboardingToken>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _emailSender.Received(1).SendEmailAsync(
            "jane@example.com",
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("https://t.me/klacks_bot?start=")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_Returns_SendFailed_When_Only_Cell_Phone_Present()
    {
        var clientId = Guid.NewGuid();
        _employeeReader.GetEmployeeAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new EmployeeClientInfo(clientId, "Jane", "+41790000000", null));

        var result = await _sut.SendAsync(clientId, BotConfig);

        result.Should().Be(OnboardingSendResult.SendFailed);
    }

    private sealed class StubGetMeHandler : HttpMessageHandler
    {
        private readonly string _username;

        public StubGetMeHandler(string username)
        {
            _username = username;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"ok\":true,\"result\":{{\"id\":1,\"username\":\"{_username}\"}}}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}

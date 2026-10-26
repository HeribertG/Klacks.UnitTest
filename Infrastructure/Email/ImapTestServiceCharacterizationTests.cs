// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of ImapTestService.TestConnectionAsync on the paths reachable without an IMAP server: the
/// required-field validation, a refused loopback connection (network error), an out-of-range port and a null request
/// (both end in the generic catch), and the secure-socket option chosen per port and SSL flag. Pins result texts, keys
/// and every rendered log line with its level. Success, login, folder, TLS and timeout branches need a real server and
/// are not covered here.
/// </summary>

using System.Net;
using System.Net.Sockets;
using Klacks.Api.Domain.DTOs.Email;
using Klacks.Api.Infrastructure.Email;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class ImapTestServiceCharacterizationTests
{
    private const string User = "user@test.local";
    private const string Password = "secret";
    private const string Loopback = "127.0.0.1";
    private const string MissingFieldsMessage = "Missing required fields. Please fill in all required fields.";
    private const int OutOfRangePort = 99999;

    private RecordingLogger<ImapTestService> _logger = null!;
    private ImapTestService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new RecordingLogger<ImapTestService>();
        _sut = new ImapTestService(_logger);
    }

    [TestCase("", User, Password)]
    [TestCase("   ", User, Password)]
    [TestCase(Loopback, "", Password)]
    [TestCase(Loopback, User, " ")]
    public async Task MissingRequiredField_IsRefused_AfterTheFirstLogLine(string server, string username, string password)
    {
        var result = await _sut.TestConnectionAsync(new ImapTestRequest
        {
            Server = server,
            Port = 993,
            Username = username,
            Password = password,
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MissingFieldsMessage);
        result.MessageKey.ShouldBe("IMAP_TEST_MISSING_FIELDS");
        result.MessageParams.ShouldBeNull();
        result.ErrorDetails.ShouldBeNull();
        result.MessageCount.ShouldBeNull();
        _logger.Entries.Count.ShouldBe(1);
        _logger.Entries[0].Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public async Task MissingServer_LogsTheRequestWithAnEmptyServer()
    {
        await _sut.TestConnectionAsync(new ImapTestRequest
        {
            Server = "",
            Port = 143,
            Username = User,
            Password = Password,
        });

        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Information, $"Testing IMAP configuration for server: :143 with username: {User}"),
        });
    }

    [Test]
    public async Task NullRequest_EndsInTheUnexpectedCatch_WithoutAnyInformationLog()
    {
        var result = await _sut.TestConnectionAsync(null!);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("An unexpected error occurred: NullReferenceException");
        result.MessageKey.ShouldBe("IMAP_TEST_UNEXPECTED");
        result.ErrorDetails.ShouldNotBeNullOrEmpty();
        _logger.Entries.Count.ShouldBe(1);
        _logger.Entries[0].Level.ShouldBe(LogLevel.Error);
        _logger.Entries[0].Message.ShouldBe("Unexpected error during IMAP configuration test");
        _logger.Entries[0].Exception.ShouldBeOfType<NullReferenceException>();
    }

    [Test]
    public async Task RefusedConnection_MapsToTheNetworkError()
    {
        var closedPort = FreePort();

        var result = await _sut.TestConnectionAsync(Request(closedPort, enableSsl: false));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Network connection failed. Please check server address and port.");
        result.MessageKey.ShouldBe("IMAP_TEST_NETWORK_ERROR");
        result.MessageParams.ShouldBeNull();
        result.MessageCount.ShouldBeNull();
        result.ErrorDetails.ShouldNotBeNullOrEmpty();
        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Information, $"Testing IMAP configuration for server: {Loopback}:{closedPort} with username: {User}"),
            (LogLevel.Information, $"Connecting to IMAP server {Loopback}:{closedPort} with Auto..."),
            (LogLevel.Warning, $"Network error: {result.ErrorDetails}"),
        });
    }

    [Test]
    public async Task OutOfRangePort_EndsInTheUnexpectedCatch_AfterTheConnectingLine()
    {
        var result = await _sut.TestConnectionAsync(Request(OutOfRangePort, enableSsl: false));

        result.Success.ShouldBeFalse();
        result.MessageKey.ShouldBe("IMAP_TEST_UNEXPECTED");
        result.ErrorDetails.ShouldNotBeNullOrEmpty();
        _logger.Entries.Count.ShouldBe(3);
        _logger.Entries[1].Message.ShouldBe($"Connecting to IMAP server {Loopback}:{OutOfRangePort} with Auto...");
        var error = _logger.Entries[2];
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldBe("Unexpected error during IMAP configuration test");
        error.Exception.ShouldNotBeNull();
        result.Message.ShouldBe("An unexpected error occurred: " + error.Exception!.GetType().Name);
        result.ErrorDetails.ShouldBe(error.Exception.Message);
    }

    [TestCase(993, false, "SslOnConnect")]
    [TestCase(993, true, "SslOnConnect")]
    [TestCase(143, true, "StartTls")]
    [TestCase(143, false, "None")]
    public async Task SecureSocketOption_ForTheWellKnownPorts(int port, bool enableSsl, string expected)
    {
        await _sut.TestConnectionAsync(Request(port, enableSsl));

        _logger.Entries[1].Message.ShouldBe($"Connecting to IMAP server {Loopback}:{port} with {expected}...");
    }

    [TestCase(true, "StartTls")]
    [TestCase(false, "Auto")]
    public async Task SecureSocketOption_ForAnyOtherPort_FollowsTheSslFlag(bool enableSsl, string expected)
    {
        var closedPort = FreePort();

        await _sut.TestConnectionAsync(Request(closedPort, enableSsl));

        _logger.Entries[1].Message.ShouldBe($"Connecting to IMAP server {Loopback}:{closedPort} with {expected}...");
    }

    private static ImapTestRequest Request(int port, bool enableSsl) => new()
    {
        Server = Loopback,
        Port = port,
        Username = User,
        Password = Password,
        EnableSSL = enableSsl,
        Folder = "INBOX",
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

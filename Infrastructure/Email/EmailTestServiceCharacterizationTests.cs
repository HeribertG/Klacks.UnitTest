// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of EmailTestService.TestConnectionAsync against an in-process fake SMTP server on loopback, so the
/// connect, authenticate, send and disconnect path and its exception mapping run for real without the internet: the
/// success result and the sent test mail, a rejected login, a rejected recipient, a garbled greeting and a refused
/// connection. Also pins every rendered log line with its level and the validation precedence. The GMX/Microsoft auth
/// texts, the TLS handshake and the timeout branch are not reachable here.
/// </summary>

using System.Net;
using System.Net.Sockets;
using System.Text;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Infrastructure.Email;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailTestServiceCharacterizationTests
{
    private const string User = "user@test.local";
    private const string Password = "secret";
    private const string Loopback = "127.0.0.1";

    private RecordingLogger<EmailTestService> _logger = null!;
    private EmailTestService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new RecordingLogger<EmailTestService>();
        _sut = new EmailTestService(_logger);
    }

    [Test]
    public async Task AcceptingServer_SendsTheTestMail_AndReturnsTheSuccessResult()
    {
        await using var server = FakeSmtpServer.Start(FakeSmtpMode.Accept);

        var result = await _sut.TestConnectionAsync(Request(server.Port));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe($"Test email sent successfully to {User}! Please check your inbox to confirm delivery.");
        result.MessageKey.ShouldBe("EMAIL_TEST_SUCCESS_SENT");
        result.MessageParams.ShouldBe(new Dictionary<string, string> { { "email", User } });
        result.ErrorDetails.ShouldBeNull();

        await server.Completion;
        server.ReceivedQuit.ShouldBeTrue("the client disconnects with QUIT after sending");
        server.MailFrom.ShouldBe($"<{User}>");
        server.RcptTo.ShouldBe(new[] { $"<{User}>" });
        var mail = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(server.Data!)));
        mail.Subject.ShouldBe("Klacks Email Configuration Test");
        mail.From.Mailboxes.Single().Name.ShouldBe(User);
        mail.From.Mailboxes.Single().Address.ShouldBe(User);
        mail.To.Mailboxes.Single().Name.ShouldBe(User);
        mail.To.Mailboxes.Single().Address.ShouldBe(User);
        var html = mail.HtmlBody;
        html.ShouldContain("<h2>Email Configuration Test Successful!</h2>");
        html.ShouldContain("<p>This is a test email from your Klacks application to verify that your email configuration is working correctly.</p>");
        html.ShouldContain($"<li>SMTP Server: {Loopback}</li>");
        html.ShouldContain($"<li>Port: {server.Port}</li>");
        html.ShouldContain("<li>SSL/TLS: Auto</li>");
        html.ShouldContain("<li>Authentication: LOGIN</li>");
        html.ShouldMatch(@"<li>Test Date: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}</li>");
        html.ShouldContain("<p>If you receive this email, your email configuration is working properly.</p>");
        html.ShouldContain("<p><em>This is an automated test message from Klacks.</em></p>");
    }

    [Test]
    public async Task AcceptingServer_LogsEveryStep_AtInformation_InOrder()
    {
        await using var server = FakeSmtpServer.Start(FakeSmtpMode.Accept);

        await _sut.TestConnectionAsync(Request(server.Port));

        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Information, $"Testing email configuration for server: {Loopback}:{server.Port} with username: {User}"),
            (LogLevel.Information, "Password analysis: Length=6, StartsWithENC=False"),
            (LogLevel.Information, $"SSL Configuration: Port={server.Port}, RequestEnableSSL=False, SecureSocketOptions=Auto"),
            (LogLevel.Information, $"Connecting to SMTP server {Loopback}:{server.Port} with Auto..."),
            (LogLevel.Information, $"Connected successfully. Authenticating as {User}..."),
            (LogLevel.Information, "Authentication successful. Sending test email..."),
            (LogLevel.Information, $"Test email sent successfully to {User}"),
        });
    }

    [Test]
    public async Task RejectedLogin_MapsToTheGenericAuthFailure_WithTheServerMessageAsDetails()
    {
        await using var server = FakeSmtpServer.Start(FakeSmtpMode.RejectAuth);

        var result = await _sut.TestConnectionAsync(Request(server.Port));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Authentication failed. Please check your username and password.");
        result.MessageKey.ShouldBe("EMAIL_TEST_AUTH_FAILED");
        result.ErrorDetails.ShouldNotBeNullOrEmpty();
        var warning = _logger.Entries.Last();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"SMTP Authentication failed: {result.ErrorDetails}");
        _logger.Entries.ShouldNotContain(e => e.Message.StartsWith("Authentication successful", StringComparison.Ordinal));
    }

    [Test]
    public async Task RejectedRecipient_MapsToTheSmtpCommandError_WithTheStatusCode()
    {
        await using var server = FakeSmtpServer.Start(FakeSmtpMode.RejectRecipient);

        var result = await _sut.TestConnectionAsync(Request(server.Port));

        result.Success.ShouldBeFalse();
        result.MessageKey.ShouldBe("EMAIL_TEST_SMTP_COMMAND_ERROR");
        result.ErrorDetails.ShouldBe("Status: MailboxUnavailable");
        result.Message.ShouldStartWith("SMTP error: ");
        var exceptionMessage = result.Message["SMTP error: ".Length..];
        var warning = _logger.Entries.Last();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"SMTP command error: MailboxUnavailable - {exceptionMessage}");
        _logger.Entries[^2].Message.ShouldBe("Authentication successful. Sending test email...");
    }

    [Test]
    public async Task GarbledGreeting_MapsToTheProtocolError()
    {
        await using var server = FakeSmtpServer.Start(FakeSmtpMode.GarbledGreeting);

        var result = await _sut.TestConnectionAsync(Request(server.Port));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("SMTP protocol error occurred. Please check your server settings.");
        result.MessageKey.ShouldBe("EMAIL_TEST_PROTOCOL_ERROR");
        result.ErrorDetails.ShouldNotBeNullOrEmpty();
        var warning = _logger.Entries.Last();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"SMTP protocol error: {result.ErrorDetails}");
    }

    [Test]
    public async Task RefusedConnection_MapsToTheNetworkError()
    {
        var closedPort = FreePort();

        var result = await _sut.TestConnectionAsync(Request(closedPort));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Network connection failed. Please check server address and port.");
        result.MessageKey.ShouldBe("EMAIL_TEST_NETWORK_ERROR");
        result.ErrorDetails.ShouldNotBeNullOrEmpty();
        var warning = _logger.Entries.Last();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"Network error: {result.ErrorDetails}");
        _logger.Entries[^2].Message.ShouldBe($"Connecting to SMTP server {Loopback}:{closedPort} with Auto...");
    }

    [Test]
    public async Task MissingFields_WinOverAnInvalidPort_AndBothAnalysisLinesAreStillLogged()
    {
        var result = await _sut.TestConnectionAsync(new EmailTestRequest
        {
            Server = "smtp.gmx.net",
            Port = "abc",
            Username = User,
            Password = "",
            AuthenticationType = "<None>",
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Missing required fields. Please fill in all required fields.");
        result.MessageKey.ShouldBe("EMAIL_TEST_MISSING_FIELDS");
        result.ErrorDetails.ShouldBeNull();
        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Information, $"Testing email configuration for server: smtp.gmx.net:abc with username: {User}"),
            (LogLevel.Information, "Password analysis: Length=0, StartsWithENC=False"),
        });
    }

    [Test]
    public async Task InvalidPort_WinsOverTheAuthRequiredProviderCheck()
    {
        var result = await _sut.TestConnectionAsync(new EmailTestRequest
        {
            Server = "smtp.gmx.net",
            Port = "abc",
            Username = User,
            Password = "ENC:xyz",
            AuthenticationType = "<None>",
        });

        result.Message.ShouldBe("Invalid port number.");
        result.MessageKey.ShouldBe("EMAIL_TEST_INVALID_PORT");
        _logger.Entries[1].Message.ShouldBe("Password analysis: Length=7, StartsWithENC=True");
        _logger.Entries.Count.ShouldBe(2);
    }

    [Test]
    public async Task NoAuthenticationOnAKnownProvider_IsRefused_WithAWarning()
    {
        var result = await _sut.TestConnectionAsync(new EmailTestRequest
        {
            Server = "smtp.yahoo.com",
            Port = "25",
            Username = User,
            Password = Password,
            AuthenticationType = "",
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Authentication required for this email provider. Please set Authentication Type to 'LOGIN' and provide valid credentials.");
        result.MessageKey.ShouldBe("EMAIL_TEST_AUTH_REQUIRED");
        result.ErrorDetails.ShouldBe("Most email providers require authentication");
        _logger.Entries.Count.ShouldBe(3);
        _logger.Entries[2].Level.ShouldBe(LogLevel.Warning);
        _logger.Entries[2].Message.ShouldBe("Authentication set to '<None>' but server smtp.yahoo.com typically requires authentication");
    }

    [Test]
    public async Task NoAuthenticationOnAnUnknownServer_GoesOnToConnect()
    {
        var closedPort = FreePort();

        var result = await _sut.TestConnectionAsync(new EmailTestRequest
        {
            Server = Loopback,
            Port = closedPort.ToString(),
            Username = User,
            Password = Password,
            AuthenticationType = "<None>",
        });

        result.MessageKey.ShouldBe("EMAIL_TEST_NETWORK_ERROR");
    }

    [TestCase("465", false, "SslOnConnect")]
    [TestCase("587", false, "StartTls")]
    [TestCase("2525", true, "StartTls")]
    [TestCase("2525", false, "Auto")]
    public async Task SecureSocketOption_FollowsPortThenSslFlag(string port, bool enableSsl, string expected)
    {
        var request = Request(0);
        request.Server = Loopback;
        request.Port = port;
        request.EnableSSL = enableSsl;
        request.Timeout = 1000;

        await _sut.TestConnectionAsync(request);

        _logger.Entries[2].Message.ShouldBe($"SSL Configuration: Port={port}, RequestEnableSSL={enableSsl}, SecureSocketOptions={expected}");
        _logger.Entries[3].Message.ShouldBe($"Connecting to SMTP server {Loopback}:{port} with {expected}...");
    }

    private static EmailTestRequest Request(int port) => new()
    {
        Server = Loopback,
        Port = port.ToString(),
        Username = User,
        Password = Password,
        EnableSSL = false,
        AuthenticationType = "LOGIN",
        Timeout = 5000,
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private enum FakeSmtpMode
    {
        Accept,
        RejectAuth,
        RejectRecipient,
        GarbledGreeting,
    }

    /// <summary>
    /// Minimal single-connection SMTP responder: greets, advertises AUTH PLAIN/LOGIN without STARTTLS, accepts or
    /// rejects per mode, and records the envelope and the DATA payload.
    /// </summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly FakeSmtpMode _mode;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

        private FakeSmtpServer(FakeSmtpMode mode)
        {
            _mode = mode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Completion = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public Task Completion { get; }

        public string? MailFrom { get; private set; }

        public List<string> RcptTo { get; } = [];

        public string? Data { get; private set; }

        public bool ReceivedQuit { get; private set; }

        public static FakeSmtpServer Start(FakeSmtpMode mode) => new(mode);

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            if (_mode == FakeSmtpMode.GarbledGreeting)
            {
                await writer.WriteLineAsync("hello there");
                await Task.Delay(500);
                return;
            }

            await writer.WriteLineAsync("220 fake.local ESMTP");
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                var command = line.ToUpperInvariant();
                if (command.StartsWith("EHLO", StringComparison.Ordinal) || command.StartsWith("HELO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-fake.local");
                    await writer.WriteLineAsync("250 AUTH PLAIN LOGIN");
                }
                else if (command.StartsWith("AUTH", StringComparison.Ordinal))
                {
                    await HandleAuthAsync(command, reader, writer);
                }
                else if (command.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                {
                    MailFrom = line["MAIL FROM:".Length..].Split(' ')[0];
                    await writer.WriteLineAsync("250 OK");
                }
                else if (command.StartsWith("RCPT TO:", StringComparison.Ordinal))
                {
                    RcptTo.Add(line["RCPT TO:".Length..].Split(' ')[0]);
                    await writer.WriteLineAsync(_mode == FakeSmtpMode.RejectRecipient
                        ? "550 5.1.1 Mailbox unavailable"
                        : "250 OK");
                }
                else if (command == "DATA")
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    Data = await ReadDataAsync(reader);
                    await writer.WriteLineAsync("250 OK queued");
                }
                else if (command == "QUIT")
                {
                    ReceivedQuit = true;
                    await writer.WriteLineAsync("221 Bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
        }

        private async Task HandleAuthAsync(string command, StreamReader reader, StreamWriter writer)
        {
            if (command.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
            {
                if (command.Trim() == "AUTH LOGIN")
                {
                    await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                    await reader.ReadLineAsync(_cts.Token);
                }

                await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                await reader.ReadLineAsync(_cts.Token);
            }
            else if (command.Trim() == "AUTH PLAIN")
            {
                await writer.WriteLineAsync("334 ");
                await reader.ReadLineAsync(_cts.Token);
            }

            await writer.WriteLineAsync(_mode == FakeSmtpMode.RejectAuth
                ? "535 5.7.8 Authentication credentials invalid"
                : "235 2.7.0 Authentication successful");
        }

        private async Task<string> ReadDataAsync(StreamReader reader)
        {
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(_cts.Token) is { } line && line != ".")
            {
                data.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
            }

            return data.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await Completion;
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
        }
    }
}

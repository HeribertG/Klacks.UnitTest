// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the HTTPS/public-reachability classification of a configured webhook URL.
/// </summary>
using Klacks.Plugin.Messaging.Application.Services.Setup;
using Klacks.Plugin.Messaging.Domain.Enums;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class WebhookUrlClassifierTests
{
    [TestCase("https://klacks.example.com/api/messaging/webhook/telegram", WebhookUrlVerdict.Public)]
    [TestCase("http://klacks.example.com/api/messaging/webhook/telegram", WebhookUrlVerdict.NotHttps)]
    [TestCase("https://localhost:5001/api/messaging/webhook/telegram", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://127.0.0.1/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://192.168.1.20/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://10.0.0.5/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://172.20.0.3/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://nas.local/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://[::1]/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("", WebhookUrlVerdict.Missing)]
    [TestCase("not a url", WebhookUrlVerdict.Invalid)]
    [TestCase("https://0.0.0.0/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://[::]/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://100.64.0.1/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://100.99.0.1/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://100.127.255.255/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://100.63.255.255/x", WebhookUrlVerdict.Public)]
    [TestCase("https://100.128.0.1/x", WebhookUrlVerdict.Public)]
    [TestCase("https://localhost./x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://foo.localhost/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://klacks-server/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://server.internal/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://printer.lan/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://box.home.arpa/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://[::ffff:192.168.1.1]/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://[fd00::1]/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://[fe80::1]/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://169.254.1.1/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://[2001:4860:4860::8888]/x", WebhookUrlVerdict.Public)]
    [TestCase("https://172.15.0.1/x", WebhookUrlVerdict.Public)]
    [TestCase("https://172.32.0.1/x", WebhookUrlVerdict.Public)]
    [TestCase("https://172.16.0.1/x", WebhookUrlVerdict.NotPublic)]
    [TestCase("https://172.31.255.255/x", WebhookUrlVerdict.NotPublic)]
    public void Classify(string url, WebhookUrlVerdict expected) => WebhookUrlClassifier.Classify(url).ShouldBe(expected);
}

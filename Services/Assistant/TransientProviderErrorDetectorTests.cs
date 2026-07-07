// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TransientProviderErrorDetectorTests
{
    [TestCase("Anthropic rate limit exceeded")]
    [TestCase("rate_limit_exceeded: Requested 15110 tokens")]
    [TestCase("HTTP 429: Too Many Requests")]
    [TestCase("The model is currently overloaded")]
    [TestCase("503 Service Unavailable")]
    [TestCase("Gateway timeout while contacting provider")]
    [TestCase("Request timed out")]
    [TestCase("Provider error: 502 Bad Gateway")]
    public void IsTransient_ReturnsTrue_ForRetryableProviderErrors(string errorMessage)
    {
        Assert.That(TransientProviderErrorDetector.IsTransient(errorMessage), Is.True);
    }

    [TestCase("Invalid API key")]
    [TestCase("The selected model is not available.")]
    [TestCase("400 Bad Request: invalid tool schema")]
    [TestCase("content policy violation")]
    public void IsTransient_ReturnsFalse_ForPermanentProviderErrors(string errorMessage)
    {
        Assert.That(TransientProviderErrorDetector.IsTransient(errorMessage), Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsTransient_ReturnsFalse_ForEmptyMessages(string? errorMessage)
    {
        Assert.That(TransientProviderErrorDetector.IsTransient(errorMessage), Is.False);
    }
}

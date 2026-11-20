// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Services.Assistant.Pseudonymization;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

[TestFixture]
public class LocalModelEndpointTests
{
    [TestCase("http://localhost:11434/v1", "llama3", true)]
    [TestCase("http://127.0.0.5:1234", "llama3", true)]
    [TestCase("http://[::1]:11434", "llama3", true)]
    [TestCase("http://10.1.2.3/v1", "llama3", true)]
    [TestCase("http://172.16.0.1/v1", "llama3", true)]
    [TestCase("http://172.31.255.1/v1", "llama3", true)]
    [TestCase("http://192.168.1.20:1234/v1", "qwen", true)]
    [TestCase("http://169.254.1.1/v1", "qwen", true)]
    [TestCase("http://[fd00::5]/v1", "qwen", true)]
    [TestCase("http://host.docker.internal:11434", "llama3", true)]
    [TestCase("http://ai-box.local:11434", "llama3", true)]
    [TestCase("http://ollama:11434/v1", "llama3", true)]
    [TestCase("http://172.15.0.1/v1", "llama3", false)]
    [TestCase("http://172.32.0.1/v1", "llama3", false)]
    [TestCase("http://8.8.8.8/v1", "llama3", false)]
    [TestCase("http://[2001:4860::1]/v1", "llama3", false)]
    [TestCase("https://api.openai.com/v1", "gpt", false)]
    [TestCase("https://localhost@api.openai.com/v1", "gpt", false)]
    [TestCase("http://localhost:11434/v1", "gpt-oss:120b-cloud", false)]
    [TestCase("http://localhost:11434/v1", "kimi-k2:1t-CLOUD", false)]
    [TestCase(null, "llama3", false)]
    [TestCase("", "llama3", false)]
    [TestCase("not a url", "llama3", false)]
    public void IsLocal(string? baseUrl, string model, bool expected)
    {
        LocalModelEndpoint.IsLocal(baseUrl, model).ShouldBe(expected);
    }
}

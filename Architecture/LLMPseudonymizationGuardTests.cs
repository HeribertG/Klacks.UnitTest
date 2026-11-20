// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every language-model call passes the pseudonymisation decorator: (1) the factory puts
/// PseudonymizingLLMProvider directly around the concrete provider when the switch is on or missing, and leaves it out
/// only for an explicit "false" (the local exception is decided per request inside the decorator, see
/// PseudonymizingLLMProviderTests); (2) no code outside LLMProviderFactory resolves or constructs a concrete
/// ILLMProvider, so no call can bypass the single wrap point. Registrations in Infrastructure/Extensions are not
/// resolutions and are skipped. The image refusal (guard 4 of the design) is pinned in PseudonymizingLLMProviderTests.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Base;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Playground;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Pseudonymization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ILLMProvider = Klacks.Api.Domain.Services.Assistant.Providers.ILLMProvider;
using LLMProvider = Klacks.Api.Domain.Models.Assistant.LLMProvider;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class LLMPseudonymizationGuardTests
{
    private const string ProviderId = "custom-provider";
    private const string FactoryFile = "LLMProviderFactory.cs";

    private ILLMRepository _repository = null!;
    private ISettingsReader _settings = null!;
    private Klacks.Api.Domain.Interfaces.Playground.IPlaygroundMode _mode = null!;
    private GenericOpenAICompatibleProvider _concrete = null!;
    private LLMProviderFactory _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _repository.GetProviderByIdAsync(ProviderId).Returns(new LLMProvider
        {
            ProviderId = ProviderId, ProviderName = "Custom", IsEnabled = true, Priority = 1, BaseUrl = "https://llm.example.com/v1"
        });
        _settings = Substitute.For<ISettingsReader>();
        _mode = Substitute.For<Klacks.Api.Domain.Interfaces.Playground.IPlaygroundMode>();
        _concrete = new GenericOpenAICompatibleProvider(
            new HttpClient(), NullLogger<GenericOpenAICompatibleProvider>.Instance, new ConfigurationBuilder().Build());
        var services = new ServiceCollection().AddSingleton(_concrete).BuildServiceProvider();
        _sut = new LLMProviderFactory(
            services, _repository, NullLogger<LLMProviderFactory>.Instance, _mode,
            Substitute.For<Klacks.Api.Domain.Interfaces.Playground.IPlaygroundTokenBudget>(),
            _settings, Substitute.For<IPseudonymMapSource>(), Substitute.For<ICompanyClock>());
    }

    private void SwitchIs(string? value) =>
        _settings.GetSetting(SettingKeys.LlmPseudonymizationEnabled)
            .Returns(value == null ? null : new SettingsRow { Type = SettingKeys.LlmPseudonymizationEnabled, Value = value });

    [TestCase(null)]
    [TestCase("true")]
    [TestCase("True")]
    [TestCase("garbage")]
    public async Task SwitchOnOrMissing_WrapsTheConcreteProvider(string? value)
    {
        SwitchIs(value);

        var provider = await _sut.GetProviderAsync(ProviderId);

        var decorator = provider.ShouldBeOfType<PseudonymizingLLMProvider>();
        decorator.Inner.ShouldBeSameAs(_concrete);
    }

    [Test]
    public async Task SwitchOff_ReturnsTheConcreteProvider()
    {
        SwitchIs("false");

        (await _sut.GetProviderAsync(ProviderId)).ShouldBeSameAs(_concrete);
    }

    [Test]
    public async Task Playground_WrapsOutsideThePseudonymisation()
    {
        SwitchIs(null);
        _mode.IsActive.Returns(true);

        var provider = await _sut.GetProviderAsync(ProviderId);

        var playground = provider.ShouldBeOfType<PlaygroundBudgetedLLMProvider>();
        playground.Inner.ShouldBeOfType<PseudonymizingLLMProvider>().Inner.ShouldBeSameAs(_concrete);
    }

    [Test]
    public async Task LocalBaseUrl_ExemptsOnlyTheGenericProvider()
    {
        SwitchIs(null);
        var openAi = new Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI.OpenAIProvider(
            new HttpClient(), NullLogger<Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI.OpenAIProvider>.Instance,
            new ConfigurationBuilder().Build());
        var services = new ServiceCollection().AddSingleton(_concrete).AddSingleton(openAi).BuildServiceProvider();
        foreach (var id in new[] { ProviderId, "openai" })
        {
            _repository.GetProviderByIdAsync(id).Returns(new LLMProvider
            {
                ProviderId = id, ProviderName = id, IsEnabled = true, Priority = 1, BaseUrl = "http://localhost:11434/v1"
            });
        }

        var factory = new LLMProviderFactory(
            services, _repository, NullLogger<LLMProviderFactory>.Instance, _mode,
            Substitute.For<Klacks.Api.Domain.Interfaces.Playground.IPlaygroundTokenBudget>(),
            _settings, Substitute.For<IPseudonymMapSource>(), Substitute.For<ICompanyClock>());
        var request = new Klacks.Api.Domain.Services.Assistant.Providers.LLMProviderRequest { ModelId = "llama3" };

        var generic = (PseudonymizingLLMProvider)(await factory.GetProviderAsync(ProviderId))!;
        var named = (PseudonymizingLLMProvider)(await factory.GetProviderAsync("openai"))!;

        generic.IsActiveFor(request).ShouldBeFalse();
        named.IsActiveFor(request).ShouldBeTrue("a named cloud client may ignore its BaseUrl, so it is never exempt");
    }

    [Test]
    public void NoCodeOutsideTheFactory_ResolvesOrConstructsAConcreteProvider()
    {
        var providerNames = typeof(BaseHttpProvider).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ILLMProvider).IsAssignableFrom(type))
            .Select(type => Regex.Escape(type.Name))
            .ToList();
        providerNames.ShouldNotBeEmpty();
        var pattern = new Regex(
            $@"(GetService|GetRequiredService|CreateInstance)\s*<\s*[\w.]*\b({string.Join('|', providerNames)})\s*>|new\s+[\w.]*\b({string.Join('|', providerNames)})\s*\(",
            RegexOptions.CultureInvariant);

        var apiRoot = RepositoryRootLocator.ApiProject;
        var offenders = Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.EndsWith(FactoryFile, StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, index))
                .Where(entry => pattern.IsMatch(entry.line)))
            .Select(entry => $"{Path.GetFileName(entry.path)}:{entry.index + 1}: {entry.line.Trim()}")
            .ToList();

        offenders.ShouldBeEmpty(
            "Only LLMProviderFactory may resolve or construct a concrete ILLMProvider; anything else would bypass the " +
            "pseudonymisation decorator. Get the provider from ILLMProviderFactory instead.");
    }

    [Test]
    public void NoTypeOutsideTheFactory_TakesAConcreteProviderAsConstructorParameter()
    {
        var concreteProviders = typeof(BaseHttpProvider).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ILLMProvider).IsAssignableFrom(type))
            .ToHashSet();

        var offenders = typeof(BaseHttpProvider).Assembly.GetTypes()
            .Where(type => type != typeof(LLMProviderFactory) && !concreteProviders.Contains(type))
            .SelectMany(type => type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => concreteProviders.Contains(parameter.ParameterType))
                .Select(parameter => $"{type.FullName}({parameter.ParameterType.Name} {parameter.Name})"))
            .ToList();

        offenders.ShouldBeEmpty("A concrete provider injected by type would bypass the pseudonymisation decorator.");
    }

    [Test]
    public void EveryPseudonymPrefix_IsRecognisedByTheReturnPath()
    {
        foreach (var prefix in Klacks.Api.Domain.Services.Assistant.Pseudonymization.PseudonymPrefixes.All)
        {
            Klacks.Api.Domain.Services.Assistant.Pseudonymization.PseudonymRestorer.TokenPattern()
                .IsMatch($"{prefix}-0001").ShouldBeTrue($"{prefix} is not recognised by the return path");
        }
    }
}

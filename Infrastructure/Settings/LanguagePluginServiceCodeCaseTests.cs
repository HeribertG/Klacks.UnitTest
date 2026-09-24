// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that LanguagePluginService treats a pack code case-insensitively while holding it in the
/// spelling of its manifest (zh-CN). The installed codes used to be kept in a case-sensitive set: a fresh
/// install stored zh-CN, a restart lower-cased the settings key to zh-cn, so after every restart
/// GetAllPlugins reported zh-CN as not installed and a caller asking for zh-CN got false.
/// The uninstall runs against a Npgsql context whose connection and commands are suppressed, because the
/// uninstall path issues raw SQL that the in-memory provider cannot execute.
/// </summary>

using System.Data;
using System.Data.Common;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginServiceCodeCaseTests
{
    private const string ManifestCode = "zh-CN";
    private const string LowerCaseCode = "zh-cn";
    private const string UpperCaseCode = "ZH-CN";
    private const string InstalledSettingKey = "INSTALLED_LANGUAGE_ZH-CN";
    private const string InstalledValue = "true";
    private const string UninstalledValue = "false";
    private const string PluginDirectoryConfigKey = "LanguagePlugins:Directory";
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";

    private string _pluginDirectory = null!;
    private DataBaseContext _context = null!;
    private ISettingsRepository _settingsRepository = null!;
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-plugin-code-case-" + Guid.NewGuid().ToString("N"));
        var packDirectory = Path.Combine(_pluginDirectory, ManifestCode);
        Directory.CreateDirectory(packDirectory);
        File.WriteAllText(
            Path.Combine(packDirectory, "manifest.json"),
            $"{{\"code\": \"{ManifestCode}\", \"name\": \"Chinese (Simplified)\"}}");

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SuppressedConnectionInterceptor(), new EmptyResultCommandInterceptor())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.GetSettingsList()
            .Returns(Task.FromResult<IEnumerable<SettingsEntity>>(new List<SettingsEntity>()));

        var skillRepository = Substitute.For<IAgentSkillRepository>();
        skillRepository.GetAllEnabledTrackedAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>());
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(_settingsRepository);
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(skillRepository);
        services.AddSingleton(recipeRepository);
        services.AddSingleton(Substitute.For<ISkillPhraseRepository>());
        services.AddSingleton(Substitute.For<INavigationTargetSynonymRepository>());
        services.AddSingleton(Substitute.For<ISkillCatalogRefresher>());
        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
        _context.Dispose();

        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [TestCase(ManifestCode)]
    [TestCase(LowerCaseCode)]
    [TestCase(UpperCaseCode)]
    public async Task Install_UnderAnySpelling_HoldsTheManifestSpellingAndAnswersForEverySpelling(string installCode)
    {
        var service = CreateService();
        await service.InitializeAsync();

        (await service.InstallAsync(installCode)).ShouldBeTrue();

        service.GetInstalledPluginCodes().ShouldBe([ManifestCode]);
        AssertInstalledUnderEverySpelling(service, expected: true);
        await _settingsRepository.Received(1).AddSetting(Arg.Is<SettingsEntity>(s => s.Type == InstalledSettingKey));
    }

    [Test]
    public async Task Restart_LoadsTheUpperCasedSettingKeyUnderTheManifestSpelling()
    {
        GivenInstalledSetting();
        var service = CreateService();

        await service.InitializeAsync();

        service.GetInstalledPluginCodes().ShouldBe([ManifestCode]);
        AssertInstalledUnderEverySpelling(service, expected: true);
    }

    [TestCase(ManifestCode)]
    [TestCase(LowerCaseCode)]
    public async Task Uninstall_UnderAnySpelling_RemovesTheInstalledCode(string uninstallCode)
    {
        var setting = GivenInstalledSetting();
        var service = CreateService();
        await service.InitializeAsync();

        (await service.UninstallAsync(uninstallCode)).ShouldBeTrue();

        service.GetInstalledPluginCodes().ShouldBeEmpty();
        AssertInstalledUnderEverySpelling(service, expected: false);
        setting.Value.ShouldBe(UninstalledValue);
    }

    private LanguagePluginService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PluginDirectoryConfigKey] = _pluginDirectory
            })
            .Build();

        return new LanguagePluginService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<LanguagePluginService>.Instance);
    }

    private SettingsEntity GivenInstalledSetting()
    {
        var setting = new SettingsEntity { Id = Guid.NewGuid(), Type = InstalledSettingKey, Value = InstalledValue };
        _settingsRepository.GetSettingsList()
            .Returns(Task.FromResult<IEnumerable<SettingsEntity>>(new List<SettingsEntity> { setting }));
        _settingsRepository.GetSetting(InstalledSettingKey).Returns(setting);
        return setting;
    }

    private static void AssertInstalledUnderEverySpelling(LanguagePluginService service, bool expected)
    {
        service.GetPlugin(ManifestCode)!.IsInstalled.ShouldBe(expected);
        service.GetPlugin(LowerCaseCode)!.IsInstalled.ShouldBe(expected);
        service.GetPlugin(UpperCaseCode)!.IsInstalled.ShouldBe(expected);
        service.GetAllPlugins().Single(p => !p.IsCore).IsInstalled.ShouldBe(expected);
    }

    private sealed class SuppressedConnectionInterceptor : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class EmptyResultCommandInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader());

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader()));

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result) =>
            InterceptionResult<int>.SuppressWithResult(0);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}

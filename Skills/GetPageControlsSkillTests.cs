// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetPageControlsSkillTests
{
    private IUiControlRepository _uiControlRepository = null!;
    private GetPageControlsSkill _sut = null!;

    [SetUp]
    public void Setup()
    {
        _uiControlRepository = Substitute.For<IUiControlRepository>();
        _sut = new GetPageControlsSkill(_uiControlRepository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    [Test]
    public async Task UnknownPage_NamesTheRegisteredPagesSoTheModelCanCorrectItself()
    {
        _uiControlRepository.GetByPageKeyAsync("settings", Arg.Any<CancellationToken>()).Returns(new List<UiControl>());
        _uiControlRepository.GetDistinctPageKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new List<string> { "settings-email", "settings-general" });

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["page"] = "settings" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("settings-email, settings-general"));
    }

    [Test]
    public async Task UnknownPage_WithoutRegisteredPages_SaysSo()
    {
        _uiControlRepository.GetByPageKeyAsync("settings", Arg.Any<CancellationToken>()).Returns(new List<UiControl>());
        _uiControlRepository.GetDistinctPageKeysAsync(Arg.Any<CancellationToken>()).Returns(new List<string>());

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["page"] = "settings" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("No UI pages registered yet"));
    }

    [Test]
    public async Task KnownPage_ReturnsItsControls()
    {
        _uiControlRepository.GetByPageKeyAsync("settings-email", Arg.Any<CancellationToken>())
            .Returns(new List<UiControl> { new() { PageKey = "settings-email", ControlKey = "smtp-host" } });

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["page"] = "Settings-Email" });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("1 control(s)"));
    }
}

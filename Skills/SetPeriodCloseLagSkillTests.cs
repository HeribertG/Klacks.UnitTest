using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetPeriodCloseLagSkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ISettingsEncryptionService _encryption = null!;
    private SetPeriodCloseLagSkill _sut = null!;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "Admin" }
    };

    [SetUp]
    public void Setup()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _encryption = Substitute.For<ISettingsEncryptionService>();
        _encryption.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        _encryption.ProcessForReading(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        _sut = new SetPeriodCloseLagSkill(_settingsRepository, _unitOfWork, _encryption);
    }

    private void StubStoreThenReadBack(string readBackValue) =>
        _settingsRepository.GetSetting(SettingKeys.PeriodCloseLagDays).Returns(
            Task.FromResult<SettingsRow?>(null),
            Task.FromResult<SettingsRow?>(new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = readBackValue }));

    private static Dictionary<string, object> Args(int lagDays) => new()
    {
        [PeriodCloseParameters.LagDays] = lagDays
    };

    [Test]
    public void SettingKey_HasTheDocumentedName()
    {
        Assert.That(SettingKeys.PeriodCloseLagDays, Is.EqualTo("PERIOD_CLOSE_LAG_DAYS"));
    }

    [Test]
    public async Task ExecuteAsync_ValidLag_WritesTheValueAndVerifiesByReadBack()
    {
        StubStoreThenReadBack("5");

        var result = await _sut.ExecuteAsync(Ctx(), Args(5));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _settingsRepository.Received(1).AddSetting(Arg.Is<SettingsRow>(s =>
            s.Type == SettingKeys.PeriodCloseLagDays && s.Value == "5"));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [TestCase(0)]
    [TestCase(31)]
    public async Task ExecuteAsync_BoundaryLags_AreAccepted(int lagDays)
    {
        StubStoreThenReadBack(lagDays.ToString());

        var result = await _sut.ExecuteAsync(Ctx(), Args(lagDays));

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_ExistingRow_IsUpdatedNotDuplicated()
    {
        var existing = new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = "2" };
        _settingsRepository.GetSetting(SettingKeys.PeriodCloseLagDays).Returns(
            Task.FromResult<SettingsRow?>(existing),
            Task.FromResult<SettingsRow?>(new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = "7" }));

        var result = await _sut.ExecuteAsync(Ctx(), Args(7));

        Assert.That(result.Success, Is.True);
        await _settingsRepository.Received(1).PutSetting(Arg.Is<SettingsRow>(s => s.Value == "7"));
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsRow>());
    }

    [Test]
    public async Task ExecuteAsync_ReadBackMismatch_IsReportedAsNotPersisted()
    {
        StubStoreThenReadBack("9");

        var result = await _sut.ExecuteAsync(Ctx(), Args(5));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not persisted"));
    }

    [Test]
    public async Task ExecuteAsync_RowMissingAfterWrite_IsReportedAsNotPersisted()
    {
        _settingsRepository.GetSetting(SettingKeys.PeriodCloseLagDays)
            .Returns(Task.FromResult<SettingsRow?>(null));

        var result = await _sut.ExecuteAsync(Ctx(), Args(5));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not persisted"));
    }

    [TestCase(-1)]
    [TestCase(32)]
    [TestCase(400)]
    public async Task ExecuteAsync_OutOfRangeLag_IsRejectedBeforeAnyWrite(int lagDays)
    {
        var result = await _sut.ExecuteAsync(Ctx(), Args(lagDays));

        Assert.That(result.Success, Is.False);
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsRow>());
    }

    [Test]
    public async Task ExecuteAsync_LagMissing_IsRejectedBeforeAnyWrite()
    {
        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain(PeriodCloseParameters.LagDays));
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}

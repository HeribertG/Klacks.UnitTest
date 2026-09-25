using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetPlanningDeadlineLeadSkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ISettingsEncryptionService _encryption = null!;
    private SetPlanningDeadlineLeadSkill _sut = null!;

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
        StubCompliance(null);
        _sut = new SetPlanningDeadlineLeadSkill(_settingsRepository, _unitOfWork, _encryption);
    }

    private void StubCompliance(string? value) =>
        _settingsRepository.GetSetting(SettingKeys.ComplianceRosterPublicationMinLeadDays).Returns(
            Task.FromResult<SettingsRow?>(value == null
                ? null
                : new SettingsRow { Type = SettingKeys.ComplianceRosterPublicationMinLeadDays, Value = value }));

    private void StubStoreThenReadBack(string readBackValue) =>
        _settingsRepository.GetSetting(SettingKeys.PlanningDeadlineLeadDays).Returns(
            Task.FromResult<SettingsRow?>(null),
            Task.FromResult<SettingsRow?>(new SettingsRow { Type = SettingKeys.PlanningDeadlineLeadDays, Value = readBackValue }));

    private static Dictionary<string, object> Args(int announcement = 14, int transit = 3, int review = 2) => new()
    {
        [PlanningDeadlineParameters.AnnouncementDays] = announcement,
        [PlanningDeadlineParameters.TransitDays] = transit,
        [PlanningDeadlineParameters.ReviewDays] = review
    };

    [Test]
    public async Task ExecuteAsync_ValidInputs_WritesTheServerComputedSumAndVerifies()
    {
        StubStoreThenReadBack("19");

        var result = await _sut.ExecuteAsync(Ctx(), Args());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _settingsRepository.Received(1).AddSetting(Arg.Is<SettingsRow>(s =>
            s.Type == SettingKeys.PlanningDeadlineLeadDays && s.Value == "19"));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ExecuteAsync_TransitOmitted_CountsAsZeroForEmail()
    {
        StubStoreThenReadBack("16");
        var args = Args();
        args.Remove(PlanningDeadlineParameters.TransitDays);

        var result = await _sut.ExecuteAsync(Ctx(), args);

        Assert.That(result.Success, Is.True);
        await _settingsRepository.Received(1).AddSetting(Arg.Is<SettingsRow>(s => s.Value == "16"));
    }

    [Test]
    public async Task ExecuteAsync_ComplianceMinimumRaisesAShorterAnnouncement()
    {
        StubCompliance("21");
        StubStoreThenReadBack("26");

        var result = await _sut.ExecuteAsync(Ctx(), Args(announcement: 14, transit: 3, review: 2));

        Assert.That(result.Success, Is.True);
        await _settingsRepository.Received(1).AddSetting(Arg.Is<SettingsRow>(s => s.Value == "26"));
    }

    [Test]
    public async Task ExecuteAsync_ReadBackMismatch_IsReportedAsNotPersisted()
    {
        StubStoreThenReadBack("7");

        var result = await _sut.ExecuteAsync(Ctx(), Args());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not persisted"));
    }

    [TestCase(-1, 0, 2)]
    [TestCase(14, 400, 2)]
    [TestCase(14, 0, -5)]
    public async Task ExecuteAsync_OutOfRangeInput_IsRejectedBeforeAnyWrite(int announcement, int transit, int review)
    {
        var result = await _sut.ExecuteAsync(Ctx(), Args(announcement, transit, review));

        Assert.That(result.Success, Is.False);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase(PlanningDeadlineParameters.AnnouncementDays)]
    [TestCase(PlanningDeadlineParameters.ReviewDays)]
    public async Task ExecuteAsync_RequiredInputMissing_IsRejectedBeforeAnyWrite(string missing)
    {
        var args = Args();
        args.Remove(missing);

        var result = await _sut.ExecuteAsync(Ctx(), args);

        Assert.That(result.Success, Is.False);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ExecuteAsync_ZeroInputs_StoreZeroWhichClearsTheDeadline()
    {
        StubStoreThenReadBack("0");

        var result = await _sut.ExecuteAsync(Ctx(), Args(0, 0, 0));

        Assert.That(result.Success, Is.True);
    }
}

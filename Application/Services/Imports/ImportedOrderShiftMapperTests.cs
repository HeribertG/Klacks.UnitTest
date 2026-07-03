using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.Services.Imports;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ImportedOrderShiftMapperTests
{
    private static ImportedOrderPayload BuildPayload()
    {
        return new ImportedOrderPayload
        {
            SourceSystemId = "erp-1",
            ExternalOrderReference = "ORD-1",
            Description = "Morning care visit",
            FromDate = new DateOnly(2026, 8, 1),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(15, 0),
            IsTimeRange = true,
            DurationMinutes = 20
        };
    }

    [Test]
    public void ResolveWorkTimeHours_ExplicitDuration_Wins()
    {
        ImportedOrderShiftMapper.ResolveWorkTimeHours(BuildPayload()).ShouldBe(0.3333m);
    }

    [Test]
    public void ResolveWorkTimeHours_NoDuration_UsesStartToEndDistance()
    {
        var payload = BuildPayload();
        payload.DurationMinutes = null;
        payload.IsTimeRange = false;
        payload.StartTime = new TimeOnly(18, 0);
        payload.EndTime = new TimeOnly(22, 0);

        ImportedOrderShiftMapper.ResolveWorkTimeHours(payload).ShouldBe(4m);
    }

    [Test]
    public void ResolveWorkTimeHours_OvernightShift_WrapsMidnight()
    {
        var payload = BuildPayload();
        payload.DurationMinutes = null;
        payload.IsTimeRange = false;
        payload.StartTime = new TimeOnly(22, 0);
        payload.EndTime = new TimeOnly(6, 0);

        ImportedOrderShiftMapper.ResolveWorkTimeHours(payload).ShouldBe(8m);
    }

    [Test]
    public void ApplyToDraft_MapsDescriptionAndWorkTime()
    {
        var clientId = Guid.NewGuid();

        var shift = ImportedOrderShiftMapper.BuildDraft(BuildPayload(), clientId);

        shift.Description.ShouldBe("Morning care visit");
        shift.WorkTime.ShouldBe(0.3333m);
        shift.IsTimeRange.ShouldBeTrue();
    }

    [Test]
    public void DiffersFromSealedOrder_DescriptionChanged_ReturnsTrue()
    {
        var clientId = Guid.NewGuid();
        var sealedOrder = ImportedOrderShiftMapper.BuildDraft(BuildPayload(), clientId);
        var changed = BuildPayload();
        changed.Description = "Evening care visit";

        ImportedOrderShiftMapper.DiffersFromSealedOrder(sealedOrder, changed, clientId).ShouldBeTrue();
    }

    [Test]
    public void DiffersFromSealedOrder_DurationChanged_ReturnsTrue()
    {
        var clientId = Guid.NewGuid();
        var sealedOrder = ImportedOrderShiftMapper.BuildDraft(BuildPayload(), clientId);
        var changed = BuildPayload();
        changed.DurationMinutes = 30;

        ImportedOrderShiftMapper.DiffersFromSealedOrder(sealedOrder, changed, clientId).ShouldBeTrue();
    }

    [Test]
    public void DiffersFromSealedOrder_UnchangedPayload_ReturnsFalse()
    {
        var clientId = Guid.NewGuid();
        var sealedOrder = ImportedOrderShiftMapper.BuildDraft(BuildPayload(), clientId);

        ImportedOrderShiftMapper.DiffersFromSealedOrder(sealedOrder, BuildPayload(), clientId).ShouldBeFalse();
    }
}

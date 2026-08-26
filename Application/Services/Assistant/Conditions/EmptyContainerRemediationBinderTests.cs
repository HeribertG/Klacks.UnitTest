// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the empty_container parameter binder. They run against the payload as it actually arrives
/// - serialized through JSON and back, so the values are JsonElement - because that round trip is where
/// this binder would break silently: TimeOnly becomes a string, the weekday list becomes a JSON array,
/// and a JsonElement is not IConvertible, a trap this repository has been caught by before.
///
/// The three unbindable cases matter as much as the happy path: they are what keeps an impossible
/// remediation from costing an attempt and a slot of the daily action budget.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class EmptyContainerRemediationBinderTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateOnly FromDate = new(2026, 9, 1);

    private EmptyContainerRemediationBinder _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new EmptyContainerRemediationBinder();
    }

    [Test]
    public void ContainerRunningWednesdayAndFriday_BindsTheLowestWeekdayAndTheContainersOwnTimes()
    {
        var payload = RoundTrip(Schedule(new TimeOnly(6, 30), new TimeOnly(14, 45), [3, 5]));

        var arguments = _sut.Bind(payload);

        Assert.Multiple(() =>
        {
            Assert.That(arguments[CreateContainerTemplateParameters.ContainerId], Is.EqualTo(ContainerId.ToString()));
            Assert.That(arguments[CreateContainerTemplateParameters.Weekday], Is.EqualTo(3));
            Assert.That(arguments[CreateContainerTemplateParameters.FromTime], Is.EqualTo("06:30"));
            Assert.That(arguments[CreateContainerTemplateParameters.UntilTime], Is.EqualTo("14:45"));
        });
    }

    [Test]
    public void HolidayFlags_AreMirroredFromTheContainer()
    {
        var payload = RoundTrip(Schedule(
            new TimeOnly(8, 0), new TimeOnly(17, 0), [7], isHoliday: true, isWeekdayAndHoliday: true));

        var arguments = _sut.Bind(payload);

        Assert.Multiple(() =>
        {
            Assert.That(arguments[CreateContainerTemplateParameters.Weekday], Is.EqualTo(7));
            Assert.That(arguments[CreateContainerTemplateParameters.IsHoliday], Is.EqualTo(true));
            Assert.That(arguments[CreateContainerTemplateParameters.IsWeekdayAndHoliday], Is.EqualTo(true));
        });
    }

    [Test]
    public void PayloadWrittenBeforeTheScheduleExisted_IsUnbindable()
    {
        var legacyPayload = RoundTripRaw(new Dictionary<string, object?>
        {
            [EmptyContainerPayloadKeys.ShiftId] = ContainerId,
            [EmptyContainerPayloadKeys.ContainerName] = "Container A",
            [EmptyContainerPayloadKeys.FromDate] = FromDate,
            [EmptyContainerPayloadKeys.UntilDate] = null,
            [EmptyContainerPayloadKeys.GroupIds] = Array.Empty<Guid>()
        });

        var arguments = _sut.Bind(legacyPayload);

        AssertUnbindable(
            arguments,
            "An open ledger row keeps the payload it was opened with forever, so every row already open "
            + "when this binder shipped has to be recognisable as unbindable rather than fail at the skill.");
    }

    [Test]
    public void ContainerWithNoWeekdayFlagAtAll_IsUnbindable()
    {
        var payload = RoundTrip(Schedule(new TimeOnly(8, 0), new TimeOnly(17, 0), []));

        var arguments = _sut.Bind(payload);

        AssertUnbindable(arguments, "There is no weekday to write a template for.");
    }

    [Test]
    public void NightContainerCrossingMidnight_IsUnbindable()
    {
        var payload = RoundTrip(Schedule(new TimeOnly(22, 0), new TimeOnly(6, 0), [1]));

        var arguments = _sut.Bind(payload);

        AssertUnbindable(
            arguments,
            "create_container_template refuses untilTime <= fromTime by its own validation. Emitting the "
            + "pair anyway would buy three guaranteed failures and an escalation instead of one skip.");
    }

    private static void AssertUnbindable(IReadOnlyDictionary<string, object?> arguments, string because)
    {
        var missing = CreateContainerTemplateParameters.Required
            .Where(name => !arguments.TryGetValue(name, out var value) || value is null)
            .ToList();

        Assert.That(missing, Is.Not.Empty, because);
    }

    private static ContainerScheduleSnapshot Schedule(
        TimeOnly startShift,
        TimeOnly endShift,
        int[] isoWeekdays,
        bool isHoliday = false,
        bool isWeekdayAndHoliday = false) =>
        new(startShift, endShift, isoWeekdays, isHoliday, isWeekdayAndHoliday);

    private static IReadOnlyDictionary<string, object?> RoundTrip(ContainerScheduleSnapshot schedule) =>
        RoundTripRaw(new EmptyContainerTriggerEvent(
            ContainerId, "Container A", FromDate, null, Array.Empty<Guid>(), schedule).Payload);

    /// <summary>
    /// Serializes and deserializes exactly the way the tick does, so the binder sees JsonElement values
    /// rather than the CLR objects the event produced.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> RoundTripRaw(IReadOnlyDictionary<string, object?> payload) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(payload))!;
}

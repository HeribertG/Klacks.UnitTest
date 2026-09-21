// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetProactiveMessagesQueryHandler — verifies mapping of dispatch rows to inbox
/// DTOs (content key, deserialized params, severity, trigger kind, action route and params,
/// reaction, timestamps), the empty-params fallback for missing or invalid JSON, the null action
/// params for missing or invalid JSON, and normalization of the take parameter to the configured
/// default and maximum.
///
/// Also pins the live re-rendering of the content params: a row reporting a STILL-OPEN ledger row states
/// that row's current count and names instead of the copy frozen at first delivery (the aggregated
/// findings write their dispatch row exactly once, so the frozen count is the count of the day the gap
/// was first detected), while a closed or missing condition and a row without one keep the frozen params
/// untouched. The whole page is resolved with ONE ledger read — the inbox is polled, so a per-row lookup
/// would be a query per listed message.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetProactiveMessagesQueryHandlerTests
{
    private const string UserId = "user-a";

    private const string FrozenParamsJson = """{"count":"12","names":"Ann"}""";

    private const string LivePayloadJson =
        """{"count":40,"names":"Ann, Bob","clients":[{"ClientName":"Ann"}],"missingField":"address"}""";

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private GetProactiveMessagesQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _conditionRepository
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());
        _sut = new GetProactiveMessagesQueryHandler(_dispatchRepository, _conditionRepository);
    }

    private static ProactiveTriggerDispatchRow MakeRow(string? contentParamsJson = null, string? actionRoute = null, string? actionParamsJson = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        TriggerKind = "unstaffed_shift",
        DedupKey = "dedup-key",
        ContentKey = "i18n:proactive.unstaffedShift",
        ContentParamsJson = contentParamsJson,
        Severity = "medium",
        ActionRoute = actionRoute,
        ActionParamsJson = actionParamsJson,
        Reaction = ProactiveReaction.Helpful,
        CreateTime = new DateTime(2026, 7, 24, 10, 0, 0, DateTimeKind.Utc),
        ReadAtUtc = new DateTime(2026, 7, 24, 11, 0, 0, DateTimeKind.Utc)
    };

    private void SetupRows(params ProactiveTriggerDispatchRow[] rows) =>
        _dispatchRepository
            .ListForUserAsync(UserId, Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    private ProactiveTriggerDispatchRow GivenRowReportingCondition(
        AgentConditionStatus status,
        string payloadJson = LivePayloadJson,
        string? contentParamsJson = FrozenParamsJson)
    {
        var row = MakeRow(contentParamsJson: contentParamsJson);
        row.ConditionId = Guid.NewGuid();
        SetupRows(row);
        _conditionRepository
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>
            {
                new() { Id = row.ConditionId.Value, Status = status, PayloadJson = payloadJson }
            });

        return row;
    }

    private async Task<ProactiveInboxMessageDto> HandleSingleAsync()
    {
        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        return result[0];
    }

    [Test]
    public async Task Handle_MapsRowToDto()
    {
        var row = MakeRow("""{"date":"24.07.2026","days":"2"}""");
        SetupRows(row);

        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        var dto = result[0];
        Assert.That(dto.Id, Is.EqualTo(row.Id));
        Assert.That(dto.Content, Is.EqualTo(row.ContentKey));
        Assert.That(dto.ContentParams["date"], Is.EqualTo("24.07.2026"));
        Assert.That(dto.ContentParams["days"], Is.EqualTo("2"));
        Assert.That(dto.Severity, Is.EqualTo(row.Severity));
        Assert.That(dto.Kind, Is.EqualTo(row.TriggerKind));
        Assert.That(dto.Reaction, Is.EqualTo(nameof(ProactiveReaction.Helpful)));
        Assert.That(dto.CreatedUtc, Is.EqualTo(row.CreateTime));
        Assert.That(dto.ReadAtUtc, Is.EqualTo(row.ReadAtUtc));
    }

    [Test]
    public async Task Handle_MapsReminderLoopFields()
    {
        var row = MakeRow();
        row.ReminderCount = 2;
        row.LastRemindedAtUtc = new DateTime(2026, 7, 25, 8, 0, 0, DateTimeKind.Utc);
        row.AcknowledgedAtUtc = new DateTime(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);
        SetupRows(row);

        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        var dto = result[0];
        Assert.That(dto.ReminderCount, Is.EqualTo(2));
        Assert.That(dto.LastRemindedAtUtc, Is.EqualTo(row.LastRemindedAtUtc));
        Assert.That(dto.AcknowledgedAtUtc, Is.EqualTo(row.AcknowledgedAtUtc));
    }

    [Test]
    public async Task Handle_RowWithAction_MapsActionRouteAndParams()
    {
        var row = MakeRow(
            actionRoute: "/workplace/schedule",
            actionParamsJson: """{"groupId":"3e2f6f9a-6f4b-4d47-9d5e-0a1b2c3d4e5f","date":"2026-08-03"}""");
        SetupRows(row);

        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        var dto = result[0];
        Assert.That(dto.ActionRoute, Is.EqualTo("/workplace/schedule"));
        Assert.That(dto.ActionParams, Is.Not.Null);
        Assert.That(dto.ActionParams!["groupId"], Is.EqualTo("3e2f6f9a-6f4b-4d47-9d5e-0a1b2c3d4e5f"));
        Assert.That(dto.ActionParams["date"], Is.EqualTo("2026-08-03"));
    }

    [Test]
    public async Task Handle_RowWithoutAction_YieldsNullActionFields()
    {
        SetupRows(MakeRow());

        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        Assert.That(result[0].ActionRoute, Is.Null);
        Assert.That(result[0].ActionParams, Is.Null);
    }

    [Test]
    public async Task Handle_InvalidActionParamsJson_YieldsNullActionParams()
    {
        SetupRows(MakeRow(actionRoute: "/workplace/schedule", actionParamsJson: "not-json"));

        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        Assert.That(result[0].ActionRoute, Is.EqualTo("/workplace/schedule"));
        Assert.That(result[0].ActionParams, Is.Null);
    }

    [Test]
    public async Task Handle_NullContentParamsJson_YieldsEmptyParams()
    {
        SetupRows(MakeRow(contentParamsJson: null));

        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        Assert.That(result[0].ContentParams, Is.Empty);
    }

    [Test]
    public async Task Handle_InvalidContentParamsJson_YieldsEmptyParams()
    {
        SetupRows(MakeRow(contentParamsJson: "not-json"));

        var result = await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        Assert.That(result[0].ContentParams, Is.Empty);
    }

    [Test]
    public async Task Handle_NoTake_UsesDefaultListTake()
    {
        SetupRows();

        await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId, Take = null }, CancellationToken.None);

        await _dispatchRepository.Received(1).ListForUserAsync(
            UserId, Arg.Any<bool>(), ProactiveInboxDefaults.DefaultListTake, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_TakeAboveMaximum_ClampsToMaxListTake()
    {
        SetupRows();

        await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId, Take = ProactiveInboxDefaults.MaxListTake + 1 }, CancellationToken.None);

        await _dispatchRepository.Received(1).ListForUserAsync(
            UserId, Arg.Any<bool>(), ProactiveInboxDefaults.MaxListTake, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NonPositiveTake_UsesDefaultListTake()
    {
        SetupRows();

        await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId, Take = 0 }, CancellationToken.None);

        await _dispatchRepository.Received(1).ListForUserAsync(
            UserId, Arg.Any<bool>(), ProactiveInboxDefaults.DefaultListTake, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ForwardsUnreadOnlyFlag()
    {
        SetupRows();

        await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId, UnreadOnly = true }, CancellationToken.None);

        await _dispatchRepository.Received(1).ListForUserAsync(
            UserId, true, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // The point of the whole live merge: the aggregated findings write their dispatch row exactly once,
    // so the frozen count states how many people lacked the data on the day the gap was first detected.
    // The ledger row's payload is refreshed on every detector tick, so the open row knows the current
    // number and the list must state that one.
    [Test]
    public async Task Handle_RowReportingAnOpenCondition_StatesTheCurrentCountAndNames()
    {
        GivenRowReportingCondition(AgentConditionStatus.Reported);

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams["count"], Is.EqualTo("40"));
        Assert.That(dto.ContentParams["names"], Is.EqualTo("Ann, Bob"));
    }

    // Nested values are skipped rather than stringified: a raw JSON array in a user-facing sentence is
    // noise, and the rendered name list is what the placeholder wants.
    [Test]
    public async Task Handle_LivePayload_NeverLeaksItsNestedValues()
    {
        GivenRowReportingCondition(AgentConditionStatus.Reported);

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams.ContainsKey("clients"), Is.False);
    }

    // Merged, not replaced: the payload is what the detector captured, the frozen params are what the
    // sentence interpolates, and a placeholder the payload happens not to carry must keep its value
    // instead of rendering as a hole.
    [Test]
    public async Task Handle_LivePayloadCarriesNoValueForAParam_KeepsTheFrozenOne()
    {
        GivenRowReportingCondition(
            AgentConditionStatus.Reported,
            payloadJson: """{"count":40}""",
            contentParamsJson: """{"count":"12","names":"Ann","from":"01.10.2026"}""");

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams["count"], Is.EqualTo("40"));
        Assert.That(dto.ContentParams["names"], Is.EqualTo("Ann"));
        Assert.That(dto.ContentParams["from"], Is.EqualTo("01.10.2026"));
    }

    // A finished finding is a record of what was true when it was handled. Refreshing a resolved row's
    // numbers would restate a closed case with figures nobody acted on.
    [TestCase(AgentConditionStatus.Resolved)]
    [TestCase(AgentConditionStatus.Rejected)]
    [TestCase(AgentConditionStatus.Executed)]
    public async Task Handle_RowReportingAClosedCondition_KeepsTheFrozenParams(AgentConditionStatus status)
    {
        GivenRowReportingCondition(status);

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams["count"], Is.EqualTo("12"));
        Assert.That(dto.ContentParams["names"], Is.EqualTo("Ann"));
    }

    [Test]
    public async Task Handle_ConditionNoLongerExists_KeepsTheFrozenParams()
    {
        var row = MakeRow(contentParamsJson: FrozenParamsJson);
        row.ConditionId = Guid.NewGuid();
        SetupRows(row);

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams["count"], Is.EqualTo("12"));
    }

    [Test]
    public async Task Handle_RowWithoutConditionId_NeverReadsTheLedger()
    {
        SetupRows(MakeRow(contentParamsJson: FrozenParamsJson));

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams["count"], Is.EqualTo("12"));
        await _conditionRepository.DidNotReceiveWithAnyArgs().GetByIdsAsync(default!, default);
    }

    [Test]
    public async Task Handle_BrokenLivePayload_KeepsTheFrozenParams()
    {
        GivenRowReportingCondition(AgentConditionStatus.Reported, payloadJson: "not json at all");

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams["count"], Is.EqualTo("12"));
        Assert.That(dto.ContentParams["names"], Is.EqualTo("Ann"));
    }

    // The inbox is the most frequently polled assistant read there is, so the page resolves its ledger
    // rows in ONE query no matter how many messages it lists - and asks for each condition once even
    // when several rows report the same finding.
    [Test]
    public async Task Handle_SeveralRowsReportingConditions_ReadsTheLedgerOnceWithDistinctIds()
    {
        var sharedConditionId = Guid.NewGuid();
        var otherConditionId = Guid.NewGuid();
        var first = MakeRow(contentParamsJson: FrozenParamsJson);
        first.ConditionId = sharedConditionId;
        var second = MakeRow(contentParamsJson: FrozenParamsJson);
        second.ConditionId = sharedConditionId;
        var third = MakeRow(contentParamsJson: FrozenParamsJson);
        third.ConditionId = otherConditionId;
        var fourth = MakeRow(contentParamsJson: FrozenParamsJson);
        SetupRows(first, second, third, fourth);

        await _sut.Handle(new GetProactiveMessagesQuery { UserId = UserId }, CancellationToken.None);

        await _conditionRepository.Received(1).GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids =>
                ids.Count == 2
                && ids.Contains(sharedConditionId)
                && ids.Contains(otherConditionId)),
            Arg.Any<CancellationToken>());
    }

    // The frozen params are capped per value before they are stored; the payload column has no width of
    // its own. A live value must therefore obey the same cap, or a row whose payload repeats the frozen
    // value verbatim (OrderImportFailedTriggerEvent carries the same exception message in both) would
    // hand the list a longer string than it could ever have stored.
    [Test]
    public async Task Handle_OverlongLiveValue_IsCappedLikeAFrozenOne()
    {
        var overlong = new string('x', ProactiveTriggerDispatchLimits.ContentParamValueMaxLength + 500);
        GivenRowReportingCondition(
            AgentConditionStatus.Reported,
            payloadJson: JsonSerializer.Serialize(new Dictionary<string, string> { ["reason"] = overlong }),
            contentParamsJson: """{"reason":"short"}""");

        var dto = await HandleSingleAsync();

        Assert.That(
            dto.ContentParams["reason"].Length,
            Is.EqualTo(ProactiveTriggerDispatchLimits.ContentParamValueMaxLength));
        Assert.That(
            dto.ContentParams["reason"],
            Does.EndWith(ProactiveTriggerDispatchLimits.TruncationSuffix));
    }

    // The action params address the route the user lands on. A payload key sharing a name with one of
    // them would silently redirect the click, so the merge must never reach them.
    [Test]
    public async Task Handle_LivePayload_NeverLeaksIntoTheActionParams()
    {
        var row = MakeRow(
            contentParamsJson: FrozenParamsJson,
            actionRoute: "/workplace/schedule",
            actionParamsJson: """{"count":"12"}""");
        row.ConditionId = Guid.NewGuid();
        SetupRows(row);
        _conditionRepository
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>
            {
                new()
                {
                    Id = row.ConditionId.Value,
                    Status = AgentConditionStatus.Reported,
                    PayloadJson = LivePayloadJson
                }
            });

        var dto = await HandleSingleAsync();

        Assert.That(dto.ContentParams["count"], Is.EqualTo("40"));
        Assert.That(dto.ActionParams, Is.Not.Null);
        Assert.That(dto.ActionParams!["count"], Is.EqualTo("12"));
    }
}

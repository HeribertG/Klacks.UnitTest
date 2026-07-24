// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetProactiveMessagesQueryHandler — verifies mapping of dispatch rows to inbox
/// DTOs (content key, deserialized params, severity, trigger kind, action route and params,
/// reaction, timestamps), the empty-params fallback for missing or invalid JSON, the null action
/// params for missing or invalid JSON, and normalization of the take parameter to the configured
/// default and maximum.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetProactiveMessagesQueryHandlerTests
{
    private const string UserId = "user-a";

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private GetProactiveMessagesQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new GetProactiveMessagesQueryHandler(_dispatchRepository);
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
}

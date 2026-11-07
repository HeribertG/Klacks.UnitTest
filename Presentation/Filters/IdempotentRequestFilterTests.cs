// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Idempotency-Key filter of the creating endpoints: without the header nothing changes; a repeated key of the
/// same user and route runs the action once and replays its answer, also while the first request is still running; other
/// users, other routes and another body under the same key are kept apart; a malformed key is refused with 400; failures
/// and empty answers are not kept, so a repeat after them runs the action again. Over the per-user cap and with a full store
/// the action runs as without the header, and a waiter whose request is aborted gives up while the owner completes.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Constants;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Klacks.UnitTest.Presentation.Filters;

[TestFixture]
public class IdempotentRequestFilterTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";
    private const string WorksPath = "/api/backend/Works";
    private const string BreaksPath = "/api/backend/Breaks";

    private IdempotentRequestStore _store = null!;
    private IdempotentRequestFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new IdempotentRequestStore();
        _filter = new IdempotentRequestFilter(_store);
    }

    [TearDown]
    public void TearDown()
    {
        _store.Dispose();
    }

    [Test]
    public async Task WithoutHeader_RunsTheActionEveryTime_AndLeavesTheResultAlone()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));

        var first = await RunAsync(action, UserA, WorksPath);
        var second = await RunAsync(action, UserA, WorksPath);

        action.Calls.ShouldBe(2);
        first.Result.ShouldBeNull();
        second.Result.ShouldBeNull();
    }

    [Test]
    public async Task SameKeyTwice_RunsTheActionOnce_AndReplaysTheSameAnswer()
    {
        var created = new object();
        var action = new CountingAction(() => new OkObjectResult(created));
        var key = NewKey();

        var first = await RunAsync(action, UserA, WorksPath, key);
        var second = await RunAsync(action, UserA, WorksPath, key);

        action.Calls.ShouldBe(1);
        first.Result.ShouldBeNull();
        var replayed = second.Result.ShouldBeOfType<ObjectResult>();
        replayed.StatusCode.ShouldBe(StatusCodes.Status200OK);
        replayed.Value.ShouldBeSameAs(created);
    }

    [Test]
    public async Task SameKeyInAnotherSpelling_IsTheSameKey()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));
        var key = NewKey();

        await RunAsync(action, UserA, WorksPath, key);
        await RunAsync(action, UserA, WorksPath, key.ToUpperInvariant());

        action.Calls.ShouldBe(1);
    }

    [Test]
    public async Task SameKeyWhileTheFirstStillRuns_WaitsForIt_AndRunsTheActionOnce()
    {
        var created = new object();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = new CountingAction(async () =>
        {
            started.TrySetResult();
            await release.Task;
            return new OkObjectResult(created);
        });
        var key = NewKey();

        var firstRun = RunAsync(action, UserA, WorksPath, key);
        await started.Task;
        var secondRun = RunAsync(action, UserA, WorksPath, key);
        await Task.Delay(50);
        secondRun.IsCompleted.ShouldBeFalse();

        release.SetResult();
        await Task.WhenAll(firstRun, secondRun);

        action.Calls.ShouldBe(1);
        (await secondRun).Result.ShouldBeOfType<ObjectResult>().Value.ShouldBeSameAs(created);
    }

    [Test]
    public async Task SameKeyWhileTheFirstStillRuns_AndTheFirstFails_RunsTheActionForTheWaiter()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new object();
        var action = new CountingAction(async () =>
        {
            if (started.TrySetResult())
            {
                await release.Task;
                return new ObjectResult(new ProblemDetails()) { StatusCode = StatusCodes.Status409Conflict };
            }

            return new OkObjectResult(created);
        });
        var key = NewKey();

        var firstRun = RunAsync(action, UserA, WorksPath, key);
        await started.Task;
        var secondRun = RunAsync(action, UserA, WorksPath, key);

        release.SetResult();
        await Task.WhenAll(firstRun, secondRun);

        action.Calls.ShouldBe(2);
        (await secondRun).Result.ShouldBeNull();
    }

    [Test]
    public async Task SameKeyOfAnotherUser_RunsTheActionAgain()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));
        var key = NewKey();

        await RunAsync(action, UserA, WorksPath, key);
        var other = await RunAsync(action, UserB, WorksPath, key);

        action.Calls.ShouldBe(2);
        other.Result.ShouldBeNull();
    }

    [Test]
    public async Task SameKeyOnAnotherRoute_RunsTheActionAgain()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));
        var key = NewKey();

        await RunAsync(action, UserA, WorksPath, key);
        await RunAsync(action, UserA, BreaksPath, key);

        action.Calls.ShouldBe(2);
    }

    [Test]
    public async Task SameKeyWithAnotherBody_RunsTheActionAgain()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));
        var key = NewKey();

        await RunAsync(action, UserA, WorksPath, [key], body: new Body("client-1"));
        await RunAsync(action, UserA, WorksPath, [key], body: new Body("client-1"));
        var changed = await RunAsync(action, UserA, WorksPath, [key], body: new Body("client-2"));

        action.Calls.ShouldBe(2);
        changed.Result.ShouldBeNull();
    }

    [Test]
    public async Task OverThePerUserCap_RunsTheActionAsWithoutHeader_WhileOtherUsersAreStillKept()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));
        for (var i = 0; i < IdempotentRequestConstants.MaxEntriesPerUser; i++)
        {
            await RunAsync(action, UserA, WorksPath, NewKey());
        }

        var overCap = NewKey();
        await RunAsync(action, UserA, WorksPath, overCap);
        await RunAsync(action, UserA, WorksPath, overCap);
        await RunAsync(action, UserB, WorksPath, overCap);
        await RunAsync(action, UserB, WorksPath, overCap);

        action.Calls.ShouldBe(IdempotentRequestConstants.MaxEntriesPerUser + 3);
    }

    [Test]
    public async Task WithAFullStore_RunsTheActionAsWithoutHeader()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));
        var users = IdempotentRequestConstants.MaxStoredResponses / IdempotentRequestConstants.MaxEntriesPerUser;
        for (var user = 0; user < users; user++)
        {
            for (var i = 0; i < IdempotentRequestConstants.MaxEntriesPerUser; i++)
            {
                await RunAsync(action, $"filler-{user}", WorksPath, NewKey());
            }
        }

        var key = NewKey();
        var first = await RunAsync(action, UserA, WorksPath, key);
        var second = await RunAsync(action, UserA, WorksPath, key);

        action.Calls.ShouldBe(IdempotentRequestConstants.MaxStoredResponses + 2);
        first.Result.ShouldBeNull();
        second.Result.ShouldBeNull();
    }

    [Test]
    public async Task WaiterWhoseRequestIsAborted_GivesUp_AndTheOwnerStillCompletes()
    {
        var created = new object();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = new CountingAction(async () =>
        {
            started.TrySetResult();
            await release.Task;
            return new OkObjectResult(created);
        });
        var key = NewKey();
        using var aborted = new CancellationTokenSource();

        var ownerRun = RunAsync(action, UserA, WorksPath, [key]);
        await started.Task;
        var waiterRun = RunAsync(action, UserA, WorksPath, [key], requestAborted: aborted.Token);
        await aborted.CancelAsync();

        Exception? thrown = null;
        try
        {
            await waiterRun;
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        thrown.ShouldBeAssignableTo<OperationCanceledException>();
        release.SetResult();
        (await ownerRun).Result.ShouldBeNull();
        var replay = await RunAsync(action, UserA, WorksPath, key);

        action.Calls.ShouldBe(1);
        replay.Result.ShouldBeOfType<ObjectResult>().Value.ShouldBeSameAs(created);
    }

    [Test]
    public async Task WithoutAuthenticatedUser_RunsTheActionEveryTime()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));
        var key = NewKey();

        await RunAsync(action, null, WorksPath, key);
        await RunAsync(action, null, WorksPath, key);

        action.Calls.ShouldBe(2);
    }

    [TestCase("")]
    [TestCase("not-a-guid")]
    [TestCase("3f2504e04f8941d39a0c0305e82c3301")]
    [TestCase("{3f2504e0-4f89-41d3-9a0c-0305e82c3301}")]
    [TestCase("3f2504e0-4f89-41d3-9a0c-0305e82c3301-3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    public async Task MalformedKey_IsRefusedWith400_WithoutRunningTheAction(string key)
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));

        var context = await RunAsync(action, UserA, WorksPath, key);

        AssertRefused(context, action);
    }

    [Test]
    public async Task TwoKeys_AreRefusedWith400_WithoutRunningTheAction()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()));

        var context = await RunAsync(action, UserA, WorksPath, NewKey(), NewKey());

        AssertRefused(context, action);
    }

    [Test]
    public async Task RefusedAnswer_IsNotKept_SoTheRepeatRunsTheActionAgain()
    {
        var action = new CountingAction(() =>
            new ObjectResult(new ProblemDetails()) { StatusCode = StatusCodes.Status409Conflict });
        var key = NewKey();

        await RunAsync(action, UserA, WorksPath, key);
        var second = await RunAsync(action, UserA, WorksPath, key);

        action.Calls.ShouldBe(2);
        second.Result.ShouldBeNull();
    }

    [Test]
    public async Task ThrownException_IsNotKept_SoTheRepeatRunsTheActionAgain()
    {
        var action = new CountingAction(() => new OkObjectResult(new object()), new InvalidOperationException("boom"));
        var key = NewKey();

        await RunAsync(action, UserA, WorksPath, key);
        await RunAsync(action, UserA, WorksPath, key);

        action.Calls.ShouldBe(2);
    }

    [Test]
    public async Task SuccessWithoutBody_IsNotKept_SoTheRepeatRunsTheActionAgain()
    {
        var action = new CountingAction(() => new OkObjectResult(null));
        var key = NewKey();

        await RunAsync(action, UserA, WorksPath, key);
        await RunAsync(action, UserA, WorksPath, key);

        action.Calls.ShouldBe(2);
    }

    private static void AssertRefused(ActionExecutingContext context, CountingAction action)
    {
        var refusal = context.Result.ShouldBeOfType<ObjectResult>();
        refusal.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        refusal.Value.ShouldBeOfType<ProblemDetails>().Detail.ShouldBe(IdempotentRequestConstants.InvalidKeyMessage);
        action.Calls.ShouldBe(0);
    }

    private static string NewKey() => Guid.NewGuid().ToString("D");

    private Task<ActionExecutingContext> RunAsync(
        CountingAction action,
        string? userId,
        string path,
        params string[] keys) => RunAsync(action, userId, path, keys, body: null);

    private async Task<ActionExecutingContext> RunAsync(
        CountingAction action,
        string? userId,
        string path,
        string[] keys,
        Body? body = null,
        CancellationToken requestAborted = default)
    {
        var httpContext = new DefaultHttpContext { RequestAborted = requestAborted };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = path;
        if (keys.Length > 0)
        {
            httpContext.Request.Headers[HttpHeaderNames.IdempotencyKey] = keys;
        }

        if (userId != null)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test"));
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var arguments = new Dictionary<string, object?>();
        if (body != null)
        {
            arguments["resource"] = body;
        }

        var context = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), arguments, controller: new object());

        await _filter.OnActionExecutionAsync(context, () => action.InvokeAsync(actionContext));

        return context;
    }

    private sealed record Body(string ClientId);

    private sealed class CountingAction
    {
        private readonly Func<Task<IActionResult>> _produce;
        private readonly Exception? _exception;
        private int _calls;

        public CountingAction(Func<IActionResult> produce, Exception? exception = null)
            : this(() => Task.FromResult(produce()), exception)
        {
        }

        public CountingAction(Func<Task<IActionResult>> produce, Exception? exception = null)
        {
            _produce = produce;
            _exception = exception;
        }

        public int Calls => _calls;

        public async Task<ActionExecutedContext> InvokeAsync(ActionContext actionContext)
        {
            Interlocked.Increment(ref _calls);
            var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object());
            if (_exception != null)
            {
                executed.Exception = _exception;
                return executed;
            }

            executed.Result = await _produce();
            return executed;
        }
    }
}

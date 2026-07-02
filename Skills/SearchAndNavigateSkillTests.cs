// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SearchAndNavigateSkill — verifies that a single search hit produces the
/// correct /workplace/... route, since the frontend only navigates to /workplace/ routes.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SearchAndNavigateSkillTests
{
    private IClientSearchRepository _clientSearch = null!;
    private IGroupSearchRepository _groupSearch = null!;
    private IShiftSearchRepository _shiftSearch = null!;
    private SearchAndNavigateSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _clientSearch = Substitute.For<IClientSearchRepository>();
        _groupSearch = Substitute.For<IGroupSearchRepository>();
        _shiftSearch = Substitute.For<IShiftSearchRepository>();
        _skill = new SearchAndNavigateSkill(_clientSearch, _groupSearch, _shiftSearch);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { "Admin" }
        };
    }

    private static string? RouteOf(SkillResult result)
    {
        var json = JsonSerializer.Serialize(result.Data);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("Route", out var route) ? route.GetString() : null;
    }

    [Test]
    public async Task ExecuteAsync_SingleClient_NavigatesToWorkplaceEditAddress()
    {
        var id = Guid.NewGuid();
        _clientSearch.SearchAsync(searchTerm: "Heribert", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                TotalCount = 1,
                Items = new[]
                {
                    new ClientSearchItem { Id = id, FirstName = "Heribert", LastName = "Gasparoli" }
                }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "client" },
            { "searchQuery", "Heribert" }
        });

        result.Type.ShouldBe(SkillResultType.Navigation);
        RouteOf(result).ShouldBe($"/workplace/edit-address/{id}");
    }

    [Test]
    public async Task ExecuteAsync_SingleGroup_NavigatesToWorkplaceEditGroup()
    {
        var id = Guid.NewGuid();
        _groupSearch.SearchAsync(searchTerm: "Bern", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult
            {
                TotalCount = 1,
                Items = new[] { new GroupSearchItem { Id = id, Name = "Bern" } }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "group" },
            { "searchQuery", "Bern" }
        });

        RouteOf(result).ShouldBe($"/workplace/edit-group/{id}");
    }

    [Test]
    public async Task ExecuteAsync_MultipleClients_MessageGuidesReQueryByIdNumber_NeverAsEntityId()
    {
        _clientSearch.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                TotalCount = 2,
                Items = new[]
                {
                    new ClientSearchItem { Id = Guid.NewGuid(), IdNumber = 6556, FirstName = "Heribert", LastName = "Gasparoli" },
                    new ClientSearchItem { Id = Guid.NewGuid(), IdNumber = 7001, FirstName = "Heribert", LastName = "Gasparoli" }
                }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "client" },
            { "searchQuery", "Heribert Gasparoli" }
        });

        result.Type.ShouldBe(SkillResultType.Data);
        result.Message.ShouldContain("search_and_navigate");
        result.Message.ShouldContain("IdNumber");
        result.Message.ShouldContain("Never pass the IdNumber");
    }

    [Test]
    public async Task ExecuteAsync_SingleClientWithTarget_IncludesTargetInNavigationData()
    {
        var id = Guid.NewGuid();
        _clientSearch.SearchAsync(searchTerm: "Heribert", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                TotalCount = 1,
                Items = new[] { new ClientSearchItem { Id = id, FirstName = "Heribert", LastName = "Gasparoli" } }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "client" },
            { "searchQuery", "Heribert" },
            { "target", "address-note" }
        });

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"Target\":\"address-note\"");
    }

    [Test]
    public async Task ExecuteAsync_MultipleClients_CandidateListDoesNotExposeCompanyField()
    {
        _clientSearch.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                TotalCount = 2,
                Items = new[]
                {
                    new ClientSearchItem { Id = Guid.NewGuid(), IdNumber = 6556, FirstName = "Heribert", LastName = "Gasparoli", Company = null },
                    new ClientSearchItem { Id = Guid.NewGuid(), IdNumber = 7001, FirstName = "Heribert", LastName = "Gasparoli", Company = null }
                }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "client" },
            { "searchQuery", "Heribert Gasparoli" }
        });

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldNotContain("Company");
        result.Message.ShouldContain("do not compare or mention any other field");
    }

    [Test]
    public async Task ExecuteAsync_SingleShiftWithoutClient_NavigatesToWorkplaceEditShift()
    {
        var id = Guid.NewGuid();
        _shiftSearch.SearchAsync(searchTerm: "FS", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ShiftSearchResult
            {
                TotalCount = 1,
                Items = new[]
                {
                    new ShiftSearchItem
                    {
                        Id = id,
                        Abbreviation = "FS",
                        Name = "Frühschicht",
                        Status = ShiftStatus.OriginalShift,
                        ClientFirstName = null,
                        ClientLastName = null
                    }
                }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "shift" },
            { "searchQuery", "FS" }
        });

        result.Type.ShouldBe(SkillResultType.Navigation);
        RouteOf(result).ShouldBe($"/workplace/edit-shift/{id}");
        result.Message.ShouldContain("Frühschicht (FS)");
        result.Message.ShouldContain("explain_shift_lifecycle_order_to_shift");
    }

    [Test]
    public async Task ExecuteAsync_SingleShiftAsOriginalOrder_NoLockGuidanceInMessage()
    {
        var id = Guid.NewGuid();
        _shiftSearch.SearchAsync(searchTerm: "FS", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ShiftSearchResult
            {
                TotalCount = 1,
                Items = new[]
                {
                    new ShiftSearchItem
                    {
                        Id = id,
                        Abbreviation = "FS",
                        Name = "Frühschicht",
                        Status = ShiftStatus.OriginalOrder,
                        ClientFirstName = null,
                        ClientLastName = null
                    }
                }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "shift" },
            { "searchQuery", "FS" }
        });

        result.Message.ShouldNotContain("explain_shift_lifecycle_order_to_shift");
    }

    [Test]
    public async Task ExecuteAsync_RouteAlwaysStartsWithWorkplace()
    {
        var id = Guid.NewGuid();
        _clientSearch.SearchAsync(searchTerm: "x", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                TotalCount = 1,
                Items = new[] { new ClientSearchItem { Id = id, FirstName = "X", LastName = "Y" } }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "client" },
            { "searchQuery", "x" }
        });

        RouteOf(result).ShouldNotBeNull();
        RouteOf(result)!.ShouldStartWith("/workplace/");
    }
}

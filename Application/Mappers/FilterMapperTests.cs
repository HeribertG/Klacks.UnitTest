using Shouldly;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Results;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Application.DTOs.Settings;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Mappers;

[TestFixture]
public class FilterMapperTests
{
    private FilterMapper _mapper = null!;

    [SetUp]
    public void Setup()
    {
        _mapper = new FilterMapper();
    }

    #region ToClientSearchCriteria Tests

    [Test]
    public void ToClientSearchCriteria_BasicFilter_MapsPaginationProperties()
    {
        // Arrange
        var filter = new FilterResource
        {
            FirstItemOnLastPage = 10,
            IsNextPage = true,
            IsPreviousPage = false,
            NumberOfItemsPerPage = 20,
            OrderBy = "name",
            RequiredPage = 2,
            SortOrder = "asc",
            SearchString = "test"
        };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.ShouldNotBeNull();
        result.FirstItemOnLastPage.ShouldBe(10);
        result.IsNextPage.ShouldBe(true);
        result.IsPreviousPage.ShouldBe(false);
        result.NumberOfItemsPerPage.ShouldBe(20);
        result.OrderBy.ShouldBe("name");
        result.RequiredPage.ShouldBe(2);
        result.SortOrder.ShouldBe("asc");
        result.SearchString.ShouldBe("test");
    }

    [Test]
    public void ToClientSearchCriteria_MaleFilter_MapsGenderCorrectly()
    {
        // Arrange
        var filter = new FilterResource { Male = true };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.Gender.ShouldBe(GenderEnum.Male);
    }

    [Test]
    public void ToClientSearchCriteria_FemaleFilter_MapsGenderCorrectly()
    {
        // Arrange
        var filter = new FilterResource { Female = true };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.Gender.ShouldBe(GenderEnum.Female);
    }

    [Test]
    public void ToClientSearchCriteria_LegalEntityFilter_MapsGenderCorrectly()
    {
        // Arrange
        var filter = new FilterResource { LegalEntity = true };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.Gender.ShouldBe(GenderEnum.LegalEntity);
    }

    [Test]
    public void ToClientSearchCriteria_NoGenderFilter_ReturnsNull()
    {
        // Arrange
        var filter = new FilterResource();

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.Gender.ShouldBeNull();
    }

    [Test]
    public void ToClientSearchCriteria_HomeAddressFilter_MapsAddressTypeCorrectly()
    {
        // Arrange
        var filter = new FilterResource { HomeAddress = true };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.AddressType.ShouldBe(AddressTypeEnum.Workplace);
    }

    [Test]
    public void ToClientSearchCriteria_CompanyAddressFilter_MapsAddressTypeCorrectly()
    {
        // Arrange
        var filter = new FilterResource { CompanyAddress = true };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.AddressType.ShouldBe(AddressTypeEnum.InvoicingAddress);
    }

    [Test]
    public void ToClientSearchCriteria_MembershipFilters_MapsCorrectly()
    {
        // Arrange
        var filter = new FilterResource
        {
            ActiveMembership = true,
            FormerMembership = false,
            FutureMembership = true,
            ScopeFrom = new DateTime(2024, 1, 1),
            ScopeUntil = new DateTime(2024, 12, 31)
        };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.IsActiveMember.ShouldBe(true);
        result.IsFormerMember.ShouldBe(false);
        result.IsFutureMember.ShouldBe(true);
        result.MembershipStartDate.ShouldBe(new DateOnly(2024, 1, 1));
        result.MembershipEndDate.ShouldBe(new DateOnly(2024, 12, 31));
    }

    [Test]
    public void ToClientSearchCriteria_NullScopeFrom_ReturnsNullMembershipStartDate()
    {
        // Arrange
        var filter = new FilterResource { ScopeFrom = null };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.MembershipStartDate.ShouldBeNull();
    }

    [Test]
    public void ToClientSearchCriteria_HasAnnotation_MapsCorrectly()
    {
        // Arrange
        var filter = new FilterResource { HasAnnotation = true };

        // Act
        var result = _mapper.ToClientSearchCriteria(filter);

        // Assert
        result.HasAnnotation.ShouldBe(true);
    }

    #endregion

    #region ToClientFilter Tests

    [Test]
    public void ToClientFilter_WithStateTokens_MapsFilteredCantons()
    {
        // Arrange
        var filter = new FilterResource
        {
            List = new List<StateCountryToken>
            {
                new StateCountryToken { Id = Guid.NewGuid(), State = "ZH", Country = "CH", Select = true },
                new StateCountryToken { Id = Guid.NewGuid(), State = "BE", Country = "CH", Select = true }
            },
            Countries = new List<CountryResource>
            {
                new CountryResource { Abbreviation = "CH" },
                new CountryResource { Abbreviation = "DE" }
            }
        };

        // Act
        var result = _mapper.ToClientFilter(filter);

        // Assert
        result.FilteredCantons.Count().ShouldBe(2);
        result.FilteredCantons.ShouldContain("ZH");
        result.FilteredCantons.ShouldContain("BE");
        result.Countries.Count().ShouldBe(2);
        result.Countries.ShouldContain("CH");
        result.Countries.ShouldContain("DE");
        result.FilteredStateToken.Count().ShouldBe(2);
    }

    #endregion

    #region ToBreakFilter Tests

    [Test]
    public void ToBreakFilter_ValidFilter_MapsCorrectly()
    {
        // Arrange
        var filter = new Klacks.Api.Domain.DTOs.Filter.BreakFilter
        {
            SearchString = "test",
            CurrentYear = 2024,
            OrderBy = "date",
            SortOrder = "desc"
        };

        // Act
        var result = _mapper.ToBreakFilter(filter);

        // Assert
        result.ShouldNotBeNull();
        result.SearchString.ShouldBe("test");
        result.CurrentYear.ShouldBe(2024);
        result.OrderBy.ShouldBe("date");
        result.SortOrder.ShouldBe("desc");
    }

    #endregion

    #region ToPaginationParams Tests

    [Test]
    public void ToPaginationParams_ValidFilter_MapsCorrectly()
    {
        // Arrange
        var filter = new BaseFilter
        {
            RequiredPage = 3,
            NumberOfItemsPerPage = 25,
            OrderBy = "name",
            SortOrder = "desc"
        };

        // Act
        var result = _mapper.ToPaginationParams(filter);

        // Assert
        result.ShouldNotBeNull();
        result.PageIndex.ShouldBe(3);
        result.PageSize.ShouldBe(25);
        result.SortBy.ShouldBe("name");
        result.IsDescending.ShouldBeTrue();
    }

    [Test]
    public void ToPaginationParams_ZeroPageSize_DefaultsTo20()
    {
        // Arrange
        var filter = new BaseFilter
        {
            RequiredPage = 1,
            NumberOfItemsPerPage = 0
        };

        // Act
        var result = _mapper.ToPaginationParams(filter);

        // Assert
        result.PageSize.ShouldBe(20);
    }

    [Test]
    public void ToPaginationParams_AscSortOrder_IsDescendingFalse()
    {
        // Arrange
        var filter = new BaseFilter
        {
            SortOrder = "asc"
        };

        // Act
        var result = _mapper.ToPaginationParams(filter);

        // Assert
        result.IsDescending.ShouldBeFalse();
    }

    #endregion

    #region ToBaseTruncatedResult Tests

    [Test]
    public void ToBaseTruncatedResult_ValidPagedResult_MapsCorrectly()
    {
        // Arrange
        var summaries = new List<ClientSummary>
        {
            new ClientSummary { FirstName = "John" },
            new ClientSummary { FirstName = "Jane" }
        };

        var pagedResult = new PagedResult<ClientSummary>
        {
            Items = summaries,
            TotalCount = 100,
            PageNumber = 2,
            PageSize = 10
        };

        // Act
        var result = _mapper.ToBaseTruncatedResult(pagedResult);

        // Assert
        result.ShouldNotBeNull();
        result.MaxItems.ShouldBe(100);
        result.MaxPages.ShouldBe(10);
        result.CurrentPage.ShouldBe(2);
        result.FirstItemOnPage.ShouldBe(11);
    }

    [Test]
    public void ToBaseTruncatedResult_FirstPage_FirstItemOnPageIsOne()
    {
        // Arrange
        var pagedResult = new PagedResult<ClientSummary>
        {
            Items = new List<ClientSummary>(),
            TotalCount = 50,
            PageNumber = 1,
            PageSize = 10
        };

        // Act
        var result = _mapper.ToBaseTruncatedResult(pagedResult);

        // Assert
        result.FirstItemOnPage.ShouldBe(1);
    }

    #endregion
}

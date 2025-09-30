using AutoMapper;
using Klacks.Api.Application.AutoMapper;
using Klacks.Api.Presentation.DTOs.Filter;
using Klacks.Api.Domain.Models.Filters;
using NUnit.Framework;

namespace UnitTest.Mapping
{
    [TestFixture]
    public class AutoMapperConfigTest
    {
        private IMapper _mapper = null!;

        [SetUp]
        public void Setup()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.AddMaps(typeof(ClientMappingProfile).Assembly);
            });

            _mapper = config.CreateMapper();
        }

        [Test]
        public void AutoMapper_Configuration_IsValid()
        {
            _mapper.ConfigurationProvider.AssertConfigurationIsValid();
        }

        [Test]
        public void FilterResource_To_ClientFilter_Mapping_Works()
        {
            var filterResource = new FilterResource
            {
                SearchString = "TestSearch",
                SearchOnlyByName = true,
                ActiveMembership = true,
                OrderBy = "Name",
                SortOrder = "asc"
            };

            var result = _mapper.Map<ClientFilter>(filterResource);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.SearchString, Is.EqualTo("TestSearch"));
            Assert.That(result.SearchOnlyByName, Is.EqualTo(true));
            Assert.That(result.ActiveMembership, Is.EqualTo(true));
            Assert.That(result.OrderBy, Is.EqualTo("Name"));
            Assert.That(result.SortOrder, Is.EqualTo("asc"));
        }

        [Test]
        public void BaseFilter_To_PaginationParams_Mapping_Works()
        {
            var baseFilter = new BaseFilter
            {
                RequiredPage = 2,
                NumberOfItemsPerPage = 25,
                OrderBy = "Name",
                SortOrder = "desc"
            };

            var result = _mapper.Map<PaginationParams>(baseFilter);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.PageIndex, Is.EqualTo(2));
            Assert.That(result.PageSize, Is.EqualTo(25));
            Assert.That(result.SortBy, Is.EqualTo("Name"));
            Assert.That(result.IsDescending, Is.EqualTo(true));
        }
    }
}
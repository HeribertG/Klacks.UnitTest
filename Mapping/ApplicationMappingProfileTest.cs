using AutoMapper;
using Klacks.Api.Application.AutoMapper;

namespace UnitTest.Mapping
{
    public class ApplicationMappingProfileTest
    {
        [Test]
        public void Automapper_Configuration_IsValid()
        {
            var _config = new MapperConfiguration(configure => configure.AddProfile<MappingProfile>());
            _config.AssertConfigurationIsValid();
        }
    }
}

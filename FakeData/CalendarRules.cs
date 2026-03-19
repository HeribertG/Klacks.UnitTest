using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using System.Text.Json;

namespace Klacks.UnitTest.FakeData
{
    internal static class CalendarRules
    {
        internal static List<CalendarRule> CalendarRuleList()
        {
            return JsonSerializer.Deserialize<List<CalendarRule>>(FakeDateSerializeString.Data.calendarRuleList, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }

        internal static CalendarRulesFilter CalendarRulesFilter()
        {
            return JsonSerializer.Deserialize<CalendarRulesFilter>(FakeDateSerializeString.Data.filterCalendarRuleList, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }

        internal static List<Countries> CountryList()
        {
            return JsonSerializer.Deserialize<List<Countries>>(FakeDateSerializeString.Data.countryList, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }

        internal static List<State> StateList()
        {
            return JsonSerializer.Deserialize<List<State>>(FakeDateSerializeString.Data.stateList, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
    }
}

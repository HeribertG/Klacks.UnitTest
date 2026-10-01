// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The import DTOs must travel as the API contract states: camelCase, every enum as its name (targets,
/// date format, name order, status, severity, policy modes, communication types, nullable gender
/// overrides), with the same serializer options the API uses (camelCase naming, no global enum
/// converter).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.ClientImport;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportJsonContractTests
{
    private static readonly JsonSerializerOptions ApiOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Test]
    public void Request_WithStringEnums_IsDeserialized()
    {
        const string json = """
            {
              "token": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
              "fileName": "staff.xlsx",
              "columns": [{ "index": 0, "header": "", "samples": ["Anna"] }],
              "rows": [["Anna"]],
              "mapping": [{ "columnIndex": 0, "target": "FirstName", "confidence": 1 }],
              "dateFormat": "MonthDayYear",
              "nameOrder": "LastFirst",
              "policy": {
                "contractId": null, "groupId": null, "entryDate": "2026-10-01", "defaultCountry": "CH",
                "emailType": "OfficeMail", "phoneType": "OfficeFixPhone", "mobileType": "OfficeCellPhone",
                "formerEmployees": "ImportWithExitDate", "duplicates": "CreateAnyway"
              },
              "rowOverrides": [
                { "rowIndex": 0, "skip": null, "gender": "Intersexuality", "createDuplicate": true },
                { "rowIndex": 1, "skip": true, "gender": null, "createDuplicate": null }
              ]
            }
            """;

        var request = JsonSerializer.Deserialize<ClientImportRequest>(json, ApiOptions)!;

        request.Mapping[0].Target.ShouldBe(ClientImportTarget.FirstName);
        request.DateFormat.ShouldBe(ClientImportDateFormat.MonthDayYear);
        request.NameOrder.ShouldBe(ClientImportNameOrder.LastFirst);
        request.Policy.EmailType.ShouldBe(CommunicationTypeEnum.OfficeMail);
        request.Policy.PhoneType.ShouldBe(CommunicationTypeEnum.OfficeFixPhone);
        request.Policy.MobileType.ShouldBe(CommunicationTypeEnum.OfficeCellPhone);
        request.Policy.FormerEmployees.ShouldBe(ClientImportFormerEmployeesMode.ImportWithExitDate);
        request.Policy.Duplicates.ShouldBe(ClientImportDuplicateMode.CreateAnyway);
        request.RowOverrides[0].Gender.ShouldBe(GenderEnum.Intersexuality);
        request.RowOverrides[1].Gender.ShouldBeNull();
        request.RowOverrides[1].Skip.ShouldBe(true);
    }

    [Test]
    public void PreviewResult_SerializesEnumsAsNames()
    {
        var result = new ClientImportPreviewResult
        {
            Rows =
            [
                new ClientImportPreviewRow
                {
                    Status = ClientImportRowStatus.Skipped,
                    Issues = [new ClientImportIssue { Severity = ClientImportIssueSeverity.Warning, Code = "x" }]
                }
            ],
            IgnoredTargets = [ClientImportTarget.PersonnelNumber]
        };

        var json = JsonSerializer.Serialize(result, ApiOptions);

        json.ShouldContain("\"status\":\"Skipped\"");
        json.ShouldContain("\"severity\":\"Warning\"");
        json.ShouldContain("\"ignoredTargets\":[\"PersonnelNumber\"]");
        json.ShouldContain("\"duplicateOfClientId\":null");
    }

    [Test]
    public void ParseResult_SerializesEnumsAsNames()
    {
        var json = JsonSerializer.Serialize(new ClientImportParseResult
        {
            Mapping = [new ClientImportColumnMapping { Target = ClientImportTarget.ZipCity }],
            DateFormat = ClientImportDateFormat.YearMonthDay,
            NameOrder = ClientImportNameOrder.FirstLast
        }, ApiOptions);

        json.ShouldContain("\"target\":\"ZipCity\"");
        json.ShouldContain("\"dateFormat\":\"YearMonthDay\"");
        json.ShouldContain("\"nameOrder\":\"FirstLast\"");
    }
}

using System.Net;
using System.Text;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.MinSpeiding;

namespace FikenHjelper.Tests;

public sealed class MinSpeidingParserTests
{
    [Fact]
    public void Parse_ValidParticipantsAndQuestions_ResolvesGroupsFeesPricesAndChoices()
    {
        const string participantsJson = """
            {
              "participants": {
                "10": {
                  "member_no": 12345,
                  "first_name": "Ola",
                  "last_name": "Nordmann",
                  "primary_email": "ola@example.no",
                  "fee_id": 101,
                  "cancelled": false,
                  "confirmed": true,
                  "attended": true,
                  "checked_in": false,
                  "primary_membership_info": {
                    "group_id": 55,
                    "group_name": "Molde speidergruppe",
                    "troop_name": "Tropp"
                  },
                  "group_registration_info": {
                    "group_id": 77,
                    "group_name": "Aukra speidergruppe"
                  },
                  "questions": {
                    "5": ["1", "2"],
                    "6": "Telt"
                  },
                  "custom_norway_field": "krets-ok"
                },
                "11": {
                  "member_no": "12346",
                  "first_name": "Kari",
                  "last_name": "Berg",
                  "fee_id": 102,
                  "cancelled": 1,
                  "confirmed": "true",
                  "primary_membership_info": [],
                  "group_registration_info": [],
                  "questions": []
                }
              },
              "labels": {
                "project_fee": {
                  "101": "Speider (450 kr)",
                  "102": "Leder 250,-"
                }
              }
            }
            """;

        const string questionsJson = """
            {
              "forms": {
                "1": {
                  "title": "Kretsleir 2026",
                  "id": 1,
                  "form_type": "group"
                }
              },
              "questions": {
                "5": {
                  "question": "Allergi",
                  "type": "choice",
                  "choices": {
                    "1": { "value": 1, "option": "Gluten" },
                    "2": { "value": 2, "option": "Nøtter" }
                  }
                },
                "6": {
                  "question": "Overnatting",
                  "type": "text"
                }
              }
            }
            """;

        var result = MinSpeidingParser.Parse("999", participantsJson, questionsJson, questionsEndpointAvailable: true);

        Assert.Equal("Kretsleir 2026", result.EventTitle);
        Assert.Equal(2, result.Participants.Count);
        Assert.Contains("custom_norway_field", result.ExtraParticipantPropertyNames);

        var ola = result.Participants.Single(p => p.ParticipantKey == "10");
        Assert.Equal("Aukra speidergruppe", ola.GroupName); // group_registration_info takes priority
        Assert.True(ola.HasMultipleGroups);
        Assert.Equal(2, ola.AvailableGroups.Count);
        Assert.Equal("Aukra speidergruppe", ola.AvailableGroups[0].GroupName);
        Assert.Equal("Molde speidergruppe", ola.AvailableGroups[1].GroupName);
        Assert.Equal(450m, ola.DetectedPriceNok);
        Assert.Equal("Speider (450 kr)", ola.FeeLabel);
        Assert.Equal("Gluten, Nøtter", ola.Answers["Allergi"]);
        Assert.Equal("Telt", ola.Answers["Overnatting"]);

        var kari = result.Participants.Single(p => p.ParticipantKey == "11");
        Assert.Equal("Uten gruppe", kari.GroupName);
        Assert.False(kari.HasMultipleGroups);
        Assert.Empty(kari.AvailableGroups);
        Assert.True(kari.Cancelled);
        Assert.True(kari.Confirmed);
        Assert.Equal(250m, kari.DetectedPriceNok);
    }

    [Fact]
    public void Parse_PhpEmptyArrayParticipants_ReturnsEmptyListGracefully()
    {
        const string json = """
            {
              "participants": [],
              "labels": []
            }
            """;

        var result = MinSpeidingParser.Parse("42", json, null, questionsEndpointAvailable: false);

        Assert.Equal("Arrangement 42", result.EventTitle);
        Assert.Empty(result.Participants);
        Assert.Empty(result.FeeCategories);
    }

    [Theory]
    [InlineData("Speider (450 kr)", 450.0)]
    [InlineData("Leder 250,-", 250.0)]
    [InlineData("kr 375.50", 375.50)]
    [InlineData("Leir 1 200 kr", 1200.0)]
    [InlineData("kr 1 450,50", 1450.5)]
    [InlineData("Gratis", 0.0)]
    [InlineData("Jamboree 2026", null)]
    public void TryExtractPriceNokFromLabel_HandlesNorwegianPriceFormatsWithoutFalsePositivesOnYears(
        string label,
        double? expectedDouble)
    {
        var expected = expectedDouble.HasValue ? (decimal?)TestAmounts.Exact(expectedDouble.Value) : null;
        var actual = MinSpeidingParser.TryExtractPriceNokFromLabel(label);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task MinSpeidingClient_SendsBasicAuthAndContinuesWhenQuestionsEndpointReturns403()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpHandler((req, _) =>
        {
            requests.Add(req);
            if (req.RequestUri!.AbsolutePath.EndsWith("/project/get/participants"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"participants":[],"labels":[]}""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });

        using var http = new HttpClient(handler);
        var store = new LocalConfigStore(dataDirectory: Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N")));
        var client = new MinSpeidingClient(http, store);

        var result = await client.GetEventParticipantsAsync(
            "12345",
            participantsApiKey: "secret-key",
            questionsApiKey: "questions-secret",
            baseUrlOverride: "https://min.speiding.no/api",
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.QuestionsEndpointAvailable);
        Assert.Equal(2, requests.Count);

        var participantsReq = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.EndsWith("/project/get/participants"));
        Assert.Equal("Basic", participantsReq.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("12345:secret-key")), participantsReq.Headers.Authorization?.Parameter);

        var questionsReq = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.EndsWith("/project/get/questions"));
        Assert.Equal("Basic", questionsReq.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("12345:questions-secret")), questionsReq.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task MinSpeidingClient_SkipsQuestionsEndpointWhenQuestionsApiKeyIsEmpty()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpHandler((req, _) =>
        {
            requests.Add(req);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"participants":[],"labels":[]}""", Encoding.UTF8, "application/json")
            });
        });

        using var http = new HttpClient(handler);
        var store = new LocalConfigStore(dataDirectory: Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N")));
        var client = new MinSpeidingClient(http, store);

        var result = await client.GetEventParticipantsAsync(
            "12345",
            participantsApiKey: "secret-key",
            questionsApiKey: null,
            baseUrlOverride: "https://min.speiding.no/api",
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.QuestionsEndpointAvailable);
        var req = Assert.Single(requests);
        Assert.EndsWith("/project/get/participants", req.RequestUri!.AbsolutePath);
        Assert.Equal("Basic", req.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("12345:secret-key")), req.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void Parse_MultipleMembershipsFromExtensionFields_ExtractsDeduplicatedAvailableGroups()
    {
        const string json = """
            {
              "participants": {
                "20": {
                  "member_no": 20001,
                  "first_name": "Kari",
                  "last_name": "Speider",
                  "fee_id": 101,
                  "primary_membership_info": {
                    "group_id": 501,
                    "group_name": "Molde speidergruppe"
                  },
                  "memberships": {
                    "501": "Molde speidergruppe",
                    "503": "Aukra speidergruppe"
                  },
                  "other_memberships": [
                    { "group_id": 504, "group_name": "Sunndal speidergruppe" }
                  ],
                  "groups": ["505", "Vestnes speidergruppe", "molde speidergruppe"]
                }
              },
              "labels": {
                "groups": {
                  "505": "Fræna speidergruppe"
                },
                "project_fee": {
                  "101": "Speider (450 kr)"
                }
              }
            }
            """;

        var result = MinSpeidingParser.Parse("999", json, null, questionsEndpointAvailable: false);

        var p = Assert.Single(result.Participants);
        Assert.Equal("Molde speidergruppe", p.GroupName);
        Assert.True(p.HasMultipleGroups);
        Assert.Equal(5, p.AvailableGroups.Count);
        Assert.Equal("Molde speidergruppe", p.AvailableGroups[0].GroupName);
        Assert.Equal(501, p.AvailableGroups[0].GroupId);
        Assert.Equal("Aukra speidergruppe", p.AvailableGroups[1].GroupName);
        Assert.Equal(503, p.AvailableGroups[1].GroupId);
        Assert.Equal("Sunndal speidergruppe", p.AvailableGroups[2].GroupName);
        Assert.Equal(504, p.AvailableGroups[2].GroupId);
        Assert.Equal("Fræna speidergruppe", p.AvailableGroups[3].GroupName);
        Assert.Equal(505, p.AvailableGroups[3].GroupId);
        Assert.Equal("Vestnes speidergruppe", p.AvailableGroups[4].GroupName);
        Assert.Null(p.AvailableGroups[4].GroupId);
    }

    [Fact]
    public void Parse_UnrelatedGroupLikeFields_AreNotTreatedAsGroups()
    {
        const string json = """
            {
              "participants": {
                "40": {
                  "member_no": 40001,
                  "first_name": "Kari",
                  "last_name": "Speider",
                  "fee_id": 101,
                  "membership_status": "active",
                  "age_group": "Speider",
                  "other_memberships": [
                    { "group_id": 5, "status": "aktiv", "start_date": "2020-01-01" },
                    { "group_id": 6, "group_name": "Sunndal speidergruppe", "role": "leder" }
                  ]
                }
              },
              "labels": { "project_fee": { "101": "Speider (450 kr)" } }
            }
            """;

        var result = MinSpeidingParser.Parse("999", json, null, questionsEndpointAvailable: false);

        var p = Assert.Single(result.Participants);
        var only = Assert.Single(p.AvailableGroups);
        Assert.Equal("Sunndal speidergruppe", only.GroupName);

        // A group found only in extra fields is offered as a choice, but is not used as the recipient.
        Assert.Equal("Uten gruppe", p.GroupName);
    }

    [Fact]
    public void Parse_IgnoresDeprecatedPlaceholderGroupFields()
    {
        const string json = """
            {
              "participants": {
                "30": {
                  "member_no": 30001,
                  "first_name": "Ola",
                  "last_name": "Speider",
                  "fee_id": 101,
                  "group": "Deprecated - use group_registration_info",
                  "group_registration_info": {
                    "group_id": 10,
                    "group_name": "1. Batnfjord speidergruppe"
                  }
                }
              },
              "labels": {
                "project_fee": {
                  "101": "Speider (450 kr)"
                }
              }
            }
            """;

        var result = MinSpeidingParser.Parse("999", json, null, questionsEndpointAvailable: false);

        var p = Assert.Single(result.Participants);
        Assert.Equal("1. Batnfjord speidergruppe", p.GroupName);
        Assert.False(p.HasMultipleGroups);
        var onlyGroup = Assert.Single(p.AvailableGroups);
        Assert.Equal("1. Batnfjord speidergruppe", onlyGroup.GroupName);
    }

    [Fact]
    public void Parse_DetectsFeeAmountAndTotalAmount_AndSkipsLegacyDeprecatedFields()
    {
        const string json = """
            {
              "participants": {
                "1": {
                  "member_no": 1001,
                  "first_name": "Ola",
                  "last_name": "Speider",
                  "fee_id": 101,
                  "fee_amount": 450,
                  "purchase_amount": 0,
                  "total_amount": 450,
                  "group_id": "Deprecated - use group_registration_info",
                  "group_name": "Deprecated - use group_registration_info",
                  "district_name": "Deprecated - use group_registration_info",
                  "patrol_name": "Deprecated - use group_registration_info",
                  "org_name": "Deprecated - use group_registration_info",
                  "group_registration_info": {
                    "group_id": 501,
                    "group_name": "Molde speidergruppe"
                  }
                },
                "2": {
                  "member_no": 1002,
                  "first_name": "Kari",
                  "last_name": "Tillegg",
                  "fee_id": 102,
                  "fee_amount": 0,
                  "purchase_amount": 150,
                  "total_amount": 150,
                  "group_registration_info": {
                    "group_id": 501,
                    "group_name": "Molde speidergruppe"
                  }
                },
                "3": {
                  "member_no": 1003,
                  "first_name": "Per",
                  "last_name": "Leder",
                  "fee_id": 103,
                  "fee_amount": 0,
                  "purchase_amount": 0,
                  "total_amount": 0,
                  "group_registration_info": {
                    "group_id": 501,
                    "group_name": "Molde speidergruppe"
                  }
                },
                "4": {
                  "member_no": 1004,
                  "first_name": "Lise",
                  "last_name": "Stab",
                  "fee_id": 104,
                  "fee_amount": 0,
                  "purchase_amount": 0,
                  "total_amount": 0,
                  "group_registration_info": {
                    "group_id": 501,
                    "group_name": "Molde speidergruppe"
                  }
                }
              },
              "labels": {
                "project_fee": {
                  "101": "Speider",
                  "102": "Tillegg",
                  "103": "Leder (1 250 kr)",
                  "104": "Stab"
                }
              }
            }
            """;

        var result = MinSpeidingParser.Parse("999", json, null, questionsEndpointAvailable: false);

        Assert.Equal(450m, result.Participants.Single(p => p.ParticipantKey == "1").DetectedPriceNok);
        Assert.Equal(150m, result.Participants.Single(p => p.ParticipantKey == "2").DetectedPriceNok);
        Assert.Equal(1250m, result.Participants.Single(p => p.ParticipantKey == "3").DetectedPriceNok);
        Assert.Equal(0m, result.Participants.Single(p => p.ParticipantKey == "4").DetectedPriceNok);

        Assert.Equal(450m, result.FeeCategories.Single(c => c.Key == "fee:101").SuggestedPriceNok);
        Assert.Equal(150m, result.FeeCategories.Single(c => c.Key == "fee:102").SuggestedPriceNok);
        Assert.Equal(1250m, result.FeeCategories.Single(c => c.Key == "fee:103").SuggestedPriceNok);
        Assert.Equal(0m, result.FeeCategories.Single(c => c.Key == "fee:104").SuggestedPriceNok);

        Assert.All(result.Participants, p =>
        {
            Assert.False(p.HasMultipleGroups);
            var only = Assert.Single(p.AvailableGroups);
            Assert.Equal("Molde speidergruppe", only.GroupName);
        });
    }
}

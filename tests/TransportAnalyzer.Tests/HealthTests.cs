using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TransportAnalyzer.Tests;

public class HealthTests
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        await using var app = new WebApplicationFactory<Program>();
        var res = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("ok", res.GetProperty("status").GetString());
    }
}

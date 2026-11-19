using System;
using System.IO;
using System.Text.Json;

namespace Janus.IntegrationTests;

public static class TestConfiguration
{
    private static readonly string ConnectionStringValue;

    static TestConfiguration()
    {
        var envValue = Environment.GetEnvironmentVariable("JANUS_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(envValue))
        {
            ConnectionStringValue = envValue;
            return;
        }

        var launchSettingsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Properties", "launchSettings.json");
        if (File.Exists(launchSettingsPath))
        {
            using var jsonDoc = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
            if (jsonDoc.RootElement.TryGetProperty("profiles", out var profiles) &&
                profiles.TryGetProperty("Janus.IntegrationTests", out var profile) &&
                profile.TryGetProperty("environmentVariables", out var envVars) &&
                envVars.TryGetProperty("JANUS_CONNECTION_STRING", out var connString))
            {
                ConnectionStringValue = connString.GetString() ?? throw new InvalidOperationException("Connection string tokens are empty.");
                return;
            }
        }

        throw new InvalidOperationException("JANUS_CONNECTION_STRING environment variable is not configured.");
    }

    public static string ConnectionString => ConnectionStringValue;

    public static string GenerateTestEgn()
    {
        var year = Random.Shared.Next(2000, 2026);
        var month = Random.Shared.Next(1, 13);
        var day = Random.Shared.Next(1, 29);

        var encodedMonth = month + 40;
        var yearShort = year % 100;

        var egn9 = $"{yearShort:D2}{encodedMonth:D2}{day:D2}{Random.Shared.Next(100, 1000):D3}";

        var weights = new[] { 2, 4, 8, 5, 10, 9, 7, 3, 6 };
        var sum = 0;

        for (var i = 0; i < 9; i++)
        {
            sum += (egn9[i] - '0') * weights[i];
        }

        var rem = sum % 11;
        var checksum = rem < 10 ? rem : 0;

        return egn9 + checksum;
    }

}

using Aion.Host.Database;
using Microsoft.Extensions.Configuration;

namespace Aion.Host.Tests;

public sealed class PostgresMigrationRunnerTests
{
    [Theory]
    [InlineData("Development", null, true)]
    [InlineData("Production", null, false)]
    [InlineData("Production", "true", true)]
    [InlineData("Development", "false", false)]
    public void Migrate_on_startup_honours_environment_and_override(string environment, string? setting, bool enabled)
    {
        var data = new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = environment };
        if (setting is not null) data["Aion:Database:MigrateOnStartup"] = setting;
        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        Assert.Equal(enabled, PostgresMigrationRunner.Enabled(config));
    }
}
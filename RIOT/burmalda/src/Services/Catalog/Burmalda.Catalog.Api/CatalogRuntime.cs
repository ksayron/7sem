namespace Burmalda.Catalog.Api;

public sealed record CatalogRuntime(string Mode, string Instance)
{
    public static CatalogRuntime FromConfiguration(IConfiguration configuration) => new(
        string.IsNullOrWhiteSpace(configuration["APP_MODE"]) ? "local" : configuration["APP_MODE"]!.Trim(),
        configuration["HOSTNAME"] ?? Environment.MachineName);
}

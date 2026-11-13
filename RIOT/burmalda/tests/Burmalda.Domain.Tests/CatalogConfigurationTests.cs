using Burmalda.Catalog.Api;
using Microsoft.Extensions.Configuration;

namespace Burmalda.Domain.Tests;

public sealed class CatalogConfigurationTests
{
    [Theory]
    [InlineData(null, "local")]
    [InlineData(" ", "local")]
    [InlineData(" kubernetes ", "kubernetes")]
    [InlineData("compose", "compose")]
    public void Mode_uses_configured_value_or_local_default(string? value, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["APP_MODE"] = value, ["HOSTNAME"] = "catalog-pod" }).Build();
        Assert.Equal(new CatalogRuntime(expected, "catalog-pod"), CatalogRuntime.FromConfiguration(configuration));
    }

    [Fact]
    public void Valid_book_and_copy_are_accepted()
    {
        Assert.True(CatalogRequestValidation.IsValid(new CreateBookRequest("isbn", "Title", null, ["Author"], [], null, 2026, null)));
        Assert.True(CatalogRequestValidation.IsValid(new ListCopyRequest(Guid.NewGuid(), BookCondition.Good, 10, 14, "Minsk")));
    }

    [Fact]
    public void Missing_authors_or_location_are_rejected_without_throwing()
    {
        Assert.False(CatalogRequestValidation.IsValid(new CreateBookRequest("isbn", "Title", null, null!, [], null, null, null)));
        Assert.False(CatalogRequestValidation.IsValid(new ListCopyRequest(Guid.NewGuid(), BookCondition.Good, 10, 14, null!)));
    }

    [Theory]
    [InlineData(-1, 14)]
    [InlineData(10, 0)]
    [InlineData(10, 91)]
    public void Invalid_lending_terms_are_rejected(int price, int days) =>
        Assert.False(CatalogRequestValidation.IsValid(new ListCopyRequest(Guid.NewGuid(), BookCondition.Good, price, days, "Minsk")));
}

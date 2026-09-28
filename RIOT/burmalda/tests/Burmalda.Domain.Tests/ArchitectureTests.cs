namespace Burmalda.Domain.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Service_assemblies_do_not_reference_other_service_assemblies()
    {
        var serviceAssemblies = new[]
        {
            typeof(Burmalda.Catalog.Api.CatalogDbContext).Assembly,
            typeof(Burmalda.Lending.Api.LendingDbContext).Assembly,
            typeof(Burmalda.Economy.Api.EconomyDbContext).Assembly
        };
        foreach (var assembly in serviceAssemblies)
        {
            var forbidden = assembly.GetReferencedAssemblies().Where(x => x.Name?.StartsWith("Burmalda.") == true && x.Name.EndsWith(".Api") && x.Name != assembly.GetName().Name).ToArray();
            Assert.Empty(forbidden);
        }
    }
}

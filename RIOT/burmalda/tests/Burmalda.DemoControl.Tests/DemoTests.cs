using Burmalda.DemoControl;
using Burmalda.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Burmalda.DemoControl.Tests;

public class DemoTests
{
    [Fact]
    public void KubernetesTrustBundleDoesNotRequireTheClusterPrivateKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "burmalda-demo-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=KubernetesTestCA",key,
                System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
            File.WriteAllText(Path.Combine(directory,"ca.crt"),certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory,"token"),"test-token");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Demo:ServiceAccountDirectory"]=directory }).Build();
            using var kube = new KubeControl(configuration,null!);
        }
        finally
        {
            File.Delete(Path.Combine(directory,"ca.crt")); File.Delete(Path.Combine(directory,"token")); Directory.Delete(directory);
        }
    }
    [Theory]
    [InlineData(0,20)] [InlineData(30,20)] [InlineData(75,160)] [InlineData(120,300)]
    [InlineData(239,300)] [InlineData(255,150)] [InlineData(270,0)] [InlineData(420,0)]
    public void SevenMinuteScenarioHasRealRampAndObservationWindow(double at, int expected)
    {
        var options = new RunOptions();
        Assert.Equal(420, options.DurationSeconds);
        Assert.Equal(expected, ScenarioSchedule.Target(options, at));
    }
    [Fact]
    public void SmallScenarioNeverStartsMoreClientsThanItsPeak()
    {
        var options = new RunOptions(Peak: 3, RampSeconds: 5, HoldSeconds: 30);
        for (var second = 0; second <= options.DurationSeconds; second++) Assert.InRange(ScenarioSchedule.Target(options, second), 0, 3);
    }
    [Theory]
    [InlineData("unknown",1,90,120)] [InlineData("wave",501,90,120)]
    [InlineData("wave",0,90,120)] [InlineData("wave",20,0,120)] [InlineData("wave",20,90,601)]
    public void InvalidRunCannotStart(string scenario, int clients, int ramp, int hold) =>
        Assert.Throws<DemoConflict>(() => new RunOptions(scenario,clients,ramp,hold).Validate());
    [Theory]
    [InlineData("postgres",25,0,30)] [InlineData("catalog",101,0,30)] [InlineData("gateway",10,5001,30)] [InlineData("catalog",25,0,121)]
    public void FaultsAreRestrictedAndBounded(string service, int percent, int delay, int ttl) =>
        Assert.Throws<DemoConflict>(() => new FaultRequest(service,percent,delay,ttl).Validate());
    [Theory]
    [InlineData("/health/live",false)] [InlineData("/health/ready",false)]
    [InlineData("/internal/faults/catalog",false)] [InlineData("/api/catalog/books/search",true)]
    public async Task FaultsAffectBusinessApiButNeverProbesOrControl(string path, bool faulted)
    {
        var called = false;
        var state = new DemoFaultState(); state.Set(new("catalog",100,0,DateTimeOffset.UtcNow.AddSeconds(30)));
        var middleware = new DemoFaultMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext(); context.Request.Path = path; context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context,state);
        Assert.Equal(!faulted,called); Assert.Equal(faulted ? 503 : 200,context.Response.StatusCode);
    }
    [Fact]
    public async Task ExpiredFaultIsHarmlessEvenWhenItsControllerCannotBeReached()
    {
        var state = new DemoFaultState(); state.Set(new("catalog",100,5000,DateTimeOffset.UtcNow.AddSeconds(-1)));
        var called = false;
        var middleware = new DemoFaultMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext(); context.Request.Path="/api/catalog/books/search";
        await middleware.InvokeAsync(context,state);
        Assert.True(called); Assert.Equal(200,context.Response.StatusCode);
    }
    [Fact]
    public async Task RestartMarksRunInterruptedAndPreservesFixturesAndUnknownOperations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DemoDb>().UseSqlite(connection).Options;
        var factory = new PooledDbContextFactory<DemoDb>(options);
        using var metrics = new DemoMetrics(); var store = new DemoStore(factory,metrics);
        await store.Initialize();
        var run = new DemoRun(Guid.NewGuid(),new RunOptions(),DateTimeOffset.UtcNow);
        await store.Write("run",run); await store.Write("fixtures",new[] { "preserved fixture" });
        await store.Write("pending",new Dictionary<string,string> { ["1"]="unknown loan outcome" });
        await store.Initialize();
        var restored = await store.Read<DemoRun>("run");
        Assert.Equal(run.Id,restored!.Id); Assert.Equal("interrupted",restored.State); Assert.NotNull(restored.FinishedAt);
        Assert.Equal("preserved fixture",Assert.Single((await store.Read<string[]>("fixtures"))!));
        Assert.Equal("unknown loan outcome",(await store.Read<Dictionary<string,string>>("pending"))!["1"]);
        Assert.Contains(await store.Events(),x=>x.Kind=="interrupted");
        var result = await store.Read<System.Text.Json.JsonElement>("result:" + run.Id);
        Assert.Equal("interrupted", result.GetProperty("Run").GetProperty("State").GetString());
        Assert.Equal("unknown loan outcome", result.GetProperty("Unresolved").GetProperty("1").GetString());
    }
}

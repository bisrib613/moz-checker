using System.Net;
using System.Text.Json.Nodes;
using MozChecker.Core;
using Xunit;

namespace MozChecker.Tests;

public sealed class RdapRoutingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "moz-rdap-" + Guid.NewGuid().ToString("N"));
    private Store Open() => new(Path.Combine(directory, "checker.sqlite3"));
    private const string OldList = "{\"services\":[[[\"com\"],[\"https://rdap.verisign.com/com/v1/\"]]]}";
    private const string NewList = "{\"services\":[[[\"fr\"],[\"https://rdap.nic.fr/\"]],[[\"com\"],[\"https://rdap.verisign.com/com/v1/\"]]]}";
    private static HttpResponseMessage Json(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data) };

    [Fact]
    public async Task MissingFrenchRouteRefreshesOldCacheAndPersistsOfficialRoute()
    {
        using var store = Open();
        store.CachePut("iana", "bootstrap", JsonNode.Parse(OldList));
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://data.iana.org/rdap/dns.json", request.RequestUri!.AbsoluteUri);
            return Json(NewList);
        });
        using var http = new Transport(handler);
        var providers = new Providers(store, new(), http);
        var routes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => providers.ResolveRdapAsync($"example{i}.fr", CancellationToken.None)));
        Assert.All(routes, uri => Assert.Equal("rdap.nic.fr", uri!.Host));
        Assert.Equal("https://rdap.nic.fr/domain/example0.fr", routes[0]!.AbsoluteUri);
        Assert.Equal(1, handler.Calls);
        var restarted = new Providers(store, new(), http);
        Assert.NotNull(await restarted.ResolveRdapAsync("another.fr", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExistingCachedRouteDoesNotDownloadOrUseFallbackServers()
    {
        using var store = Open();
        store.CachePut("iana", "bootstrap", JsonNode.Parse(NewList));
        var handler = new StubHandler(_ => throw new InvalidOperationException("Unexpected HTTP request"));
        using var http = new Transport(handler);
        var providers = new Providers(store, new(), http);
        Assert.Equal("https://rdap.nic.fr/domain/afnic.fr", (await providers.ResolveRdapAsync("afnic.fr", CancellationToken.None))!.AbsoluteUri);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnsupportedTldsShareOneRefreshAndRemainUnknownWithoutDnsInference()
    {
        using var store = Open();
        store.CachePut("iana", "bootstrap", JsonNode.Parse(OldList));
        var handler = new StubHandler(_ => Json(OldList));
        using var http = new Transport(handler);
        var providers = new Providers(store, new(), http);
        foreach (var domain in new[] { "afnic.fr", "example.de", "another.fr" })
        {
            var result = new Result { Domain = domain, Dns = "nxdomain", Status = "available" };
            await providers.AvailabilityAsync(result, false, CancellationToken.None);
            Assert.Equal("unknown", result.Status);
            Assert.Equal("", result.Dns);
            Assert.Null(result.RdapHttp);
            Assert.Contains("setelah pembaruan daftar IANA", result.Errors["availability"]);
        }
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FailedRefreshKeepsOldRoutesAndDoesNotClaimTldIsUnsupported()
    {
        using var store = Open();
        store.CachePut("iana", "bootstrap", JsonNode.Parse(OldList));
        var handler = new StubHandler(_ => Json("{\"services\":[]}"));
        using var http = new Transport(handler);
        var providers = new Providers(store, new(), http);
        for (var i = 0; i < 3; i++)
        {
            var failure = await Assert.ThrowsAsync<SourceException>(() => providers.ResolveRdapAsync("afnic.fr", CancellationToken.None));
            Assert.Contains("Pembaruan daftar RDAP IANA gagal", failure.Message);
        }
        Assert.Equal(1, handler.Calls);
        Assert.NotNull(await providers.ResolveRdapAsync("example.com", CancellationToken.None));
        Assert.Equal(OldList, store.CacheGet<JsonNode>("iana", "bootstrap", 86400)!.ToJsonString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"services\":[]}")]
    public async Task InvalidCachedListIsReplacedBeforeRouting(string cached)
    {
        using var store = Open();
        store.CachePut("iana", "bootstrap", JsonNode.Parse(cached));
        var handler = new StubHandler(_ => Json(NewList));
        using var http = new Transport(handler);
        Assert.NotNull(await new Providers(store, new(), http).ResolveRdapAsync("afnic.fr", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FreshListDoesNotImmediatelyRedownloadAndRouteMatchingAcceptsCase()
    {
        using var store = Open();
        var handler = new StubHandler(_ => Json("{\"services\":[[[\"FR\"],[\"http://rdap.nic.fr/\",\"HTTPS://rdap.nic.fr/\"]]]}"));
        using var http = new Transport(handler);
        var providers = new Providers(store, new(), http);
        Assert.Equal("rdap.nic.fr", (await providers.ResolveRdapAsync("afnic.fr", CancellationToken.None))!.Host);
        Assert.Null(await providers.ResolveRdapAsync("example.de", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}

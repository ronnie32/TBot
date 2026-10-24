using System.Net;
using System.Net.Http;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class BoundedDiscoveryTransportTests {
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bounded_endpoint_sends_only_reserved_positions_and_preserves_partial_evidence(bool complete) {
        var requests = new List<(string Path, string Body)>();
        using var client = new HttpClient(new Handler(async request => {
            requests.Add((request.RequestUri.AbsolutePath, await request.Content.ReadAsStringAsync()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"Status\":\"ok\",\"Result\":{\"coordinates\":[{\"Galaxy\":1,\"System\":2,\"Position\":3}],\"complete\":" + complete.ToString().ToLowerInvariant() + ",\"error\":\"synthetic partial result\"}}") };
        })) { BaseAddress = new Uri("http://offline.invalid/") };
        var service = Create(client);
        var result = await Invoke(service, 2, new[] { 3, 7 });
        Assert.Equal(complete, (bool)result["Complete"]);
        Assert.Equal(3, (int)result["Coordinates"][0]["Position"]);
        var request = Assert.Single(requests);
        Assert.Equal("/bot/planets/101/send-system-discovery-bounded", request.Path);
        Assert.Equal("galaxy=1&system=2&maxFleets=2&positions=3%2C7", request.Body);
    }

    [Theory]
    [InlineData(0, new[] { 1 })]
    [InlineData(1, new[] { 1, 2 })]
    [InlineData(2, new[] { 1, 1 })]
    [InlineData(1, new[] { 16 })]
    public async Task Invalid_reservations_are_rejected_before_any_request(int maximum, int[] positions) {
        int requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; throw new InvalidOperationException(); })) { BaseAddress = new Uri("http://offline.invalid/") };
        var service = Create(client);
        var method = Method();
        var invocation = (Task)method.Invoke(service, new object[] { new Celestial { ID = 101 }, 1, 2, maximum, positions });
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await invocation);
        Assert.Equal(0, requests);
    }

    private static MethodInfo Method() {
        var method = typeof(OgameService).GetMethod("SendBoundedSystemDiscovery");
        Assert.NotNull(method);
        return method;
    }
    private static async Task<JObject> Invoke(OgameService service, int maximum, int[] positions) {
        var task = (Task)Method().Invoke(service, new object[] { new Celestial { ID = 101 }, 1, 2, maximum, positions });
        await task;
        return JObject.FromObject(task.GetType().GetProperty("Result").GetValue(task));
    }
    private static OgameService Create(HttpClient client) {
        var logger = AuditProxy<ILoggerService<OgameService>>.Make((m, _) => AuditProxy<ILoggerService<OgameService>>.Default(m.ReturnType));
        var service = new OgameService(logger);
        typeof(OgameService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, client);
        return service;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}

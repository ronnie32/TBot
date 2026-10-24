using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class OgameDiscoveryTransportTests {
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_failed_discovery_post_is_not_replayed_and_retains_server_diagnostics(HttpStatusCode status) {
        using var fixture = new DiscoveryTransportFixture((attempt, _) => Task.FromResult(
            attempt == 1
                ? Json(status, "{\"status\":\"error\",\"code\":500,\"message\":\"page token expired in fixture\"}")
                : Json(HttpStatusCode.OK, "{\"status\":\"ok\",\"result\":true}")));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Send());
        Assert.Contains("page token expired in fixture", error.Message);
        var request = Assert.Single(fixture.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/bot/planets/101/send-discovery", request.Path);
        Assert.Equal("galaxy=1&system=2&position=3", request.Body);

    }

    [Fact]
    public async Task A_connection_lost_after_a_discovery_post_is_not_replayed() {
        using var fixture = new DiscoveryTransportFixture((attempt, _) => {
            if (attempt == 1)
                throw new HttpRequestException("connection lost after send in fixture");
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"ok\",\"result\":true}"));
        });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Send());
        Assert.Contains("connection lost after send in fixture", error.Message);

        Assert.Single(fixture.Requests);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_discovery_result_is_preserved_without_replaying_the_action(bool result) {
        using var fixture = new DiscoveryTransportFixture((_, _) => Task.FromResult(
            Json(HttpStatusCode.OK, "{\"status\":\"ok\",\"result\":" + (result ? "true" : "false") + "}")));

        Assert.Equal(result, await fixture.Send());

        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task A_rejected_discovery_keeps_the_server_reason() {
        using var fixture = new DiscoveryTransportFixture((_, _) => Task.FromResult(
            Json(HttpStatusCode.BadRequest, "{\"status\":\"error\",\"message\":\"discovery unavailable in fixture\"}")));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Send());
        Assert.Contains("discovery unavailable in fixture", error.Message);

        Assert.Single(fixture.Requests);

    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task An_unsuccessful_http_status_cannot_be_overridden_by_an_ok_envelope(HttpStatusCode status) {
        using var fixture = new DiscoveryTransportFixture((attempt, _) => Task.FromResult(
            Json(attempt == 1 ? status : HttpStatusCode.OK, "{\"status\":\"ok\",\"result\":true}")));

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Send());

        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task A_malformed_discovery_response_never_becomes_success() {
        using var fixture = new DiscoveryTransportFixture((_, _) => Task.FromResult(
            Json(HttpStatusCode.OK, "not JSON")));

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Send());

        Assert.Single(fixture.Requests);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class DiscoveryTransportFixture : IDisposable {
        private readonly HttpClient _httpClient;
        private readonly OgameService _service;
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = new();
        public List<string> Logs { get; } = new();

        public DiscoveryTransportFixture(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> respond) {
            var logger = StrictInterfaceProxy.Make<ILoggerService<OgameService>>((method, args) => {
                if (method.Name != "WriteLog")
                    throw new NotSupportedException(method.Name);
                Logs.Add((string)args[2]);
                return null;
            });
            _service = new OgameService(logger);
            _httpClient = new HttpClient(new RecordingHandler(async request => {
                Requests.Add((request.Method, request.RequestUri.AbsolutePath, await request.Content.ReadAsStringAsync()));
                return await respond(Requests.Count, request);
            })) { BaseAddress = new Uri("http://ogamed.invalid/") };
            // Inject the transport without Initialize(), which would start a real daemon.
            typeof(OgameService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_service, _httpClient);
        }

        public Task<bool> Send() => _service.SendDiscovery(new Celestial { ID = 101 }, new Coordinate(1, 2, 3));
        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}

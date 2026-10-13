using System.Net;
using System.Text;

namespace MealieMcp.Tests;

/// <summary>Records requests and answers them from a routing function.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string PathAndQuery, string? Body, string? Auth)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString()));

        var (status, responseBody) = respond(request);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

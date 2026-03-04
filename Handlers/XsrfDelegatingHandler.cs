using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace FFLogsPlugin.Handlers;

/// <summary>
/// Automatically sets the X-XSRF-TOKEN header on every outgoing request
/// by reading the XSRF-TOKEN cookie from the CookieContainer.
/// This handles token rotation transparently without manual refresh calls.
/// </summary>
public class XsrfDelegatingHandler : DelegatingHandler
{
    private readonly CookieContainer cookies;
    private readonly Uri baseUri;

    public XsrfDelegatingHandler(CookieContainer cookies, Uri baseUri, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        this.cookies = cookies;
        this.baseUri = baseUri;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var xsrfCookie = cookies.GetCookies(baseUri)["XSRF-TOKEN"]?.Value;
        if (!string.IsNullOrEmpty(xsrfCookie))
        {
            var decoded = HttpUtility.UrlDecode(xsrfCookie);
            request.Headers.Remove("X-XSRF-TOKEN");
            request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", decoded);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

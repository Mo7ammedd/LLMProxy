using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LLMProxy.Api;

public static class DashboardEndpoints
{
    public static void MapDashboard(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin", (HttpContext http) =>
        {
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            return Asset("index.html", "text/html");
        }).AllowAnonymous();
        endpoints.MapGet("/admin/console.css", () => Asset("console.css", "text/css")).AllowAnonymous();
        endpoints.MapGet("/admin/console.js", () => Asset("console.js", "text/javascript")).AllowAnonymous();
        endpoints.MapGet("/admin/icon.svg", () => Asset("icon.svg", "image/svg+xml")).AllowAnonymous();
    }
    private static IResult Asset(string name, string contentType)
    {
        using var stream = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream("LLMProxy.Api.Dashboard." + name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Results.Content(reader.ReadToEnd(), contentType, Encoding.UTF8);
    }
}

using System.Reflection;

namespace NexusPipeline.Host.Control.Http;

internal static class WebAssets
{
    private static readonly IReadOnlyDictionary<string, string> Assets = Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .Where(name => name.StartsWith("nexus.web/", StringComparison.Ordinal))
        .ToDictionary(name => "/" + name["nexus.web/".Length..].Replace('\\', '/'), name => name, StringComparer.Ordinal);
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal)
        { "/", "/dashboard", "/dispatch", "/queues", "/scripts", "/users", "/history", "/plugins", "/notifications", "/settings" };

    internal static bool IsPublic(HttpContext context) => context.Request.Method == "GET" &&
        (Pages.Contains(context.Request.Path.Value ?? "") || Assets.ContainsKey(context.Request.Path.Value ?? ""));

    internal static void Map(WebApplication app)
    {
        foreach (var path in Pages) app.MapGet(path, (HttpContext context) => Serve(context, "/index.html"));
        foreach (var path in Assets.Keys) app.MapGet(path, (HttpContext context) => Serve(context, path));
    }

    private static IResult Serve(HttpContext context, string path)
    {
        if (!Assets.TryGetValue(path, out var resource)) return Results.StatusCode(503);
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; " +
            "connect-src 'self'; font-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var type = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8", ".svg" => "image/svg+xml", ".png" => "image/png",
            ".woff2" => "font/woff2", _ => "application/octet-stream"
        };
        return Results.Stream(Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!, type);
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ancorhash.Tests.Api;

/// <summary>
/// Invoca una Function HTTP con una richiesta in memoria e serializza la risposta
/// come l'host (System.Text.Json con i default web), per verificare il JSON esposto.
/// </summary>
internal static class FunctionHttp
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static Task<(int Status, JsonElement Body)> PostJsonAsync(
        Func<HttpRequest, CancellationToken, Task<IActionResult>> function,
        object payload) =>
        PostRawAsync(function, JsonSerializer.Serialize(payload), "application/json");

    public static async Task<(int Status, JsonElement Body)> PostRawAsync(
        Func<HttpRequest, CancellationToken, Task<IActionResult>> function,
        string body,
        string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = Assert.IsAssignableFrom<ObjectResult>(
            await function(context.Request, TestContext.Current.CancellationToken));
        var json = JsonSerializer.SerializeToElement(result.Value, result.Value!.GetType(), WebJson);
        return (result.StatusCode ?? StatusCodes.Status200OK, json);
    }
}

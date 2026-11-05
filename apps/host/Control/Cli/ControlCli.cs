using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Cli;

internal static class ControlCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task<int> RunAsync(string[] args)
    {
        byte[]? body = null; byte[]? bearer = null;
        ControlCliRequest? command = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            command = ControlCliRequest.Parse(args);
            if (command.Input is { } input)
            {
                await using var source = input == "-" ? Console.OpenStandardInput() :
                    new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                body = await ReadBoundedAsync(source, command.InputMediaType == "application/json" ? StrictJson.MaximumBytes :
                    command.Path == "/control/v1/local/control-token/change" ? 256 : 196608, deadline.Token);
                using var parsed = command.InputMediaType == "application/json" ? StrictJson.Parse(body) : null;
                if (command.OperationId is null && parsed is not null && parsed.RootElement.ValueKind == JsonValueKind.Object &&
                    parsed.RootElement.TryGetProperty("operationId", out var operationValue) &&
                    operationValue.ValueKind == JsonValueKind.String && Guid.TryParseExact(operationValue.GetString(), "D", out var id) &&
                    id != Guid.Empty && operationValue.GetString() == id.ToString("D")) command = command with { OperationId = id };
            }
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None, Credentials = null,
                MaxConnectionsPerServer = 1, ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            using var client = new HttpClient(handler) { BaseAddress = command.Address, Timeout = TimeSpan.FromSeconds(60) };
            using var request = new HttpRequestMessage(command.Method, command.Path);
            if (command.BearerFile is { } credential)
            {
                await using var file = new FileStream(credential, FileMode.Open, FileAccess.Read, FileShare.Read);
                bearer = await ReadBoundedAsync(file, 256, deadline.Token);
                if (bearer.Length < 32 || bearer.Any(value => value is < 33 or > 126)) throw new ArgumentException("control_cli_credential_invalid");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(bearer));
            }
            request.Headers.Accept.Add(new(command.BinaryMaximumBytes == 0 ? "application/json" : "*/*"));
            if (command.FixedHeaders is not null)
                foreach (var header in command.FixedHeaders) request.Headers.Add(header.Key, header.Value);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new(command.InputMediaType) { CharSet = "utf-8" };
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            if (command.BinaryMaximumBytes != 0)
            {
                if (!response.IsSuccessStatusCode) return Failure(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 : 4,
                    "control_cli_artifact_rejected", command.OperationId, true);
                if (response.Content.Headers.ContentLength > command.BinaryMaximumBytes) throw new IOException("control_cli_content_limit");
                await CopyBoundedAsync(stream, Console.OpenStandardOutput(), command.BinaryMaximumBytes, deadline.Token);
                return 0;
            }
            var bytes = await ReadBoundedAsync(stream, StrictJson.MaximumBytes, deadline.Token);
            try
            {
                using var document = bytes.Length == 0 ? null : StrictJson.Parse(bytes);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    format = "nexus.control.cli-response", version = 1, status = (int)response.StatusCode,
                    operationId = command.OperationId, body = document?.RootElement
                }, Json));
            }
            catch (JsonException) { return Failure(5, "control_cli_response_invalid", command.OperationId); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return response.IsSuccessStatusCode ? 0 : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 :
                response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.Gone ? 4 : 5;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException)
        { return Failure(2, "control_cli_input_invalid", command?.OperationId, command?.BinaryMaximumBytes > 0); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        { return Failure(6, "control_cli_transport_or_input_unavailable", command?.OperationId, command?.BinaryMaximumBytes > 0); }
        finally
        {
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            if (bearer is not null) CryptographicOperations.ZeroMemory(bearer);
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken cancellation)
    {
        var bytes = new byte[65536]; long count = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(bytes, cancellation);
                if (read == 0) break;
                count = checked(count + read);
                if (count > maximum) throw new IOException("control_cli_content_limit");
                await destination.WriteAsync(bytes.AsMemory(0, read), cancellation);
            }
            await destination.FlushAsync(cancellation);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken cancellation)
    {
        var bytes = new byte[maximum + 1]; var count = 0;
        try
        {
            while (count <= maximum)
            {
                var read = await source.ReadAsync(bytes.AsMemory(count, maximum + 1 - count), cancellation);
                if (read == 0) return bytes.AsSpan(0, count).ToArray();
                count += read;
            }
            throw new InvalidDataException("control_cli_content_limit");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static int Failure(int exit, string code, Guid? operation, bool binary = false)
    {
        Console.Error.WriteLine(code);
        var response = JsonSerializer.Serialize(new { format = "nexus.control.cli-response", version = 1,
            status = (int?)null, operationId = operation, code }, Json);
        if (binary) Console.Error.WriteLine(response); else Console.WriteLine(response);
        return exit;
    }
}

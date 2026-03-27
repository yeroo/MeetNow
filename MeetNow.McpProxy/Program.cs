// MeetNow MCP Proxy — stdio-to-HTTP bridge
// Reads JSON-RPC from stdin, POSTs to MeetNow's MCP server, writes response to stdout.

using System.Text;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 27182;
var baseUrl = $"http://localhost:{port}/messages";
using var client = new HttpClient(new HttpClientHandler { UseProxy = false });

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var stderr = Console.Error;
stderr.WriteLine($"MeetNow MCP Proxy started, forwarding to {baseUrl}");

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;
    if (string.IsNullOrWhiteSpace(line)) continue;

    try
    {
        var content = new StringContent(line, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(baseUrl, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!string.IsNullOrEmpty(body))
        {
            Console.WriteLine(body);
            Console.Out.Flush();
        }
    }
    catch (HttpRequestException ex)
    {
        stderr.WriteLine($"Connection error: {ex.Message}");
        Console.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32000,\"message\":\"MeetNow not running on port {port}\"}}}}");
        Console.Out.Flush();
    }
    catch (Exception ex)
    {
        stderr.WriteLine($"Error: {ex.Message}");
    }
}

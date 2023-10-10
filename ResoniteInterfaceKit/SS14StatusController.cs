using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace ResoniteInterfaceKit;

public class SS14StatusController : ControllerBase
{
    private const int BufferSize = 512;
    private const string Version = "0.0.0";
    private const char CommandSplit = '\x07';
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient = new();
    private Dictionary<Uri, (DateTime, JsonNode)> _lastStatusData = new();

    public SS14StatusController(ILogger<SS14StatusController> logger)
    {
        _logger = logger;
        _httpClient.MaxResponseContentBufferSize = 2048;
    }

    [Route("/v1/ss14status")]
    public async Task Get()
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            var requestMem = new Memory<byte>(new byte[BufferSize]);
            var state = new SS14StatusState();
            while (true)
            {
                if (webSocket.CloseStatus is not null)
                {
                    break;
                }
                
                var res = await webSocket.ReceiveAsync(requestMem, new());
                switch (res.MessageType)
                {
                    case WebSocketMessageType.Close:
                        return;
                    default:
                    case WebSocketMessageType.Binary:
                        await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Cannot read binary or non-standard forms.", new());
                        return;
                    case WebSocketMessageType.Text:
                    {
                        if (res.Count > BufferSize)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                                $"Max message size is {BufferSize}.", new());
                            return;
                        }

                        var len = res.Count;
                        var text = Encoding.UTF8.GetString(requestMem.Span.Slice(0, len));
                        _logger.LogInformation($"Got command {text}");
                        var cmdStream = text.Split(CommandSplit);
                        
                        await HandleCommand(webSocket, cmdStream, state);
                        break;
                    }
                } 
            }
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

    private sealed class SS14StatusState
    {
        public Uri? Target;
    }

    private async Task HandleCommand(WebSocket socket, string[] args, SS14StatusState state)
    {
        const string cmdSetTarget = "SETTARGET";
        const string cmdVersion = "VERSION";
        const string cmdReady = "READYFORDATA";
        switch (args[0])
        {
            case cmdVersion:
            {
                _logger.LogInformation($"Replying to {cmdVersion} request.");
                await socket.Reply($"REPLY:{cmdVersion}{CommandSplit}{Version}");
                break;
            }
            case cmdSetTarget:
            {
                if (args.Length < 2)
                {
                    await socket.CommandKillConn(cmdSetTarget, "Expected an argument.");
                    break;
                }

                if (!Uri.TryCreate(args[1], UriKind.Absolute, out var res) || !res.Scheme.StartsWith("http"))
                {
                    await socket.CommandKillConn(cmdSetTarget, "Bad URI.");
                    break;
                }
                state.Target = res;
                await socket.Reply($"REPLY:{cmdSetTarget}{CommandSplit}");
                
                break;
            }
            case cmdReady:
            {
                if (state.Target is null)
                {
                    await socket.CommandKillConn(cmdReady, "No target set.");
                    break;
                }

                if (_lastStatusData.TryGetValue(state.Target, out var res))
                {
                    var (time, data) = res;
                    if (time + TimeSpan.FromMilliseconds(400) < DateTime.Now)
                    {
                        await socket.Reply($"REPLY:{cmdReady}{CommandSplit}{TryIntoStatusData(data!)}");
                        break;
                    }
                }

                try
                {
                    var response = await _httpClient.GetAsync(state.Target);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        await socket.CommandKillConn(cmdReady, "Couldn't read status.");
                        break;
                    }

                    try
                    {
                        var node = JsonNode.Parse(await response.Content.ReadAsStreamAsync());
                        _lastStatusData[state.Target] = (DateTime.Now, node)!;
                        await socket.Reply($"REPLY:{cmdReady}{CommandSplit}{TryIntoStatusData(node!)}");
                        break;
                    }
                    catch (JsonException)
                    {
                        await socket.CommandKillConn(cmdReady, "Didn't get valid JSON.");
                        break;
                    }
                }
                catch (Exception e)
                {
                    await socket.CommandKillConn(cmdReady, $"Couldn't read: {e.Message}");
                    break;
                }
                break;
            }
        }
    }
    
    public static string? TryIntoStatusData(JsonNode node)
    {
        if (node is not JsonObject obj)
            return null;
        if (!obj.TryGetPropertyValue("players", out var playerNode) || playerNode is not JsonValue playerValue ||!playerValue.TryGetValue(out int players))
            return null;
        if (!obj.TryGetPropertyValue("soft_max_players", out var maxPlayerNode) || maxPlayerNode is not JsonValue maxPlayerValue ||!maxPlayerValue.TryGetValue(out int maxPlayers))
            return null;
        if (!obj.TryGetPropertyValue("name", out var nameNode) || nameNode is not JsonValue nameValue ||!nameValue.TryGetValue(out string? name))
            return null;
        if (!obj.TryGetPropertyValue("round_start_time", out var startTimeNode) || startTimeNode is not JsonValue startTimeValue ||!startTimeValue.TryGetValue(out string? startTime))
            return null;
        if (!obj.TryGetPropertyValue("run_level", out var runLevelNode) ||
            runLevelNode is not JsonValue runLevelValue || !runLevelValue.TryGetValue(out int runLevel))
            return null;

        return $"{players}{CommandSplit}{maxPlayers}{CommandSplit}{name}{CommandSplit}{startTime}{CommandSplit}{runLevel}";
    }
}
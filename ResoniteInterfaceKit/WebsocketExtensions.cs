using System.Net.WebSockets;
using System.Text;

namespace ResoniteInterfaceKit;

public static class WebsocketExtensions
{
    public static async Task Reply(this WebSocket socket, string reply)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(reply).AsMemory(), WebSocketMessageType.Text, true, new());
    }

    public static async Task CommandKillConn(this WebSocket socket, string command, string reason)
    {
        await socket.CloseAsync(WebSocketCloseStatus.ProtocolError, $"Command {command} failed: {reason}", new());
    }
}
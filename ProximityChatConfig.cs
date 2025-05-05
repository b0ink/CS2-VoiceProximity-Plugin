using CounterStrikeSharp.API.Core;

namespace ProximityChat;

public class Config : BasePluginConfig
{ 
    public string? SocketURL { get; set; } = "https://cs2voiceproximity.chat";
    public string? ApiKey { get; set; } 
}
using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace ProximityChat;

public class Config : BasePluginConfig
{ 
    public string? SocketURL { get; set; } = "https://cs2voiceproximity.chat";
}
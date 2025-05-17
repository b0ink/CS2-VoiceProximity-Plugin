using CounterStrikeSharp.API.Core;
using MessagePack;

namespace ProximityChat;

[MessagePackObject(true)]
public class Config : BasePluginConfig
{
    [IgnoreMember]
    public string? SocketURL { get; set; } = "https://cs2voiceproximity.chat";

    [IgnoreMember]
    public string? ApiKey { get; set; }

    // Seconds before players are muted after dying
    [Key(0)] public float DeadPlayerMuteDelay { get; set; } = 1f;

    // Can dead teammates communicate to each other
    [Key(1)] public bool AllowDeadTeamVoice { get; set; } = true;

    // Can dead players speak when spectating C4
    [Key(2)] public bool AllowSpectatorC4Voice { get; set; } = true;
}
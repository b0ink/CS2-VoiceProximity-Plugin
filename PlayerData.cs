using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using MessagePack;

namespace ProximityChat;

[MessagePackObject]
public class PlayerData
{
    [Key(0)] public string SteamId { get; set; }
    [Key(1)] public string Name { get; set; }
    [Key(2)] public int OriginX { get; set; }
    [Key(3)] public int OriginY { get; set; }
    [Key(4)] public int OriginZ { get; set; }
    [Key(5)] public int LookAtX { get; set; }
    [Key(6)] public int LookAtY { get; set; }
    [Key(7)] public int LookAtZ { get; set; }
    [Key(8)] public int Team { get; set; }
    [Key(9)] public bool IsAlive { get; set; }

    public PlayerData(string steamId)
    {
        this.SteamId = steamId;
    }

}
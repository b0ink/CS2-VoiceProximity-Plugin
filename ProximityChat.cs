using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using SocketIOClient;
using MessagePack;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Cvars;
using System.Net;

namespace ProximityChat;

public class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Proximity Chat API";
    public override string ModuleAuthor => "b0ink";
    public override string ModuleVersion => PluginVersion ?? "n/a";

    public Config Config { get; set; } = new();

    public Dictionary<ulong, PlayerData> PlayerData = new();

    private CancellationTokenSource? _cts;
    private Task? _socketTask;
    public SocketIOClient.SocketIO? socket = null;

    string? hostAddress = null;
    string? hostPort = null;

    string CurrentMap = "";
    public override void Load(bool hotReload)
    {
        if (Config.ApiKey == null)
        {
            throw new Exception($"Invalid or no ApiKey set in Proximity Chat Config.");
        }

        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() =>
        {
            CurrentMap = Server.MapName;
            InitServer();
        });

        if (hotReload)
        {
            CurrentMap = Server.MapName;
            InitServer();
        }

        RegisterListener<Listeners.OnTick>(() =>
        {
            SaveAllPlayersPositions();
        });

        RegisterListener<Listeners.OnClientDisconnect>((playerSlot) =>
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player != null && player.IsValid)
            {
                PlayerData.Remove(player.SteamID);
            }
        });

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            CurrentMap = mapName;
            NotifyMapChange();
        });
    }


    public void InitServer()
    {
        var ipInt = ConVar.Find("hostip")?.GetPrimitiveValue<int>();
        if (ipInt != null)
        {
            byte[] bytes = BitConverter.GetBytes((int)ipInt);
            Array.Reverse(bytes);
            string ipString = new IPAddress(bytes).ToString();
            hostAddress = ipString != null ? ipString : null;
        }

        var port = ConVar.Find("hostport")?.GetPrimitiveValue<int>();
        hostPort = port != null ? port.ToString() : null;

        Server.NextFrame(() =>
        {
            if (_cts != null)
            {
                _cts.Cancel();
                //_socketTask?.Wait(); // optionally await
            }
            _cts = new CancellationTokenSource();
            _socketTask = Task.Run(() => InitSocketIO(_cts.Token));
        });
    }


    private async Task InitSocketIO(CancellationToken token)
    {
        var query = new List<KeyValuePair<string, string>>();
        if (Config.ApiKey != null)
        {
            query.Add(new KeyValuePair<string, string>("api-key", Config.ApiKey));
        }
        if (hostAddress != null)
        {
            query.Add(new KeyValuePair<string, string>("server-address", hostAddress));
        }
        if (hostPort != null)
        {
            query.Add(new KeyValuePair<string, string>("server-port", hostPort));
        }

        socket = new SocketIOClient.SocketIO(Config.SocketURL, new SocketIOOptions
        {
            ReconnectionAttempts = 3,
            Reconnection = true,
            Query = query
        });
        socket.OnConnected += async (sender, e) =>
        {
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var payload = MessagePackSerializer.Serialize(PlayerData.Values.ToList());
                    await socket.EmitAsync("player-positions", "proximity-chat", payload);
                    await Task.Delay(100, token);
                }
            }, token);

            NotifyMapChange();
        };
        await socket.ConnectAsync();
    }

    [ConsoleCommand("css_updatemap")]
    [RequiresPermissions("#css/admin")]
    public void Command_UpdateMap(CCSPlayerController? caller, CommandInfo info)
    {
        CurrentMap = Server.MapName;
        NotifyMapChange();
        info.ReplyToCommand($"Attempting to notify server of current map: {Server.MapName}");
    }

    private void NotifyMapChange()
    {
        if(socket == null || !socket.Connected)
        {
            return;
        }
        Console.WriteLine($"Notifying server of current map: {CurrentMap}");
        Task.Run(async () =>
        {
            await socket.EmitAsync("current-map", "proximity-chat", CurrentMap);
        });
    }


    [ConsoleCommand("css_savepositions")]
    [RequiresPermissions("#css/admin")]
    public void Command_savepositions(CCSPlayerController? caller, CommandInfo info)
    {
        SaveAllPlayersPositions();
    }

    public CBaseEntity? GetObserverEntity(CCSPlayerController? observer)
    {
        if (!IsValid(observer))
        {
            return null;
        }

        var observerPawn = observer!.ObserverPawn?.Value;
        if (observerPawn == null)
        {
            return null;
        }

        var observedEntity = observerPawn.ObserverServices?.ObserverTarget?.Value;
        if (observedEntity != null && observedEntity.IsValid)
        {
            return observedEntity;
        }

        return null;
    }

    public CCSPlayerController? GetObserverTarget(CCSPlayerController? observer)
    {
        var observedEntity = GetObserverEntity(observer);
        if (observedEntity == null || !observedEntity.IsValid)
        {
            return null;
        }

        if (observedEntity.DesignerName != "player")
        {
            return null;
        }

        var observedPlayerPawn = observedEntity.As<CCSPlayerPawn>();

        if (observedPlayerPawn != null && observedPlayerPawn.IsValid)
        {
            var controller = observedPlayerPawn.Controller.Value;
            if (controller == null)
            {
                return null;
            }

            var playerController = controller.As<CCSPlayerController>();
            if (IsValid(playerController))
            {
                return playerController;
            }
        }

        return null;
    }

    public void SaveAllPlayersPositions()
    {
        foreach (var player in Utilities.GetPlayers().Where(IsValid))
        {
            //if (player.IsBot) continue; // ignore bots

            bool useObserverPawn = false;
            if (player.PlayerPawn.Value!.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                // Keep the camera position in the same spot for a few seconds before teleporting to the player they're spectating
                float timeSinceDeath = Server.CurrentTime - player.PlayerPawn.Value.DeathTime;
                if (timeSinceDeath >= 3)
                {
                    useObserverPawn = true;
                }
            }
            SavePlayerData(player, useObserverPawn);
        }
    }


    public void SavePlayerData(CCSPlayerController? player, bool useObserverPawn)
    {
        if (player == null || !IsValid(player))
        {
            return;
        }

        if (player.AuthorizedSteamID?.SteamId64 == null)
        {
            return;
        }

        var playerSteamId = (ulong)player.AuthorizedSteamID.SteamId64;

        float OriginX = 0f;
        float OriginY = 0f;
        float OriginZ = 0f;
        float LookAtX = 0f;
        float LookAtY = 0f;
        float LookAtZ = 0f;

        CBasePlayerPawn? pawn = player!.PlayerPawn.Value;
        bool gotOriginAndAngles = false;
        if (useObserverPawn)
        {
            // This is only effective if cameras are forced for first person
            // TODO: find another method to get positions of players in freecam
            var observingTarget = GetObserverTarget(player);
            if (observingTarget != null && IsValid(observingTarget))
            {
                pawn = observingTarget.Pawn.Value;
            }

            if (GetObserverEntity(player)?.DesignerName == "planted_c4")
            {
                var vAngle = player.Pawn.Value!.V_angle.Clone();
                var c4Position = player.Pawn.Value!.AbsOrigin?.Clone();

                // Calculate third person position when spectating planted c4
                Vector forward = new();
                NativeAPI.AngleVectors(vAngle.Handle, forward.Handle, 0, 0);

                // TODO: Could we TraceRay to the c4 to detect if cameraPos is inside an object, and reduce camDistance until the trace is clear?
                const float camDistance = 100;
                float lowestZ = c4Position.Z + 25;

                Vector offset = new(
                    -forward.X * camDistance,
                    -forward.Y * camDistance,
                    -forward.Z * camDistance
                );
                Vector cameraPos = new(
                    c4Position.X + offset.X,
                    c4Position.Y + offset.Y,
                    c4Position.Z + offset.Z
                );


                OriginX = cameraPos.X;
                OriginY = cameraPos.Y;
                // Prevent the camera from going under the floor
                OriginZ = cameraPos.Z < lowestZ ? lowestZ : cameraPos.Z;

                LookAtX = c4Position.X;
                LookAtY = c4Position.Y;
                LookAtZ = c4Position.Z;

                gotOriginAndAngles = true;
            }
        }

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        if (!gotOriginAndAngles)
        {
            var origin = GetEyePosition(pawn)?.Clone();
            var angles = pawn.V_angle.Clone();
            if (origin == null || angles == null)
            {
                return;
            }

            OriginX = origin.X;
            OriginY = origin.Y;
            OriginZ = origin.Z;

            var LookAt = CalculateForward(origin, angles)!;
            LookAtX = LookAt.X;
            LookAtY = LookAt.Y;
            LookAtZ = LookAt.Z;
        }

        var playerIsAlive = IsAlive(player) ? 1 : 0;
        var Team = (int)player.TeamNum;

        if (!PlayerData.ContainsKey(playerSteamId))
        {
            PlayerData[playerSteamId] = new PlayerData(playerSteamId.ToString());
        }

        PlayerData[playerSteamId].Name = player.PlayerName;
        PlayerData[playerSteamId].SteamId = playerSteamId.ToString();

        // Scale up the floats and store them as integers
        PlayerData[playerSteamId].OriginX = (int)(OriginX * 10000);
        PlayerData[playerSteamId].OriginY = (int)(OriginY * 10000);
        PlayerData[playerSteamId].OriginZ = (int)(OriginZ * 10000);
        PlayerData[playerSteamId].LookAtX = (int)(LookAtX * 10000);
        PlayerData[playerSteamId].LookAtY = (int)(LookAtY * 10000);
        PlayerData[playerSteamId].LookAtZ = (int)(LookAtZ * 10000);

        PlayerData[playerSteamId].Team = Team;
        PlayerData[playerSteamId].IsAlive = playerIsAlive == 1 ? true : false;
    }

    public bool IsValid(CCSPlayerController? playerController)
    {
        if (playerController == null)
            return false;
        if (playerController.IsValid == false)
            return false;
        if (playerController.IsHLTV)
            return false;
        if (playerController.Connected != PlayerConnectedState.PlayerConnected)
            return false;
        if (playerController.PlayerPawn?.Value == null)
            return false;
        if (playerController.PlayerPawn.IsValid == false)
            return false;

        return true;
    }

    public bool IsAlive(CCSPlayerController? player)
    {
        if (player != null && IsValid(player))
        {
            if (player!.Pawn.Value!.LifeState == (byte)LifeState_t.LIFE_ALIVE)
            {
                return true;
            }
        }
        return false;
    }

    public Vector? CalculateForward(Vector origin, QAngle angle)
    {
        Vector _forward = new();
        NativeAPI.AngleVectors(angle.Handle, _forward.Handle, 0, 0);
        Vector _endOrigin = new(
            origin.X + _forward.X * 8192,
            origin.Y + _forward.Y * 8192,
            origin.Z + _forward.Z * 8192
        );
        return _endOrigin;
    }

    public Vector? GetEyePosition<T>(T? playerPawn)
        where T : CBasePlayerPawn
    {
        if (
            playerPawn == null
            || !playerPawn.IsValid
            || playerPawn.CameraServices == null
            || playerPawn.AbsOrigin == null
        )
            return null;

        var absOrigin = playerPawn.AbsOrigin.Clone();
        var cameraServices = playerPawn.CameraServices;
        return new Vector(
            absOrigin.X,
            absOrigin.Y,
            absOrigin.Z + cameraServices.OldPlayerViewOffsetZ
        );
    }

    public override void Unload(bool hotReload)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _socketTask?.Wait();
            _cts.Dispose();
            _cts = null;
            _socketTask = null;
        }

        if (socket != null && socket.Connected)
        {
            socket.Dispose();
            socket = null;
        }
    }

    public void OnConfigParsed(Config config)
    {
        this.Config = config;
    }

    public AssemblyName assemblyName = typeof(ProximityChat).Assembly.GetName();
    public string? AssemblyVersion => assemblyName.Version?.ToString(); // Version: x.x.x.x
    public string? PluginVersion => AssemblyVersion?.Remove(AssemblyVersion.Length - 2); // truncate to x.x.x
}

static class VectorExtensions
{
    public static Vector Clone(this Vector vector)
    {
        return new Vector(vector.X, vector.Y, vector.Z);
    }
}

static class QAngleExtensions
{
    public static QAngle Clone(this QAngle angle)
    {
        return new QAngle(angle.X, angle.Y, angle.Z);
    }
}

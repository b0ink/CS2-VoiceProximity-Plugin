using System.Collections.Generic;
using System.Net;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using MessagePack;
using Microsoft.Extensions.Logging;
using SocketIOClient;

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

    public bool DEBUG_FAKE_PLAYERS = false;

    public override void Load(bool hotReload)
    {
        if (Config.ApiKey == null)
        {
            Logger.LogError($"Invalid no Api Key set in Proximity Chat Config.");
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

            for (int i = 0; i < Server.MaxPlayers; i++)
            {
                var player = Utilities.GetPlayerFromSlot(i);

                if (player == null || !player.IsValid)
                {
                    continue;
                }

                // Prevent players from being removed during map changes
                if (player.Connected == PlayerConnectedState.PlayerDisconnected)
                {
                    var authorisedSteamID = player.AuthorizedSteamID?.SteamId64;
                    if (authorisedSteamID != null && PlayerData.ContainsKey((ulong)authorisedSteamID))
                    {
                        PlayerData.Remove((ulong)authorisedSteamID);
                    }
                    else if (PlayerData.ContainsKey(player.SteamID))
                    {
                        PlayerData.Remove(player.SteamID);
                    }
                }
            }
        });

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            CurrentMap = mapName;
            NotifyMapChange();
            NotifyServerConfig();
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

        socket = new SocketIOClient.SocketIO(
            Config.SocketURL,
            new SocketIOOptions
            {
                ReconnectionAttempts = 3,
                Reconnection = true,
                Query = query,
            }
        );
        socket.OnConnected += (sender, e) =>
        {
            _ = Task.Run(
                async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        var payload = MessagePackSerializer.Serialize(PlayerData.Values.ToList());

                        _ = socket.EmitAsync("player-positions", "proximity-chat", payload);
                        await Task.Delay(100, token);
                    }
                },
                token
            );

            NotifyMapChange();
            NotifyServerConfig();
        };

        socket.OnDisconnected += (sender, e) =>
        {
            Logger.LogError($"Socket disconnected. Please ensure your Api Key is set correctly and you are using the correct SocketURL (Region).");
        };

        // Custom errors from the API
        socket.On(
            "exception",
            (SocketIOResponse error) =>
            {
                Logger.LogError(error.GetValue<string>(0).ToString());
            }
        );

        await socket.ConnectAsync();
    }

    private void NotifyMapChange()
    {
        if (socket == null || !socket.Connected)
        {
            return;
        }
        Console.WriteLine($"Notifying server of current map: {CurrentMap}");
        Task.Run(async () =>
        {
            await socket.EmitAsync("current-map", "proximity-chat", CurrentMap);
        });
    }

    private void NotifyServerConfig()
    {
        if (socket == null || !socket.Connected)
        {
            return;
        }
        Config.Reload();
        var payload = MessagePackSerializer.Serialize(Config);
        Task.Run(async () =>
        {
            await socket.EmitAsync("server-config", "proximity-chat", payload);
        });
    }

    [ConsoleCommand("css_fakeplayers")]
    [RequiresPermissions("#css/admin")]
    public void Command_fakeplayers(CCSPlayerController? caller, CommandInfo info)
    {
        DEBUG_FAKE_PLAYERS = !DEBUG_FAKE_PLAYERS;
        info.ReplyToCommand($"fake players?: {DEBUG_FAKE_PLAYERS}");
    }

    [ConsoleCommand("css_updatemap")]
    [RequiresPermissions("#css/admin")]
    public void Command_UpdateMap(CCSPlayerController? caller, CommandInfo info)
    {
        CurrentMap = Server.MapName;
        NotifyMapChange();
        info.ReplyToCommand($"Attempting to notify server of current map: {Server.MapName}");
    }

    [ConsoleCommand("css_updateconfig")]
    [RequiresPermissions("#css/admin")]
    public void Command_UpdateConfig(CCSPlayerController? caller, CommandInfo info)
    {
        NotifyServerConfig();
    }

    [ConsoleCommand("css_savepositions")]
    [RequiresPermissions("#css/admin")]
    public void Command_SavePositions(CCSPlayerController? caller, CommandInfo info)
    {
        SaveAllPlayersPositions();
    }

    [ConsoleCommand("css_setrollofffactor")]
    [CommandHelper(1, "<factor>")]
    [RequiresPermissions("#css/admin")]
    public void Command_SetRolloffFactor(CCSPlayerController? caller, CommandInfo info)
    {
        var sRolloffFactor = info.GetArg(1);
        float rolloffFactor;
        if (!float.TryParse(sRolloffFactor, out rolloffFactor))
        {
            info.ReplyToCommand($"Invalid input: {sRolloffFactor}");
            return;
        }
        Config.RolloffFactor = rolloffFactor;
        info.ReplyToCommand($"Saved RolloffFactor to {rolloffFactor}");
        NotifyServerConfig();
    }

    [ConsoleCommand("css_setrefdistance")]
    [CommandHelper(1, "<factor>")]
    [RequiresPermissions("#css/admin")]
    public void Command_SetRefDistance(CCSPlayerController? caller, CommandInfo info)
    {
        var sRefDistance = info.GetArg(1);
        float refDistance;
        if (!float.TryParse(sRefDistance, out refDistance))
        {
            info.ReplyToCommand($"Invalid input: {sRefDistance}");
            return;
        }
        Config.RefDistance = refDistance;
        info.ReplyToCommand($"Saved RefDistance to {refDistance}");
        NotifyServerConfig();
    }

    [ConsoleCommand("css_setdeadplayermutedelay")]
    [CommandHelper(1, "<delay>")]
    [RequiresPermissions("#css/admin")]
    public void Command_SetDeadPlayerMuteDelay(CCSPlayerController? caller, CommandInfo info)
    {
        var sMuteDelay = info.GetArg(1);
        float muteDelay;
        if (!float.TryParse(sMuteDelay, out muteDelay))
        {
            info.ReplyToCommand($"Invalid input: {sMuteDelay}");
            return;
        }
        Config.DeadPlayerMuteDelay = muteDelay;
        info.ReplyToCommand($"Saved MuteDelay to {muteDelay}");
        NotifyServerConfig();
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

    public ObserverMode_t? GetObserverMode(CCSPlayerController? observer)
    {
        if (observer == null || !IsValid(observer))
        {
            return ObserverMode_t.OBS_MODE_NONE;
        }

        var observerServices = observer.Pawn.Value?.ObserverServices;
        if (observerServices != null)
        {
            var observerMode = observerServices.ObserverMode;
            return (ObserverMode_t)observerMode;
        }
        return ObserverMode_t.OBS_MODE_NONE;
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

        bool spectatingC4 = false;

        CBasePlayerPawn? pawn = player!.PlayerPawn.Value;
        bool gotOriginAndAngles = false;
        if (useObserverPawn)
        {
            var observerMode = GetObserverMode(player);
            var observingTarget = GetObserverTarget(player);
            if (observingTarget != null && IsValid(observingTarget))
            {
                pawn = observingTarget.Pawn.Value;
            }

            if (GetObserverEntity(player)?.DesignerName == "planted_c4")
            {
                var vAngle = player.Pawn.Value!.V_angle.Clone();
                var c4Position = player.Pawn.Value!.AbsOrigin?.Clone();

                if (c4Position == null)
                {
                    return;
                }

                // Calculate third person position when spectating planted c4
                Vector forward = new();
                NativeAPI.AngleVectors(vAngle.Handle, forward.Handle, 0, 0);

                // TODO: Could we TraceRay to the c4 to detect if cameraPos is inside an object, and reduce camDistance until the trace is clear?
                const float camDistance = 100;
                float lowestZ = c4Position.Z + 25;

                Vector offset = new(-forward.X * camDistance, -forward.Y * camDistance, -forward.Z * camDistance);
                Vector cameraPos = new(c4Position.X + offset.X, c4Position.Y + offset.Y, c4Position.Z + offset.Z);

                OriginX = cameraPos.X;
                OriginY = cameraPos.Y;
                // Prevent the camera from going under the floor
                OriginZ = cameraPos.Z < lowestZ ? lowestZ : cameraPos.Z;

                LookAtX = c4Position.X;
                LookAtY = c4Position.Y;
                LookAtZ = c4Position.Z;

                gotOriginAndAngles = true;
                spectatingC4 = true;
            }
            else if (observerMode == ObserverMode_t.OBS_MODE_ROAMING)
            {
                QAngle angles = player.Pawn.Value!.V_angle.Clone();
                Vector? origin = GetFreecamPlayerPosition(player);
                if (origin != null)
                {
                    OriginX = origin.X;
                    OriginY = origin.Y;
                    OriginZ = origin.Z;

                    var LookAt = CalculateForward(origin, angles)!;

                    LookAtX = LookAt.X;
                    LookAtY = LookAt.Y;
                    LookAtZ = LookAt.Z;
                    gotOriginAndAngles = true;
                }
            }
            else if (observerMode == ObserverMode_t.OBS_MODE_CHASE && pawn!.DesignerName == "player")
            {
                var vAngle = player.Pawn.Value!.V_angle.Clone();
                var position = GetEyePosition(pawn);
                if (position == null)
                {
                    return;
                }

                // Calculate third person position when spectating a player in thirdperson view (Chase cam)
                Vector forward = new();
                NativeAPI.AngleVectors(vAngle.Handle, forward.Handle, 0, 0);

                const float camDistance = 150; // matches cam_idealdist cvar

                Vector offset = new(-forward.X * camDistance, -forward.Y * camDistance, -forward.Z * camDistance);
                Vector cameraPos = new(position.X + offset.X, position.Y + offset.Y, position.Z + offset.Z);

                OriginX = cameraPos.X;
                OriginY = cameraPos.Y;
                OriginZ = cameraPos.Z;
                LookAtX = position.X;
                LookAtY = position.Y;
                LookAtZ = position.Z;
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
        var Team = player.TeamNum;
        // csharpier-ignore-start
        SaveData(playerSteamId, player.PlayerName, OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, Team, playerIsAlive, spectatingC4);
        if (DEBUG_FAKE_PLAYERS)
        {
            SaveData(10000000000000001, "bob", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 2, playerIsAlive, spectatingC4);
            SaveData(10000000000000002, "john", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 2, playerIsAlive, spectatingC4);
            SaveData(10000000000000003, "april", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 2, playerIsAlive, spectatingC4);
            SaveData(10000000000000004, "carl", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 2, playerIsAlive, spectatingC4);
            SaveData(10000000000000005, "ethan", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 3, playerIsAlive, spectatingC4);
            SaveData(10000000000000006, "franny", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 3, playerIsAlive, spectatingC4);
            SaveData(10000000000000007, "gunter", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 3, playerIsAlive, spectatingC4);
            SaveData(10000000000000008, "ian", OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, 3, playerIsAlive, spectatingC4);
            SaveData(10000000000000009, "BOINK", 1283, -309, -100, LookAtX, LookAtY, LookAtZ, 3, playerIsAlive, spectatingC4);
        }
        // csharpier-ignore-end
    }

    public void SaveData(ulong playerSteamId, string playerName, float OriginX, float OriginY, float OriginZ, float LookAtX, float LookAtY, float LookAtZ, byte Team, int playerIsAlive, bool spectatingC4)
    {
        if (!PlayerData.ContainsKey(playerSteamId))
        {
            PlayerData[playerSteamId] = new PlayerData(playerSteamId.ToString(), playerName);
        }

        PlayerData[playerSteamId].Name = playerName;
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
        PlayerData[playerSteamId].SpectatingC4 = spectatingC4;
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
        Vector _endOrigin = new(origin.X + _forward.X * 8192, origin.Y + _forward.Y * 8192, origin.Z + _forward.Z * 8192);
        return _endOrigin;
    }

    public Vector? GetEyePosition<T>(T? playerPawn)
        where T : CBasePlayerPawn
    {
        if (playerPawn == null || !playerPawn.IsValid || playerPawn.CameraServices == null || playerPawn.AbsOrigin == null)
            return null;

        var absOrigin = playerPawn.AbsOrigin.Clone();
        var cameraServices = playerPawn.CameraServices;
        return new Vector(absOrigin.X, absOrigin.Y, absOrigin.Z + cameraServices.OldPlayerViewOffsetZ);
    }

    public Vector? GetFreecamPlayerPosition(CCSPlayerController? player)
    {
        if (player == null || !IsValid(player))
        {
            return null;
        }
        return player.Pawn.Value!.CBodyComponent?.SceneNode?.GetSkeletonInstance().AbsOrigin.Clone() ?? null;
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

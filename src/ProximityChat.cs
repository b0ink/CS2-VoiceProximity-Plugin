using System.Net;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using MessagePack;
using Microsoft.Extensions.Logging;
using SocketIOClient;

namespace ProximityChat;

public class ExceptionPayload
{
    public int Code { get; set; }
    public string? Message { get; set; }
}

public partial class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Proximity Chat API";
    public override string ModuleAuthor => "b0ink";
    public override string ModuleVersion => PluginVersion ?? "n/a";

    public Config Config { get; set; } = new();

    public Dictionary<ulong, PlayerData> PlayerData = new();

    private CancellationTokenSource? _cts;
    private Task? _socketTask;
    public SocketIOClient.SocketIO? socket = null;

    public string? hostAddress = null;
    public string? hostPort = null;

    public string CurrentMap = "";

    public bool DEBUG_FAKE_PLAYERS = false;

    public bool tryReconnectSocket = true;

    public Vector debugPlayerPosition = new Vector();

    public override void Load(bool hotReload)
    {
        if (Config.ApiKey == null)
        {
            Logger.LogError($"Invalid no Api Key set in Proximity Chat Config.");
            throw new Exception($"Invalid or no ApiKey set in Proximity Chat Config.");
        }

        CurrentMap = Server.MapName;

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

        RegisterListener<Listeners.OnClientAuthorized>(
            (slot, steamId) =>
            {
                CheckAdmin(steamId);
            }
        );

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            CurrentMap = mapName;
            if (_cts == null && _socketTask == null && (socket == null || !socket.Connected))
            {
                Logger.LogInformation("OnMapStart: Init server");
                InitServer();
            }
            else
            {
                NotifyMapChange();
                NotifyServerConfig();
            }
        });

        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            // Triggers all players to have mono audio until they join a team again
            foreach (var player in PlayerData)
            {
                player.Value.IsAlive = false;
                player.Value.Team = 0;
            }
        });
    }

    public void CheckAdmin(SteamID? steamId)
    {
        if (steamId?.SteamId64 == null)
        {
            return;
        }

        var steamId64 = steamId.SteamId64;
        if (!PlayerData.ContainsKey((ulong)steamId64))
        {
            PlayerData[steamId64] = new PlayerData(steamId64.ToString(), $"{steamId64}");
        }

        PlayerData[steamId64].IsAdmin = false;

        var adminFlags = Config.ServerConfigAdmins.Split(",").ToList();
        if (adminFlags != null)
        {
            foreach (var flag in adminFlags)
            {
                if (AdminManager.PlayerHasPermissions(steamId, flag) || AdminManager.PlayerInGroup(steamId, flag))
                {
                    PlayerData[steamId64].IsAdmin = true;
                }
            }
        }
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
                _socketTask?.Wait();
                _cts.Dispose();
                _cts = null;
                _socketTask = null;
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
        if (PluginVersion != null)
        {
            query.Add(new KeyValuePair<string, string>("plugin-version", PluginVersion));
        }

        if (socket == null || !socket.Connected)
        {
            socket = new SocketIOClient.SocketIO(
                Config.SocketURL,
                new SocketIOOptions
                {
                    //ReconnectionAttempts = 3,
                    Reconnection = false,
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

                Server.NextFrame(() =>
                {
                    AddTimer(
                        3,
                        () =>
                        {
                            NotifyMapChange();
                            NotifyServerConfig();
                        }
                    );
                });
            };

            socket.On(
                "server-config",
                (data) =>
                {
                    try
                    {
                        var bytes = data.GetValue<byte[]>();
                        if (bytes != null)
                        {
                            var updatedConfig = MessagePackSerializer.Deserialize<Config>(bytes);
                            Config.DeadPlayerMuteDelay = updatedConfig.DeadPlayerMuteDelay;
                            Config.AllowDeadTeamVoice = updatedConfig.AllowDeadTeamVoice;
                            Config.AllowSpectatorC4Voice = updatedConfig.AllowSpectatorC4Voice;
                            Config.VolumeFalloffFactor = updatedConfig.VolumeFalloffFactor;
                            Config.VolumeMaxDistance = updatedConfig.VolumeMaxDistance;
                            Config.OcclusionNear = updatedConfig.OcclusionNear;
                            Config.OcclusionFar = updatedConfig.OcclusionFar;
                            Config.OcclusionEndDist = updatedConfig.OcclusionEndDist;
                            Config.OcclusionFalloffExponent = updatedConfig.OcclusionFalloffExponent;
                            Config.AlwaysHearVisiblePlayers = updatedConfig.AlwaysHearVisiblePlayers;
                            Server.NextFrame(() =>
                            {
                                Config.Update();
                            });
                            Console.WriteLine($"Config has been updated by client");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex.Message);
                    }
                }
            );

            socket.OnDisconnected += (sender, e) =>
            {
                Logger.LogError($"Socket disconnected. You can try reconnecting by changing the map, or restarting the server.");
                if (_cts != null)
                {
                    _cts.Cancel();
                    _socketTask?.Wait();
                    _cts.Dispose();
                    _cts = null;
                    _socketTask = null;
                }
                Server.NextFrame(() =>
                {
                    if (tryReconnectSocket)
                    {
                        AddTimer(
                            1,
                            () =>
                            {
                                InitServer();
                            }
                        );
                    }
                });
            };

            // Custom errors from the API
            socket.On(
                "exception",
                (SocketIOResponse error) =>
                {
                    var payload = error.GetValue<ExceptionPayload>(0);
                    Logger.LogError(payload?.Message ?? "Unknown socket exception occurred.");
                    tryReconnectSocket = false;
                }
            );

            await socket.ConnectAsync();
        }
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
        Server.NextFrame(() =>
        {
            Config.Reload();
            var payload = MessagePackSerializer.Serialize(Config);
            Task.Run(async () =>
            {
                await socket.EmitAsync("server-config", "proximity-chat", payload);
            });
        });
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

        if (!PlayerData.ContainsKey(playerSteamId))
        {
            PlayerData[playerSteamId] = new PlayerData(playerSteamId.ToString(), player.PlayerName);
        }
        if (PlayerData[playerSteamId].IsAdmin == null)
        {
            CheckAdmin(player.AuthorizedSteamID);
        }
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
            SaveData(10000000000000009, "BOINK", debugPlayerPosition.X, debugPlayerPosition.Y, debugPlayerPosition.Z, LookAtX, LookAtY, LookAtZ, Team, playerIsAlive, spectatingC4);
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

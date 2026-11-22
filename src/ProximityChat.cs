using System.Collections.Specialized;
using System.Net;
using System.Reflection;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using MessagePack;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using CounterStrikeTrace = CounterStrikeSharp.API.Modules.Utils.Trace;

namespace ProximityChat;

public class ExceptionPayload
{
    public int code { get; set; }
    public string? message { get; set; }
}

public class ServerRestartWarning
{
    public float minutes { get; set; }
}

[MinimumApiVersion(380)]
public partial class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Proximity Chat API";
    public override string ModuleAuthor => "b0ink";
    public override string ModuleVersion => PluginVersion ?? "n/a";

    public Config Config { get; set; } = new();

    public Dictionary<ulong, PlayerData> PlayerData = new();
    private readonly Dictionary<int, (uint ControllerHandle, ulong SteamId)> _authorizedSteamIds = new();

    private CancellationTokenSource? _cts;
    private Task? _socketTask;
    public SocketIOClient.SocketIO? socket = null;

    public string? hostAddress = null;
    public string? hostPort = null;

    public string CurrentMap = "";

    public bool DEBUG_FAKE_PLAYERS = false;

    public bool tryReconnectSocket = true;

    public Vector debugPlayerPosition = new Vector();

    public Dictionary<string, int> DoorRotations = new();
    public List<CPropDoorRotating?> DoorEntities = new();

    public List<string?> fakeBots = new();

    private const float OcclusionUpdateIntervalSeconds = 0.1f;
    private const int OcclusionWindowTicks = 6;
    private const int MinOcclusionTraceQuality = 1;
    private const int MaxOcclusionTraceQuality = 5;
    private const float OcclusionOffsetWallClearance = 1f;

    // Pairs run sequentially on the game thread; these points never enter a snapshot or result cache.
    private readonly Vector _occlusionSoundLeft = new();
    private readonly Vector _occlusionSoundRight = new();
    private readonly Vector _occlusionListenerLeft = new();
    private readonly Vector _occlusionListenerRight = new();
    private static readonly TraceOptions OcclusionTraceOptions = new() { InteractsWith = Contents.WorldGeometry | Contents.Solid | Contents.Window, InteractsExclude = Contents.Player | Contents.Npc };
    public int OcclusionTraceQuality { get; private set; } = 5;

    private Task _playerPositionsEmitTask = Task.CompletedTask;
    private float _lastOcclusionCycleStartedAt = float.NegativeInfinity;

    private bool _occlusionCycleInProgress = false;
    private int _occlusionTicksRemaining = 0;
    private int _occlusionPairIndex = 0;
    private int _cycleTraceQuality = 5;
    private readonly List<(OcclusionSnapshot from, OcclusionSnapshot to)> _occlusionPairs = new();

    // Persistent per-player trace state, so steady-state cycles allocate no vectors, rows, or key strings.
    private readonly Dictionary<ulong, OcclusionSnapshot> _occlusionParticipants = new();
    private readonly List<OcclusionSnapshot> _occlusionCycleParticipants = new();
    private readonly List<ulong> _occlusionPruneScratch = new();
    private int _occlusionCycleId = 0;

    private sealed class OcclusionSnapshot
    {
        public Vector Origin { get; }
        public CBaseEntity IgnoreEntity { get; set; }

        public ulong SteamId { get; init; }
        public string SteamIdText { get; set; } = "";
        public int CycleId { get; set; } = -1;
        public Dictionary<string, float>? PendingFractions { get; set; }
        public Dictionary<string, float>? SpareFractions { get; set; }

        public OcclusionSnapshot(Vector origin, CBaseEntity ignoreEntity)
        {
            Origin = origin;
            IgnoreEntity = ignoreEntity;
        }
    }

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
            foreach (var door in DoorEntities)
            {
                if (door == null || !door.IsValid || door.AbsOrigin == null || door.AbsRotation == null)
                {
                    continue;
                }
                var rotation = door.AbsRotation.Clone();
                var doorId = GetDoorKey(door.AbsOrigin);
                int currentRotation = (int)Math.Floor(rotation.Y);
                if (!DoorRotations.ContainsKey(doorId))
                {
                    DoorRotations[doorId] = currentRotation;
                }

                int lastDoorRotation = DoorRotations[doorId];

                if (currentRotation != lastDoorRotation)
                {
                    DoorRotations[doorId] = currentRotation;

                    var origin = door.AbsOrigin.Clone();
                    QueueSocketEmit("door-rotation", new object[] { "proximity-chat", $"{origin.X} {origin.Y} {origin.Z}", currentRotation });
                }
            }

            var now = Server.CurrentTime;
            TickPlayerPositionPipeline(now);

            for (int i = 0; i < Server.MaxPlayers; i++)
            {
                var player = Utilities.GetPlayerFromSlot(i);

                if (player == null || !player.IsValid)
                {
                    continue;
                }

                // Prevent players from being removed during map changes
                if (player.Connected == PlayerConnectedState.Disconnected)
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

        RegisterListener<Listeners.OnClientConnected>(slot => _authorizedSteamIds.Remove(slot));
        RegisterListener<Listeners.OnClientDisconnect>(slot => _authorizedSteamIds.Remove(slot));

        RegisterListener<Listeners.OnClientAuthorized>(
            (slot, steamId) =>
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player != null && player.IsValid)
                {
                    _authorizedSteamIds[slot] = (player.EntityHandle.Raw, steamId.SteamId64);
                }
                CheckAdmin(steamId);
            }
        );

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            ResetOcclusionCycleState();
            _lastOcclusionCycleStartedAt = float.NegativeInfinity;
            foreach (var playerData in PlayerData.Values)
            {
                playerData.OcclusionFraction.Clear();
            }
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
            ResetOcclusionCycleState();
            // Triggers all players to have mono audio until they join a team again
            foreach (var player in PlayerData)
            {
                player.Value.IsAlive = false;
                player.Value.Team = 0;
            }
        });
    }

    public void InitServer()
    {
        var ipInt = ConVar<int>.Find("hostip")?.Value;
        if (ipInt != null)
        {
            byte[] bytes = BitConverter.GetBytes((int)ipInt);
            Array.Reverse(bytes);
            string ipString = new IPAddress(bytes).ToString();
            hostAddress = ipString != null ? ipString : null;
        }

        var port = ConVar<int>.Find("hostport")?.Value;
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
            _socketTask = Task.Run(() => InitSocketIO());
        });
    }

    private async Task InitSocketIO()
    {
        var query = new NameValueCollection();
        if (Config.ApiKey != null)
        {
            query["api-key"] = Config.ApiKey;
        }
        if (hostAddress != null)
        {
            query["server-address"] = hostAddress;
        }
        if (hostPort != null)
        {
            query["server-port"] = hostPort;
        }
        if (PluginVersion != null)
        {
            query["plugin-version"] = PluginVersion;
        }

        if (socket == null || !socket.Connected)
        {
            socket = new SocketIOClient.SocketIO(
                new Uri(Config.SocketURL ?? throw new InvalidOperationException("SocketURL is not configured.")),
                new SocketIOOptions
                {
                    //ReconnectionAttempts = 3,
                    Reconnection = false,
                    Query = query,
                }
            );
            socket.OnConnected += (sender, e) =>
            {
                Server.NextFrame(() =>
                {
                    Logger.LogInformation("Socket connected successfully.");
                    _playerPositionsEmitTask = Task.CompletedTask;
                    _lastOcclusionCycleStartedAt = float.NegativeInfinity;
                    ResetOcclusionCycleState();

                    AddTimer(3, () =>
                    {
                        NotifyMapChange();
                        NotifyServerConfig();
                    });
                });
            };

            socket.On("server-config", (data) =>
            {
                try
                {
                    var bytes = data.GetValue<byte[]>(0);
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
                        Config.OcclusionFalloffFactor = updatedConfig.OcclusionFalloffFactor;
                        Config.AlwaysHearVisiblePlayers = updatedConfig.AlwaysHearVisiblePlayers;
                        Config.DeadVoiceFilterFrequency = updatedConfig.DeadVoiceFilterFrequency;
                        Config.SpectatorsCanTalk = updatedConfig.SpectatorsCanTalk;

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

                return Task.CompletedTask;
            });

            socket.On("server-restart-warning", (data) =>
            {
                var payload = data.GetValue<ServerRestartWarning>(0);
                if (payload == null)
                {
                    return Task.CompletedTask;
                }

                Server.NextFrame(() =>
                {
                    Logger.LogWarning($"Socket server will restart in {payload.minutes * 60} seconds. Users will reconnect automatically.");
                    Server.PrintToChatAll($" {ChatColors.Green}Proximity Chat {ChatColors.Default}| {ChatColors.Red}API server will restart in {ChatColors.Default}{payload.minutes * 60} {ChatColors.Red}seconds. Users will reconnect automatically.");
                });

                return Task.CompletedTask;
            });

            socket.OnDisconnected += (sender, e) =>
            {
                Logger.LogError("Socket disconnected. Please restart the server if it does not reconnect automatically.");
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
                    ResetOcclusionCycleState();
                    _lastOcclusionCycleStartedAt = float.NegativeInfinity;
                    if (tryReconnectSocket)
                    {
                        AddTimer(1, () =>
                        {
                            InitServer();
                        });
                    }
                });
            };

            // Custom errors from the API
            socket.On("exception", (error) =>
            {
                var payload = error.GetValue<ExceptionPayload>(0);
                Log(payload?.message ?? "Unknown socket exception occurred.", ConsoleColor.Red);
                tryReconnectSocket = false;

                return Task.CompletedTask;
            });

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
        QueueSocketEmit("current-map", new object[] { "proximity-chat", CurrentMap });
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
            QueueSocketEmit("server-config", new object[] { "proximity-chat", payload });
        });
    }

    // SocketIOClient 4.0.5 does not serialize emits. A binary event is a text header followed by an attachment written in
    // 8 KB websocket fragments; a concurrent emit lands between them (or inside them as a continuation frame), corrupting
    // the stream until the API drops the connection. Every emit from this plugin must go through this gate.
    private readonly SemaphoreSlim _socketEmitGate = new(1, 1);

    private async Task EmitSerializedAsync(SocketIOClient.SocketIO activeSocket, string eventName, object[] data)
    {
        await _socketEmitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await activeSocket.EmitAsync(eventName, data).ConfigureAwait(false);
        }
        finally
        {
            _socketEmitGate.Release();
        }
    }

    private void QueueSocketEmit(string eventName, object[] data)
    {
        var activeSocket = socket;
        if (activeSocket == null)
        {
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await EmitSerializedAsync(activeSocket, eventName, data);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to send {EventName}.", eventName);
            }
        });
    }

    public bool TrySetOcclusionTraceQuality(int quality)
    {
        if (quality < MinOcclusionTraceQuality || quality > MaxOcclusionTraceQuality)
        {
            return false;
        }

        SetOcclusionTraceQuality(quality);
        return true;
    }

    public void SetOcclusionTraceQuality(int quality)
    {
        OcclusionTraceQuality = Math.Clamp(quality, MinOcclusionTraceQuality, MaxOcclusionTraceQuality);
    }

    public int GetTraceCountForQuality(int quality)
    {
        int clamped = Math.Clamp(quality, MinOcclusionTraceQuality, MaxOcclusionTraceQuality);
        if (clamped == 1)
        {
            return 1;
        }
        if (clamped == 2)
        {
            return 3;
        }
        if (clamped == 3)
        {
            return 5;
        }
        if (clamped == 4)
        {
            return 7;
        }
        return 9;
    }

    public void SaveAllPlayersPositions()
    {
        var players = Utilities.GetPlayers().Where(IsValid).ToList();
        CapturePlayerPositions(players);
        _lastOcclusionCycleStartedAt = Server.CurrentTime;
        BeginOcclusionCycle(players);
        while (_occlusionCycleInProgress)
        {
            ProcessOcclusionCycleTick();
        }
    }

    private void TickPlayerPositionPipeline(float now)
    {
        if (now < _lastOcclusionCycleStartedAt)
        {
            ResetOcclusionCycleState();
            _lastOcclusionCycleStartedAt = float.NegativeInfinity;
        }

        if (socket == null || !socket.Connected)
        {
            ResetOcclusionCycleState();
            return;
        }

        var players = Utilities.GetPlayers().Where(IsValid).ToList();
        var playerList = CapturePlayerPositions(players);

        // Send current poses before raycasting, using the last completed occlusion results.
        // If the transport is busy, sample again next tick instead of queuing stale snapshots.
        if (_playerPositionsEmitTask.IsCompleted)
        {
            var payload = MessagePackSerializer.Serialize(playerList);
            var activeSocket = socket;
            // EmitAsync's synchronous prologue (packet building, websocket send setup) cost ~0.4 ms per tick on the game thread.
            // Only one pose send is in flight, and the emit gate keeps other events from interleaving with it.
            _playerPositionsEmitTask = Task.Run(() => EmitPlayerPositionsAsync(activeSocket, payload));
        }

        if (!_occlusionCycleInProgress && now - _lastOcclusionCycleStartedAt >= OcclusionUpdateIntervalSeconds)
        {
            _lastOcclusionCycleStartedAt = now;
            BeginOcclusionCycle(players);
        }

        if (_occlusionCycleInProgress)
        {
            ProcessOcclusionCycleTick();
        }
    }

    private async Task EmitPlayerPositionsAsync(SocketIOClient.SocketIO activeSocket, byte[] payload)
    {
        try
        {
            await EmitSerializedAsync(activeSocket, "player-positions", new object[] { "proximity-chat", payload });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send player positions.");
        }
    }

    private List<PlayerData> CapturePlayerPositions(IReadOnlyList<CCSPlayerController> players)
    {
        var playerList = new List<PlayerData>(players.Count);
        foreach (var player in players)
        {
            var playerPawn = player.PlayerPawn?.Value;
            if (playerPawn == null || !playerPawn.IsValid)
            {
                continue;
            }

            bool useObserverPawn = false;
            if (playerPawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                // Keep the camera position in the same spot for a few seconds before teleporting to the player they're spectating
                float timeSinceDeath = Server.CurrentTime - playerPawn.DeathTime;
                if (timeSinceDeath >= 3)
                {
                    useObserverPawn = true;
                }
            }

            SavePlayerData(player, useObserverPawn);

            ulong steamId = GetPlayerDataSteamId(player);
            if (steamId == 0)
            {
                continue;
            }

            if (!PlayerData.TryGetValue(steamId, out var playerData))
            {
                continue;
            }

            playerList.Add(playerData);
        }

        return playerList;
    }

    private void BeginOcclusionCycle(IReadOnlyList<CCSPlayerController> players)
    {
        ResetOcclusionCycleState();
        PruneOcclusionParticipants();
        int cycleId = ++_occlusionCycleId;

        foreach (var player in players)
        {
            var playerPawn = player.PlayerPawn?.Value;
            var steamId = GetPlayerDataSteamId(player);
            if (playerPawn == null || !playerPawn.IsValid || steamId == 0 || !PlayerData.TryGetValue(steamId, out var playerData))
            {
                continue;
            }

            if (!_occlusionParticipants.TryGetValue(steamId, out var participant))
            {
                // One Vector per player keeps its native handle; a new Vector per cycle is tracked by CounterStrikeSharp until collected.
                participant = new OcclusionSnapshot(new Vector(), playerPawn) { SteamId = steamId };
                _occlusionParticipants[steamId] = participant;
            }
            else if (participant.CycleId == cycleId)
            {
                // Duplicate IDs (e.g. repeated fake-bot names) are traced once.
                continue;
            }

            SetOcclusionPoint(participant.Origin, playerData.OriginX / 10000f, playerData.OriginY / 10000f, playerData.OriginZ / 10000f);
            participant.IgnoreEntity = playerPawn;
            participant.SteamIdText = playerData.SteamId;
            participant.CycleId = cycleId;
            // Refill the row published two cycles ago; Clear keeps its capacity.
            var row = participant.SpareFractions ?? new Dictionary<string, float>(players.Count);
            participant.SpareFractions = null;
            row.Clear();
            participant.PendingFractions = row;
            _occlusionCycleParticipants.Add(participant);
        }

        for (int i = 0; i < _occlusionCycleParticipants.Count; i++)
        {
            for (int j = i + 1; j < _occlusionCycleParticipants.Count; j++)
            {
                _occlusionPairs.Add((_occlusionCycleParticipants[i], _occlusionCycleParticipants[j]));
            }
        }

        _cycleTraceQuality = OcclusionTraceQuality;
        _occlusionPairIndex = 0;
        _occlusionTicksRemaining = OcclusionWindowTicks;
        _occlusionCycleInProgress = true;

        if (_occlusionPairs.Count == 0)
        {
            CompleteOcclusionCycle();
        }
    }

    private void ProcessOcclusionCycleTick()
    {
        if (!_occlusionCycleInProgress)
        {
            return;
        }

        if (_occlusionPairIndex >= _occlusionPairs.Count)
        {
            CompleteOcclusionCycle();
            return;
        }

        int remainingPairs = _occlusionPairs.Count - _occlusionPairIndex;
        int remainingTicks = Math.Max(_occlusionTicksRemaining, 1);
        int pairsThisTick = (int)Math.Ceiling((double)remainingPairs / remainingTicks);

        for (int i = 0; i < pairsThisTick; i++)
        {
            if (_occlusionPairIndex >= _occlusionPairs.Count)
            {
                break;
            }

            var pair = _occlusionPairs[_occlusionPairIndex];
            _occlusionPairIndex++;
            ProcessOcclusionPair(pair.from, pair.to);
        }

        _occlusionTicksRemaining = Math.Max(_occlusionTicksRemaining - 1, 0);

        if (_occlusionPairIndex >= _occlusionPairs.Count)
        {
            CompleteOcclusionCycle();
        }
    }

    private void ProcessOcclusionPair(OcclusionSnapshot fromSnapshot, OcclusionSnapshot toSnapshot)
    {
        var fraction = CalculateOcclusionFraction(fromSnapshot, toSnapshot, _cycleTraceQuality);
        SetDirectionalOcclusion(fromSnapshot, toSnapshot, fraction);
        SetDirectionalOcclusion(toSnapshot, fromSnapshot, fraction);
    }

    private float CalculateOcclusionFraction(OcclusionSnapshot source, OcclusionSnapshot target, int quality)
    {
        int widening = 31;

        var sourceOrigin = source.Origin;
        var targetOrigin = target.Origin;
        if (quality <= 1)
        {
            return Trace(sourceOrigin, targetOrigin, source.IgnoreEntity) ? 1f : 0f;
        }

        PrepareOcclusionEndpoints(sourceOrigin, targetOrigin, widening, out var soundLeft, out var soundRight, out var listenerLeft, out var listenerRight);

        // Pull each side endpoint back from nearby walls once, then reuse it in every sample that touches it.
        // Sweeping from the eye also prevents samples from jumping through a thin wall into free space beyond it.
        soundLeft = ClampOcclusionSample(sourceOrigin, soundLeft, source.IgnoreEntity);
        soundRight = ClampOcclusionSample(sourceOrigin, soundRight, source.IgnoreEntity);
        listenerLeft = ClampOcclusionSample(targetOrigin, listenerLeft, target.IgnoreEntity);
        listenerRight = ClampOcclusionSample(targetOrigin, listenerRight, target.IgnoreEntity);

        int totalHits = 0;
        int tracesExecuted = 0;

        tracesExecuted++;
        totalHits += Trace(sourceOrigin, targetOrigin, source.IgnoreEntity) ? 1 : 0;

        if (quality >= 2)
        {
            tracesExecuted += 2;
            totalHits += Trace(soundLeft, listenerLeft, source.IgnoreEntity) ? 1 : 0;
            totalHits += Trace(soundRight, listenerRight, source.IgnoreEntity) ? 1 : 0;
        }

        if (quality >= 3)
        {
            tracesExecuted += 2;
            totalHits += Trace(soundLeft, targetOrigin, source.IgnoreEntity) ? 1 : 0;
            totalHits += Trace(soundRight, targetOrigin, source.IgnoreEntity) ? 1 : 0;
        }

        if (quality >= 4)
        {
            tracesExecuted += 2;
            totalHits += Trace(sourceOrigin, listenerLeft, source.IgnoreEntity) ? 1 : 0;
            totalHits += Trace(sourceOrigin, listenerRight, source.IgnoreEntity) ? 1 : 0;
        }

        if (quality >= 5)
        {
            tracesExecuted += 2;
            totalHits += Trace(soundLeft, listenerRight, source.IgnoreEntity) ? 1 : 0;
            totalHits += Trace(soundRight, listenerLeft, source.IgnoreEntity) ? 1 : 0;
        }

        if (tracesExecuted <= 0)
        {
            return 0f;
        }

        return (float)totalHits / tracesExecuted;
    }

    private static void SetDirectionalOcclusion(OcclusionSnapshot source, OcclusionSnapshot target, float fraction)
    {
        if (source.PendingFractions == null)
        {
            return;
        }

        // The cached key avoids two ulong.ToString() allocations per pair.
        source.PendingFractions[target.SteamIdText] = Math.Clamp(fraction, 0f, 1f);
    }

    private void CompleteOcclusionCycle()
    {
        // Publish the complete cycle together; pose packets must never see a cleared or partial row.
        foreach (var participant in _occlusionCycleParticipants)
        {
            var row = participant.PendingFractions;
            participant.PendingFractions = null;
            if (row == null)
            {
                continue;
            }

            if (PlayerData.TryGetValue(participant.SteamId, out var data))
            {
                // Poses serialize synchronously on this thread, so the replaced row is free to refill next cycle.
                participant.SpareFractions = data.OcclusionFraction;
                data.OcclusionFraction = row;
            }
            else
            {
                participant.SpareFractions = row;
            }
        }

        ResetOcclusionCycleState();
    }

    private void ResetOcclusionCycleState()
    {
        foreach (var participant in _occlusionCycleParticipants)
        {
            // An interrupted cycle keeps its unpublished row for reuse.
            if (participant.PendingFractions != null)
            {
                participant.SpareFractions ??= participant.PendingFractions;
                participant.PendingFractions = null;
            }
        }

        _occlusionCycleInProgress = false;
        _occlusionTicksRemaining = 0;
        _occlusionPairIndex = 0;
        _occlusionPairs.Clear();
        _occlusionCycleParticipants.Clear();
    }

    private void PruneOcclusionParticipants()
    {
        // Departed players leave PlayerData; drop their pooled trace state as well.
        if (_occlusionParticipants.Count <= PlayerData.Count)
        {
            return;
        }

        _occlusionPruneScratch.Clear();
        foreach (var steamId in _occlusionParticipants.Keys)
        {
            if (!PlayerData.ContainsKey(steamId))
            {
                _occlusionPruneScratch.Add(steamId);
            }
        }
        foreach (var steamId in _occlusionPruneScratch)
        {
            _occlusionParticipants.Remove(steamId);
        }
    }

    private ulong GetAuthorizedSteamId(CCSPlayerController player)
    {
        var steamId = (ulong)(player.AuthorizedSteamID?.SteamId64 ?? 0);
        if (steamId != 0)
        {
            _authorizedSteamIds[player.Slot] = (player.EntityHandle.Raw, steamId);
            return steamId;
        }

        // A temporary null read must not erase an ID already verified for this connection.
        return _authorizedSteamIds.TryGetValue(player.Slot, out var cached) && cached.ControllerHandle == player.EntityHandle.Raw ? cached.SteamId : 0;
    }

    private ulong GetPlayerDataSteamId(CCSPlayerController player)
    {
        var steamId = GetAuthorizedSteamId(player);
        if (steamId != 0)
        {
            return steamId;
        }

        if (!DEBUG_FAKE_PLAYERS || !player.IsBot)
        {
            return 0;
        }

        int index = fakeBots.IndexOf(player.PlayerName);
        if (index < 0)
        {
            return 0;
        }

        string candidate;
        if (index + 1 < 10)
        {
            candidate = $"1000000000000000{index + 1}";
        }
        else
        {
            candidate = $"100000000000000{index + 1}";
        }
        ulong.TryParse(candidate, out var fakeSteamId);
        return fakeSteamId;
    }

    private static TraceResult TraceOcclusion(Vector from, Vector to, CBaseEntity ignore)
    {
        return CounterStrikeTrace.TraceEndShape(from, to, ignore, OcclusionTraceOptions);
    }

    public bool Trace(Vector from, Vector to, CBaseEntity ignore)
    {
        return TraceOcclusion(from, to, ignore).DidHit();
    }

    private static Vector ClampOcclusionSample(Vector origin, Vector desired, CBaseEntity ignore)
    {
        float dx = desired.X - origin.X;
        float dy = desired.Y - origin.Y;
        float dz = desired.Z - origin.Z;
        float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length <= 0.001f)
        {
            return origin;
        }

        var result = TraceOcclusion(origin, desired, ignore);
        if (!result.DidHit())
        {
            return desired;
        }

        if (result.IsAllSolid || !float.IsFinite(result.Fraction))
        {
            return origin;
        }

        // Fraction is the ray's travel, so stop slightly before contact. Endpoints are the reusable per-pair vectors.
        float safeDistance = Math.Max(0f, length * Math.Clamp(result.Fraction, 0f, 1f) - OcclusionOffsetWallClearance);
        float scale = safeDistance / length;
        SetOcclusionPoint(desired, origin.X + dx * scale, origin.Y + dy * scale, origin.Z + dz * scale);
        return desired;
    }

    public Vector CalculatePoint(Vector a, Vector b, float m, bool positive)
    {
        var point = new Vector();
        CalculatePointInto(point, a, b, m, positive);
        return point;
    }

    private void PrepareOcclusionEndpoints(Vector sourceOrigin, Vector targetOrigin, float widening, out Vector soundLeft, out Vector soundRight, out Vector listenerLeft, out Vector listenerRight)
    {
        soundLeft = _occlusionSoundLeft;
        soundRight = _occlusionSoundRight;
        listenerLeft = _occlusionListenerLeft;
        listenerRight = _occlusionListenerRight;
        CalculatePointInto(soundLeft, sourceOrigin, targetOrigin, widening, true);
        CalculatePointInto(soundRight, sourceOrigin, targetOrigin, widening, false);
        // Both ends use the same sideways direction for quality 2's parallel traces.
        SetOcclusionPoint(listenerLeft, targetOrigin.X + (soundLeft.X - sourceOrigin.X), targetOrigin.Y + (soundLeft.Y - sourceOrigin.Y), targetOrigin.Z);
        SetOcclusionPoint(listenerRight, targetOrigin.X + (soundRight.X - sourceOrigin.X), targetOrigin.Y + (soundRight.Y - sourceOrigin.Y), targetOrigin.Z);
    }

    private static void SetOcclusionPoint(Vector point, float x, float y, float z)
    {
        point.X = x;
        point.Y = y;
        point.Z = z;
    }

    private static void CalculatePointInto(Vector point, Vector a, Vector b, float m, bool positive)
    {
        // CS2 is Z-up: spread the side samples horizontally in the XY plane.
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance == 0f)
        {
            // Vertical/coincident pairs have no horizontal direction; use a fixed sideways spread.
            SetOcclusionPoint(point, a.X + (positive ? m : -m), a.Y, a.Z);
            return;
        }

        float offset = (positive ? m : -m) / distance;
        SetOcclusionPoint(point, a.X + offset * dy, a.Y - offset * dx, a.Z);
    }

    public void SavePlayerData(CCSPlayerController? player, bool useObserverPawn)
    {
        if (player == null || !IsValid(player))
        {
            return;
        }

        var playerSteamId = GetAuthorizedSteamId(player);
        if (playerSteamId == 0 && !DEBUG_FAKE_PLAYERS)
        {
            return;
        }

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
                var observerPawn = player.Pawn.Value!;
                var vAngle = observerPawn.V_angle;
                var c4Position = observerPawn.AbsOrigin;

                if (c4Position == null)
                {
                    return;
                }

                float c4X = c4Position.X;
                float c4Y = c4Position.Y;
                float c4Z = c4Position.Z;

                // Calculate third person position when spectating planted c4
                GetForwardVector(vAngle.X, vAngle.Y, out var forwardX, out var forwardY, out var forwardZ);

                // TODO: Could we TraceRay to the c4 to detect if cameraPos is inside an object, and reduce camDistance until the trace is clear?
                const float camDistance = 100;
                float lowestZ = c4Z + 25;
                float cameraZ = c4Z - forwardZ * camDistance;

                OriginX = c4X - forwardX * camDistance;
                OriginY = c4Y - forwardY * camDistance;
                // Prevent the camera from going under the floor
                OriginZ = cameraZ < lowestZ ? lowestZ : cameraZ;

                LookAtX = c4X;
                LookAtY = c4Y;
                LookAtZ = c4Z;

                gotOriginAndAngles = true;
                spectatingC4 = true;
            }
            else if (observerMode == ObserverMode_t.OBS_MODE_ROAMING)
            {
                var angles = player.Pawn.Value!.V_angle;
                Vector? origin = GetFreecamPlayerPosition(player);
                if (origin != null)
                {
                    OriginX = origin.X;
                    OriginY = origin.Y;
                    OriginZ = origin.Z;

                    GetLookAtPoint(OriginX, OriginY, OriginZ, angles, out LookAtX, out LookAtY, out LookAtZ);
                    gotOriginAndAngles = true;
                }
            }
            else if (observerMode == ObserverMode_t.OBS_MODE_CHASE && pawn!.DesignerName == "player")
            {
                var vAngle = player.Pawn.Value!.V_angle;
                if (!TryGetEyePosition(pawn, out var eyeX, out var eyeY, out var eyeZ))
                {
                    return;
                }

                // Calculate third person position when spectating a player in thirdperson view (Chase cam)
                GetForwardVector(vAngle.X, vAngle.Y, out var forwardX, out var forwardY, out var forwardZ);

                const float camDistance = 150; // matches cam_idealdist cvar

                OriginX = eyeX - forwardX * camDistance;
                OriginY = eyeY - forwardY * camDistance;
                OriginZ = eyeZ - forwardZ * camDistance;
                LookAtX = eyeX;
                LookAtY = eyeY;
                LookAtZ = eyeZ;
                gotOriginAndAngles = true;
            }
        }

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        if (!gotOriginAndAngles)
        {
            // Managed math only: no per-tick native QAngle/Vector handles for CounterStrikeSharp to track.
            if (!TryGetEyePosition(pawn, out OriginX, out OriginY, out OriginZ))
            {
                return;
            }

            GetLookAtPoint(OriginX, OriginY, OriginZ, pawn.V_angle, out LookAtX, out LookAtY, out LookAtZ);
        }

        var playerIsAlive = IsAlive(player) ? 1 : 0;
        var Team = player.TeamNum;
        // Each PlayerName read marshals a new string; read it once per tick.
        string playerName = player.PlayerName;

        if (!PlayerData.TryGetValue(playerSteamId, out var existingData))
        {
            existingData = new PlayerData(playerSteamId.ToString(), playerName);
            PlayerData[playerSteamId] = existingData;
        }
        if (existingData.IsAdmin == null)
        {
            CheckAdmin(playerSteamId != 0 ? (SteamID)playerSteamId : null);
        }
        if (playerSteamId != 0)
        {
            SaveData(playerSteamId, playerName, OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, Team, playerIsAlive, spectatingC4);
        }
        else
        {
            if (DEBUG_FAKE_PLAYERS)
            {
                int index = fakeBots.IndexOf(playerName);
                ulong steamid;
                if (index + 1 < 10)
                {
                    ulong.TryParse($"1000000000000000{index + 1}", out steamid);
                }
                else
                {
                    ulong.TryParse($"100000000000000{index + 1}", out steamid);
                }
                SaveData(steamid, playerName, OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, Team, playerIsAlive, spectatingC4);
            }
        }
    }

    public void SaveData(ulong playerSteamId, string playerName, float OriginX, float OriginY, float OriginZ, float LookAtX, float LookAtY, float LookAtZ, byte Team, int playerIsAlive, bool spectatingC4)
    {
        // Every PlayerData is created with its key's text, so SteamId never needs re-stringifying per tick.
        if (!PlayerData.TryGetValue(playerSteamId, out var data))
        {
            data = new PlayerData(playerSteamId.ToString(), playerName);
            PlayerData[playerSteamId] = data;
        }

        data.Name = playerName;

        // Scale up the floats and store them as integers
        data.OriginX = (int)(OriginX * 10000);
        data.OriginY = (int)(OriginY * 10000);
        data.OriginZ = (int)(OriginZ * 10000);
        data.LookAtX = (int)(LookAtX * 10000);
        data.LookAtY = (int)(LookAtY * 10000);
        data.LookAtZ = (int)(LookAtZ * 10000);

        data.Team = Team;
        data.IsAlive = playerIsAlive == 1;
        data.SpectatingC4 = spectatingC4;
    }

    [EntityOutputHook("prop_door_rotating", "OnBreak")]
    public HookResult OnBreak(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        // This hook will run twice when a door is blown up by a nade
        // In one hook the activator will be the nade, second hook the door will also be the activator

        if (caller.IsValid && caller.DesignerName == "prop_door_rotating" && activator.IsValid && activator.DesignerName == "prop_door_rotating")
        {
            var door = caller.As<CPropDoorRotating>();
            if (door != null && door.IsValid && door.AbsOrigin != null)
            {
                var origin = door.AbsOrigin.Clone();
                var doorKey = GetDoorKey(origin);

                AddTimer(0.1f, () =>
                {
                    DoorRotations[doorKey] = 999;
                    QueueSocketEmit("door-rotation", new object[] { "proximity-chat", $"{origin.X} {origin.Y} {origin.Z}", 999 });
                });
            }
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult Event_RoundStart(EventRoundStart @event, GameEventInfo info)
    {
        DoorRotations.Clear();

        var doors = new List<CPropDoorRotating>();
        doors.AddRange(Utilities.FindAllEntitiesByDesignerName<CPropDoorRotating>("prop_door_rotating"));
        doors.AddRange(Utilities.FindAllEntitiesByDesignerName<CPropDoorRotating>("func_door_rotating"));

        foreach (var door in doors)
        {
            if (door.AbsOrigin == null || door.AbsRotation == null)
            {
                continue;
            }
            DoorEntities.Add(door);
            var key = GetDoorKey(door.AbsOrigin);
            DoorRotations[key] = 999;
        }

        fakeBots.Clear();
        foreach (var bot in Utilities.GetPlayers().Where(IsValid).Where(p => p.IsBot))
        {
            fakeBots.Add(bot.PlayerName);
        }

        return HookResult.Continue;
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

    private void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[{this.ModuleName}] {message}");
        Console.ResetColor();
    }

    public AssemblyName assemblyName = typeof(ProximityChat).Assembly.GetName();
    public string? AssemblyVersion => assemblyName.Version?.ToString(); // Version: x.x.x.x
    public string? PluginVersion => AssemblyVersion?.Remove(AssemblyVersion.Length - 2); // truncate to x.x.x
}

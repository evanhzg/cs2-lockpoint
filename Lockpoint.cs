using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using Lockpoint.Services;
using Lockpoint.Models;
using Lockpoint.Enums;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Lockpoint
{
    public class Lockpoint : BasePlugin
    {
        public override string ModuleName => "Lockpoint";
        public override string ModuleVersion => "0.8.2";
        public override string ModuleAuthor => "evanhh";
        public override string ModuleDescription => "Lockpoint game mode for CS2";

        private DateTime _gameEndTime;
        private string _gameEndMessage = "";


        private const string LockpointCfgDirectory = "/../../../../cfg/Lockpoint";
        private const string LockpointCfgPath = $"{LockpointCfgDirectory}/lockpoint.cfg";

        private int _gameStartCountdown = 0;
        private CounterStrikeSharp.API.Modules.Timers.Timer? _countdownTimer = null;

        private readonly HashSet<CCSPlayerController> _respawningPlayers = new();
        private readonly Dictionary<CCSPlayerController, DateTime> _playerDeathTimes = new();
        private readonly Dictionary<CCSPlayerController, System.Timers.Timer> _respawnTimers = new();
        private float _captureTime = 20f;
        private float _respawnDelay = 5.0f;
        private double _newZoneDelay = 5.0;
        private float _zoneEmptyTime = 0f; // Track how long zone has been empty
        private const float ZONE_DECAY_DELAY = 5.0f; // 5 seconds before decay starts
        private bool _zoneWasOccupied = false; // Track if zone was previously occupied
        private ZoneState _lastZoneState = ZoneState.Neutral;
        private Zone? _zoneBeingEdited = null;
        private bool _isEditingExistingZone = false;
        private ZoneVisualization? _zoneVisualization;
        private ZoneManager? _zoneManager;
        private readonly Dictionary<CCSPlayerController, Zone> _activeZones = new();
        private System.Timers.Timer? _zoneCheckTimer;
        private Zone? activeZone;

        private GamePhase _gamePhase = GamePhase.Warmup;
        private GamePhase _previousGamePhase = GamePhase.Warmup;
        private bool _editMode = false;
        private bool _previousCheatsEnabled = false;
        private readonly HashSet<CCSPlayerController> _readyPlayers = new();
        private bool _requireTeamReady = true; // Configurable: true = one per team, false = all players
        private DateTime _lastReadyMessage = DateTime.MinValue;
        private readonly double _readyMessageInterval = 10.0; // 10 seconds

        private float _ctZoneTime = 0f;
        private float _tZoneTime = 0f;
        private int _ctScore = 0;
        private int _tScore = 0;
        private System.Timers.Timer? _LockpointTimer;
        private System.Timers.Timer? _hudTimer;
        private Zone? _previousZone = null;
        private const float TIMER_INTERVAL = 100f; // Back to 100ms
        private const float HUD_UPDATE_INTERVAL = 50f; // Update HUD every 500ms instead of every timer tick
        private const int WINNING_SCORE = 3; // First team to 3 captures wins
        private const float ZONE_DETECTION_BUFFER = 8.0f; // Buffer for zone edge detection in units

        private bool _waitingForNewZone = false;
        private DateTime _zoneResetTime;
        private string _lastCaptureTeam = "";
        private bool _isCountingDown = false;
        private string _countdownMessage = "";

        public override void Load(bool hotReload)
        {
            Logger.LogInformation("Lockpoint plugin loaded");
            _zoneVisualization = new ZoneVisualization();
            _zoneManager = new ZoneManager(ModuleDirectory);

            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath); // Add this
            RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect); // Add this

            if (!string.IsNullOrEmpty(Server.MapName))
            {
                Server.PrintToConsole($"[Lockpoint] Loading zones for current map: {Server.MapName}");
                _zoneManager?.LoadZonesForMap(Server.MapName);
            }

            CreateTimers();

            _zoneCheckTimer = new System.Timers.Timer(50);
            _zoneCheckTimer.Elapsed += CheckPlayerZones;
            _zoneCheckTimer.Start();

            // Initialize Lockpoint timer
            _LockpointTimer = new System.Timers.Timer(TIMER_INTERVAL);
            _LockpointTimer.Elapsed += UpdateLockpointTimer;
            _LockpointTimer.Start();
        }

        private void OnMapStart(string mapName)
        {
            Server.PrintToConsole($"[Lockpoint] Map started: {mapName}");
            Server.PrintToChatAll($"[Lockpoint] Map started: {mapName}");

            _zoneVisualization?.ClearZoneVisualization();
            activeZone = null;
            _previousZone = null;

            _gamePhase = GamePhase.Warmup;
            _ctScore = 0;
            _tScore = 0;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;

            // Execute configuration file after map loads
            AddTimer(1.0f, () =>
            {
                ExecuteLockpointConfiguration(ModuleDirectory);
            });

            // Load zones after configuration
            AddTimer(3.0f, () =>
            {
                try
                {
                    _zoneManager?.LoadZonesForMap(mapName);

                    var zones = _zoneManager?.Zones;
                    if (zones != null && zones.Count > 0)
                    {
                        Server.PrintToConsole($"[Lockpoint] Loaded {zones.Count} zones for map {mapName}");
                        foreach (var zone in zones)
                        {
                            Server.PrintToConsole($"[Lockpoint] Zone: {zone.Name} ({zone.Points.Count} points, {zone.TerroristSpawns.Count} T spawns, {zone.CounterTerroristSpawns.Count} CT spawns)");
                        }
                    }
                    else
                    {
                        Server.PrintToConsole($"[Lockpoint] No zones found for map {mapName}");
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error loading zones: {ex.Message}");
                }

                InitializeGameState();
            });
        }

        private void CheckZonesUpdate(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                Server.NextFrame(() =>
                {
                    CheckZones();
                });
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error in zone check update: {ex.Message}");
            }
        }

        private void LockpointUpdate(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                Server.NextFrame(() =>
                {
                    UpdateHUD();
                });
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error in lockpoint update: {ex.Message}");
            }
        }

        private void GiveRandomUtility(CCSPlayerController player)
        {
            if (player?.IsValid != true || player.PlayerPawn?.Value == null || !player.PawnIsAlive)
                return;

            try
            {
                var utilities = new string[]
                {
                    "weapon_smokegrenade",
                    "weapon_hegrenade",
                    "weapon_molotov",
                    "weapon_incgrenade"
                };

                var random = new Random();
                var selectedUtility = utilities[random.Next(utilities.Length)];

                player.GiveNamedItem(selectedUtility);

                Server.PrintToConsole($"[Lockpoint] Gave {selectedUtility} to {player.PlayerName}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error giving utility to {player.PlayerName}: {ex.Message}");
            }
        }

        private void GivePlayerEquipment(CCSPlayerController player)
        {
            if (player?.IsValid != true || player.PlayerPawn?.Value == null || !player.PawnIsAlive)
                return;

            try
            {
                // Give armor and helmet using console commands
                Server.ExecuteCommand($"mp_free_armor 2");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error giving equipment to {player.PlayerName}: {ex.Message}");
            }
        }

        private void UpdateHUD()
        {
            try
            {
                switch (_gamePhase)
                {
                    case GamePhase.Warmup:
                        UpdateWarmupHUD();
                        break;
                    case GamePhase.EditMode:
                        UpdateEditModeHUD();
                        break;
                    case GamePhase.Active:
                        UpdateLockpointHUD();
                        break;
                    case GamePhase.Ended:
                        UpdateEndGameHUD();
                        break;
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error updating HUD: {ex.Message}");
            }
        }

        private void UpdateEndGameHUD()
        {
            var allPlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true && p.Connected == PlayerConnectedState.PlayerConnected && !p.IsBot)
                .ToList();

            foreach (var player in allPlayers)
            {
                if (!_respawningPlayers.Contains(player) && player.PawnIsAlive)
                {
                    player.PrintToCenter(_gameEndMessage);
                }
            }
        }

        public static void ExecuteLockpointConfiguration(string moduleDirectory)
        {
            try
            {
                var fullCfgPath = moduleDirectory + LockpointCfgPath;

                if (!File.Exists(fullCfgPath))
                {
                    // Create directory if it doesn't exist
                    Directory.CreateDirectory(moduleDirectory + LockpointCfgDirectory);

                    using (var lockpointCfg = File.Create(fullCfgPath))
                    {
                        var lockpointCfgContents = @"
        // Lockpoint Configuration File
        // This file configures the server for optimal Lockpoint gameplay

        // === CORE GAME SETTINGS ===
        // Disable automatic round win conditions (we control rounds manually)
        mp_ignore_round_win_conditions 1
        sv_skirmish_id 0

        // Long round time since we control rounds manually  
        mp_roundtime 60
        mp_roundtime_defuse 60
        mp_roundtime_hostage 60
        mp_join_grace_time 5

        // Fast transitions
        mp_freezetime 1
        mp_round_restart_delay 1
        mp_halftime_duration 1
        mp_match_can_clinch 0

        // === ECONOMY DISABLED ===
        // No money system in Lockpoint
        mp_maxmoney 0
        mp_startmoney 0
        mp_afterroundmoney 0
        mp_buytime 0
        mp_buy_anywhere 0
        mp_playercashawards 0
        mp_teamcashawards 0

        // === OBJECTIVES DISABLED ===
        // No bomb or hostage objectives
        mp_plant_c4_anywhere 0
        mp_give_player_c4 0

        // === WEAPON SETTINGS ===
        // Remove map weapons and default loadouts (we handle weapons via code)
        mp_weapons_allow_map_placed 0
        mp_ct_default_primary ""weapon_ak47""
        mp_t_default_primary ""weapon_ak47""
        mp_ct_default_secondary ""weapon_deagle""
        mp_t_default_secondary ""weapon_deagle""

        // === TEAM SETTINGS ===
        mp_autoteambalance 0
        mp_limitteams 0
        mp_solid_teammates 1

        // === RESPAWN SETTINGS ===
        // Disable automatic respawning (we handle via code)
        mp_respawn_on_death_ct 0
        mp_respawn_on_death_t 0

        // === MISC OPTIMIZATIONS ===
        // Reduce delays and cinematics
        tv_delay 0
        mp_teammatchstat_txt """"
        mp_teammatchstat_holdtime 0
        mp_min_halftime_duration
        mp_team_intro_time 0
        mp_warmup_pausetimer 0
        mp_warmup_end

        // Basic game mode
        game_type 0
        game_mode 1

        // Communication settings
        sv_talk_enemy_dead 0
        sv_talk_enemy_living 0
        sv_deadtalk 1

        // Disable bots
        bot_kick
        bot_quota 0

        // Other useful settings
        mp_forcecamera 1
        mp_autokick 0
        mp_friendlyfire 1
        spec_replay_enable 1
        mp_death_drop_gun 0
        mp_death_drop_defuser 0
        mp_death_drop_grenade 0
        mp_free_armor 2
        sv_infinite_ammo 2

        echo [Lockpoint] Configuration loaded successfully!
        ";

                        var lockpointCfgBytes = Encoding.UTF8.GetBytes(lockpointCfgContents);
                        lockpointCfg.Write(lockpointCfgBytes, 0, lockpointCfgBytes.Length);
                    }

                    Server.PrintToConsole("[Lockpoint] Created lockpoint.cfg configuration file");
                }

                // Execute the configuration file
                Server.ExecuteCommand($"exec Lockpoint/lockpoint.cfg");
                Server.PrintToConsole("[Lockpoint] Executed lockpoint.cfg");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error creating/executing configuration: {ex.Message}");
                // Fallback to individual commands if cfg fails
                ExecuteFallbackConfiguration();
            }
        }

        // Fallback method if cfg file approach fails
        private static void ExecuteFallbackConfiguration()
        {
            try
            {
                Server.PrintToConsole("[Lockpoint] Using fallback configuration...");

                // Core settings
                Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
                Server.ExecuteCommand("mp_roundtime 60");
                Server.ExecuteCommand("mp_freezetime 1");
                Server.ExecuteCommand("mp_round_restart_delay 1");

                // Economy
                Server.ExecuteCommand("mp_maxmoney 0");
                Server.ExecuteCommand("mp_startmoney 0");
                Server.ExecuteCommand("mp_buytime 0");

                // Objectives
                Server.ExecuteCommand("mp_plant_c4_anywhere 0");
                Server.ExecuteCommand("mp_give_player_c4 0");

                // Teams
                Server.ExecuteCommand("mp_autoteambalance 0");
                Server.ExecuteCommand("mp_respawn_on_death_ct 0");
                Server.ExecuteCommand("mp_respawn_on_death_t 0");

                Server.PrintToConsole("[Lockpoint] Fallback configuration applied");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error in fallback configuration: {ex.Message}");
            }
        }

        private void InitializeGameState()
        {
            _gamePhase = GamePhase.Warmup;
            _readyPlayers.Clear();
            _ctScore = 0;
            _tScore = 0;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;

            UpdateTeamScore(CsTeam.CounterTerrorist, 0);
            UpdateTeamScore(CsTeam.Terrorist, 0);

            // Start the game timers
            if (_zoneCheckTimer == null)
            {
                _zoneCheckTimer = new System.Timers.Timer(50);
                _zoneCheckTimer.Elapsed += CheckZonesUpdate;
            }

            if (_LockpointTimer == null)
            {
                _LockpointTimer = new System.Timers.Timer(TIMER_INTERVAL);
                _LockpointTimer.Elapsed += LockpointUpdate;
            }

            _zoneCheckTimer.Start();
            _LockpointTimer.Start();

            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}⏳ Type !ready to start the game{ChatColors.Default}");
            Server.PrintToConsole("[Lockpoint] Game state initialized");
        }

        private void StartGame()
        {
            try
            {
                if (_zoneManager?.Zones == null || _zoneManager.Zones.Count == 0)
                {
                    Server.PrintToChatAll($"{ChatColors.Red}[Lockpoint] Cannot start game - no zones available!{ChatColors.Default}");
                    return;
                }

                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}Game starting in 3 seconds...{ChatColors.Default}");
                Server.PrintToConsole("[Lockpoint] Starting game countdown");

                // Set countdown state to prevent HUD override
                _isCountingDown = true;
                _gameStartCountdown = 3;
                _countdownTimer = AddTimer(1.0f, () =>
                {
                    HandleGameStartCountdown();
                });
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error starting game: {ex.Message}");
            }
        }

        private void CheckZones()
        {
            // Don't check zones if we're waiting for a new zone or no active zone
            if (_gamePhase != GamePhase.Active || activeZone == null || _waitingForNewZone)
                return;

            try
            {
                // Clear the zone's player list
                activeZone.PlayersInZone.Clear();

                // Get all valid players
                var allPlayers = Utilities.GetPlayers()
                    .Where(p => p?.IsValid == true &&
                            p.Connected == PlayerConnectedState.PlayerConnected &&
                            !p.IsBot &&
                            p.PawnIsAlive &&
                            p.PlayerPawn?.Value?.AbsOrigin != null &&
                            p.TeamNum != (byte)CsTeam.None &&
                            p.TeamNum != (byte)CsTeam.Spectator)
                    .ToList();

                // Check which players are in the zone
                foreach (var player in allPlayers)
                {
                    if (IsPlayerInZone(player, activeZone))
                    {
                        activeZone.PlayersInZone.Add(player);
                    }
                }

                // Process zone control logic
                ProcessZoneControl();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error checking zones: {ex.Message}");
            }
        }

        private void ProcessZoneControl()
        {
            if (activeZone == null) return;

            try
            {
                var ctPlayersInZone = activeZone.PlayersInZone.Count(p => p.TeamNum == (byte)CsTeam.CounterTerrorist);
                var tPlayersInZone = activeZone.PlayersInZone.Count(p => p.TeamNum == (byte)CsTeam.Terrorist);

                if (ctPlayersInZone > 0 && tPlayersInZone == 0)
                {
                    _ctZoneTime += TIMER_INTERVAL / 1000f; // Convert to seconds
                    _lastCaptureTeam = "CT"; // This uses the field
                }
                else if (tPlayersInZone > 0 && ctPlayersInZone == 0)
                {
                    _tZoneTime += TIMER_INTERVAL / 1000f; // Convert to seconds
                    _lastCaptureTeam = "T"; // This uses the field
                }
                else
                {
                    // Contested or empty zone - no progress
                    _lastCaptureTeam = ""; // This uses the field
                }

                // Check for capture
                if (_ctZoneTime >= _captureTime)
                {
                    OnZoneCaptured("CT");
                }
                else if (_tZoneTime >= _captureTime)
                {
                    OnZoneCaptured("T");
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error processing zone control: {ex.Message}");
            }
        }

        private void OnZoneCaptured(string team)
        {
            try
            {
                // Increment the team score
                if (team == "CT")
                {
                    _ctScore++;
                }
                else
                {
                    _tScore++;
                }

                // Reset zone capture times and states
                _ctZoneTime = 0f;
                _tZoneTime = 0f;
                _zoneEmptyTime = 0f;
                _zoneWasOccupied = false;

                // Clear the zone
                activeZone?.PlayersInZone.Clear();

                // Announce the capture
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Orange}{team}{ChatColors.Default} captured the zone! Score: {ChatColors.Blue}CT{ChatColors.Default} {_ctScore} - {ChatColors.Red}T{ChatColors.Default} {_tScore}");

                // Check for game end
                if (_ctScore >= WINNING_SCORE)
                {
                    EndGame("CT");
                }
                else if (_tScore >= WINNING_SCORE)
                {
                    EndGame("T");
                }
                else
                {
                    // Start countdown for next zone
                    StartZoneChangeCountdown();
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error in OnZoneCaptured: {ex.Message}");
            }
        }

        private bool IsPlayerInZone(CCSPlayerController player, Zone zone)
        {
            if (player?.PlayerPawn?.Value?.AbsOrigin == null || zone?.Points == null || zone.Points.Count < 3)
                return false;

            try
            {
                var playerPos = player.PlayerPawn.Value.AbsOrigin;
                return IsPointInPolygonWithBuffer(playerPos.X, playerPos.Y, zone.Points, ZONE_DETECTION_BUFFER);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error checking if player {player.PlayerName} is in zone: {ex.Message}");
                return false;
            }
        }

        private bool IsPointInPolygonWithBuffer(float x, float y, List<CSVector> polygon, float buffer = 32.0f)
        {
            // First check if point is inside the polygon normally
            if (IsPointInPolygon(x, y, polygon))
            {
                return true;
            }

            // If not inside, check if point is within buffer distance of any edge
            return IsPointNearPolygonEdge(x, y, polygon, buffer);
        }

        private bool IsPointInPolygon(float x, float y, List<CSVector> polygon)
        {
            int intersections = 0;
            int vertexCount = polygon.Count;

            for (int i = 0; i < vertexCount; i++)
            {
                int j = (i + 1) % vertexCount;

                if (((polygon[i].Y > y) != (polygon[j].Y > y)) &&
                    (x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X))
                {
                    intersections++;
                }
            }

            return (intersections % 2) == 1;
        }

        private bool IsPointNearPolygonEdge(float x, float y, List<CSVector> polygon, float buffer)
        {
            int vertexCount = polygon.Count;

            for (int i = 0; i < vertexCount; i++)
            {
                int j = (i + 1) % vertexCount;

                // Get the two points that form this edge
                var p1 = polygon[i];
                var p2 = polygon[j];

                // Calculate distance from point to line segment
                float distance = DistanceFromPointToLineSegment(x, y, p1.X, p1.Y, p2.X, p2.Y);

                if (distance <= buffer)
                {
                    return true;
                }
            }

            return false;
        }

        // Add this helper method to calculate distance from point to line segment
        private float DistanceFromPointToLineSegment(float px, float py, float x1, float y1, float x2, float y2)
        {
            // Vector from line start to point
            float dx = px - x1;
            float dy = py - y1;

            // Vector of the line segment
            float lineX = x2 - x1;
            float lineY = y2 - y1;

            // Length squared of the line segment
            float lineLengthSquared = lineX * lineX + lineY * lineY;

            // If line segment has zero length, return distance to point
            if (lineLengthSquared == 0)
            {
                return (float)Math.Sqrt(dx * dx + dy * dy);
            }

            // Calculate the projection of the point onto the line
            float t = Math.Max(0, Math.Min(1, (dx * lineX + dy * lineY) / lineLengthSquared));

            // Find the closest point on the line segment
            float closestX = x1 + t * lineX;
            float closestY = y1 + t * lineY;

            // Calculate distance from point to closest point on line segment
            float distX = px - closestX;
            float distY = py - closestY;

            return (float)Math.Sqrt(distX * distX + distY * distY);
        }

        private void CheckGameEndCondition()
        {
            const int WINNING_SCORE = 5; // Adjust as needed

            if (_ctScore >= WINNING_SCORE)
            {
                EndGame("CT");
            }
            else if (_tScore >= WINNING_SCORE)
            {
                EndGame("T");
            }
        }

        private void EndGame(string winningTeam)
        {
            try
            {
                _gamePhase = GamePhase.Ended;
                _gameEndTime = DateTime.Now;

                // Stop game timers but keep HUD timer running for end screen
                _LockpointTimer?.Stop();
                _zoneCheckTimer?.Stop();

                // Clear visualizations
                _zoneVisualization?.ClearZoneVisualization();
                activeZone = null;

                // Set end game message for HUD
                _gameEndMessage = $"🏆 {winningTeam} WINS! 🏆\nFinal Score:{ChatColors.Blue} CT {_ctScore}{ChatColors.Default} -{ChatColors.Red} T{ChatColors.Default} {_tScore}\nReturning to warmup in a few seconds...";

                // Announce winner
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Blue}{winningTeam} WINS!{ChatColors.Default} Final Score: CT {_ctScore} - T {_tScore}");

                // Schedule return to warmup after 10 seconds
                Task.Delay(10000).ContinueWith(_ =>
                {
                    Server.NextFrame(() =>
                    {
                        _gamePhase = GamePhase.Warmup;
                        _ctScore = 0;
                        _tScore = 0;
                        _ctZoneTime = 0f;
                        _tZoneTime = 0f;
                        _zoneEmptyTime = 0f;
                        _zoneWasOccupied = false;
                        _gameEndMessage = "";
                        RespawnAllPlayers();
                        Server.PrintToChatAll($"{ChatColors.Yellow}[Lockpoint]{ChatColors.Default} - Returned to warmup phase");
                    });
                });

                Server.PrintToConsole($"[Lockpoint] Game ended - {winningTeam} wins");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error ending game: {ex.Message}");
            }
        }

        private void HandleGameStartCountdown()
        {
            try
            {
                if (_gameStartCountdown > 0)
                {
                    // Update countdown message for HUD
                    _countdownMessage = $"🎯 LOCKPOINT STARTING IN {_gameStartCountdown}";

                    // Also send to chat
                    Server.PrintToChatAll($"{ChatColors.Yellow}Game starting in {_gameStartCountdown}...{ChatColors.Default}");
                    _gameStartCountdown--;

                    // Schedule next countdown
                    if (_gameStartCountdown >= 0)
                    {
                        _countdownTimer = AddTimer(1.0f, () =>
                        {
                            HandleGameStartCountdown();
                        });
                    }
                }
                else
                {
                    // Countdown finished, clear countdown state
                    _isCountingDown = false;
                    _countdownMessage = "";
                    _countdownTimer?.Kill();
                    _countdownTimer = null;

                    StartActualGame();
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error in countdown: {ex.Message}");
                _isCountingDown = false;
                _countdownMessage = "";
                _countdownTimer?.Kill();
                _countdownTimer = null;
            }
        }

        private void StartActualGame()
        {
            try
            {
                _gamePhase = GamePhase.Active;

                // Reset scores
                _ctScore = 0;
                _tScore = 0;
                _ctZoneTime = 0f;
                _tZoneTime = 0f;
                _previousZone = null;

                UpdateTeamScore(CsTeam.CounterTerrorist, 0);
                UpdateTeamScore(CsTeam.Terrorist, 0);

                // Select first zone BEFORE respawning players
                SelectRandomZone();

                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}🔥 GAME STARTED! 🔥{ChatColors.Default}");

                if (activeZone != null)
                {
                    Server.PrintToChatAll($"{ChatColors.Yellow}📍 Capture Zone: {ChatColors.Green}{activeZone.Name}{ChatColors.Default}");
                }

                // Force a new round with proper spawns
                Server.ExecuteCommand("mp_restartgame 1");

                // Start game after round restart with immediate spawning
                AddTimer(1.0f, () =>
                {
                    // Respawn all players at zone-based spawns - no additional delay
                    RespawnAllPlayers();

                    // Start timers immediately after respawn
                    _zoneCheckTimer?.Start();
                    _LockpointTimer?.Start();
                });

                Server.PrintToConsole("[Lockpoint] Game started successfully");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error starting actual game: {ex.Message}");
            }
        }

        private void StartZoneChangeCountdown()
        {
            // Clear current zone visualization
            _zoneVisualization?.ClearZoneVisualization();
            activeZone = null;

            var countdown = 5;

            void CountdownTick()
            {
                Server.NextFrame(() =>
                {
                    if (countdown > 0)
                    {
                        countdown--;

                        Task.Delay(1000).ContinueWith(_ => CountdownTick());
                    }
                    else
                    {
                        DrawRandomZone();
                    }
                });
            }

            CountdownTick();
        }

        private void SelectRandomZone()
        {
            try
            {
                if (_zoneManager?.Zones == null || _zoneManager.Zones.Count == 0)
                {
                    Server.PrintToConsole("[Lockpoint] No zones available to select");
                    return;
                }

                var random = new Random();
                var availableZones = _zoneManager.Zones.ToList();

                // Don't repeat the same zone immediately
                if (_previousZone != null && availableZones.Count > 1)
                {
                    availableZones.Remove(_previousZone);
                }

                activeZone = availableZones[random.Next(availableZones.Count)];

                if (activeZone != null)
                {
                    Server.PrintToConsole($"[Lockpoint] Selected zone: {activeZone.Name}");
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error selecting random zone: {ex.Message}");
            }
        }

        private void UpdateLockpointTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    // Only process zone capture logic during active phase
                    if (_gamePhase != GamePhase.Active || activeZone == null)
                        return;

                    var zoneState = activeZone.GetZoneState();

                    // Only update zone color if the state has actually changed
                    if (zoneState != _lastZoneState)
                    {
                        _zoneVisualization?.UpdateZoneColor(activeZone, zoneState);
                        _lastZoneState = zoneState;
                        Server.PrintToConsole($"[Lockpoint] Zone {activeZone.Name} state changed to {zoneState}");
                    }

                    switch (zoneState)
                    {
                        case ZoneState.CTControlled:
                            _ctZoneTime += TIMER_INTERVAL / 1000f;
                            _zoneEmptyTime = 0f; // Reset empty timer
                            _zoneWasOccupied = true;
                            break;

                        case ZoneState.TControlled:
                            _tZoneTime += TIMER_INTERVAL / 1000f;
                            _zoneEmptyTime = 0f; // Reset empty timer
                            _zoneWasOccupied = true;
                            break;

                        case ZoneState.Contested:
                            // No progress when contested, but zone is occupied
                            _zoneEmptyTime = 0f; // Reset empty timer
                            _zoneWasOccupied = true;
                            break;

                        case ZoneState.Neutral:
                            // Zone is empty - start or continue empty timer
                            if (_zoneWasOccupied)
                            {
                                _zoneEmptyTime += TIMER_INTERVAL / 1000f;

                                // Only start decaying after 5 seconds of being empty
                                if (_zoneEmptyTime >= ZONE_DECAY_DELAY)
                                {
                                    // Decay progress
                                    if (_ctZoneTime > 0)
                                    {
                                        _ctZoneTime = Math.Max(0, _ctZoneTime - (TIMER_INTERVAL / 1000f));
                                    }
                                    if (_tZoneTime > 0)
                                    {
                                        _tZoneTime = Math.Max(0, _tZoneTime - (TIMER_INTERVAL / 1000f));
                                    }

                                    // If both teams have no progress, zone is truly neutral again
                                    if (_ctZoneTime <= 0 && _tZoneTime <= 0)
                                    {
                                        _zoneWasOccupied = false;
                                        _zoneEmptyTime = 0f;
                                    }
                                }
                            }
                            break;
                    }

                    // Check for zone capture
                    if (_ctZoneTime >= _captureTime)
                    {
                        OnZoneCaptured("CT");
                    }
                    else if (_tZoneTime >= _captureTime)
                    {
                        OnZoneCaptured("T");
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error in UpdateLockpointTimer: {ex.Message}");
                }
            });
        }

        private void UpdateTeamScore(CsTeam team, int newScore)
        {
            try
            {
                // Find the team entity and update the score
                var teamEntities = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");

                foreach (var teamEntity in teamEntities)
                {
                    if (teamEntity?.TeamNum == (int)team)
                    {
                        teamEntity.Score = newScore;
                        Server.PrintToConsole($"[Lockpoint] Updated {team} score to {newScore}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error updating team score: {ex.Message}");
            }
        }

        private void UpdateLockpointHUD()
        {
            try
            {
                if (_gamePhase != GamePhase.Active)
                    return;

                string currentHudContent;

                if (activeZone == null)
                {
                    // No active zone - use simple text
                    currentHudContent = "⏳ Waiting for zone...";
                }
                else
                {
                    // Show zone capture progress - use simple text formatting
                    var ctProgress = (_ctZoneTime / _captureTime) * 100;
                    var tProgress = (_tZoneTime / _captureTime) * 100;
                    var zoneState = activeZone.GetZoneState();

                    string stateText = zoneState switch
                    {
                        ZoneState.CTControlled => "🔵 CT Capturing",
                        ZoneState.TControlled => "🔴 T Capturing",
                        ZoneState.Contested => "🟣 CONTESTED",
                        ZoneState.Neutral => "🟢 Neutral",
                        _ => "❓ Unknown"
                    };

                    currentHudContent = $"Zone: {activeZone.Name}\n" +
                                    $"{stateText}\n" +
                                    $"[{_ctScore}] CT: {ctProgress:F0}% | [{_tScore}] T: {tProgress:F0}%";
                }

                // Use PrintToCenter instead of PrintToCenterHtml
                var allPlayers = Utilities.GetPlayers()
                    .Where(p => p?.IsValid == true && p.Connected == PlayerConnectedState.PlayerConnected && !p.IsBot)
                    .ToList();

                foreach (var player in allPlayers)
                {
                    if (!_respawningPlayers.Contains(player) && player.PawnIsAlive)
                    {
                        player.PrintToCenter(currentHudContent);
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error updating HUD: {ex.Message}");
            }
        }

        private void DisplayCountdownHUD()
        {
            var activePlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot)
                .ToList();

            foreach (var player in activePlayers)
            {
                if (player?.IsValid == true)
                {
                    player.PrintToCenter(_countdownMessage);
                }
            }
        }

        private void UpdateEditModeHUD()
        {
            var activePlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot)
                .ToList();

            foreach (var player in activePlayers)
            {
                if (player?.IsValid == true)
                {
                    var message = "🔧 EDIT MODE - Game is paused";
                    if (_zoneBeingEdited != null)
                    {
                        message += $"\nEditing: {_zoneBeingEdited.Name}";
                    }
                    player.PrintToCenter(message);
                }
            }
        }

        private void UpdateWarmupHUD()
        {
            // This is the code you already have in your UpdateHUD method
            var activePlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot &&
                        p.TeamNum != (byte)CsTeam.None &&
                        p.TeamNum != (byte)CsTeam.Spectator)
                .ToList();

            string warmupMessage = "";

            if (activePlayers.Count < 2)
            {
                warmupMessage = "⏳ Need at least 2 players to start...";
            }
            else if (_requireTeamReady)
            {
                var ctPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
                var tPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();

                if (ctPlayers.Count == 0 || tPlayers.Count == 0)
                {
                    warmupMessage = "⏳ Need players on both teams to start...";
                }
                else
                {
                    var readyCT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.CounterTerrorist);
                    var readyT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.Terrorist);

                    if (!readyCT && !readyT)
                        warmupMessage = "Type !ready to start the game";
                    else if (!readyCT)
                        warmupMessage = "⏳ Waiting for Counter-Terrorists to ready up";
                    else if (!readyT)
                        warmupMessage = "⏳ Waiting for Terrorists to ready up";
                    else
                        warmupMessage = "✅ All teams ready - starting game!";
                }
            }
            else
            {
                warmupMessage = $"Ready: {_readyPlayers.Count}/{activePlayers.Count} - Type !ready";
            }

            foreach (var player in activePlayers)
            {
                if (player?.IsValid == true)
                {
                    if (!_respawningPlayers.Contains(player) && player.PawnIsAlive)
                    {
                        // Your warmup HUD content here
                        player.PrintToCenter("🔥 LOCKPOINT WARMUP 🔥\nWaiting for admin to start...");
                    }
                }
            }
        }

        private void UpdateActiveGameHUD()
        {
            // Check if we're waiting for a new zone
            if (_waitingForNewZone)
            {
                var timeLeft = _zoneResetTime - DateTime.Now;
                if (timeLeft.TotalSeconds <= 0)
                {
                    // Time to activate new zone - use the dedicated method
                    _waitingForNewZone = false;
                    ActivateNewZone(); // This will show "New Lockpoint: xxx"
                }
                else
                {
                    // Show countdown - NO ACTIVE ZONE during this time
                    var activePlayers = Utilities.GetPlayers()
                        .Where(p => p?.IsValid == true &&
                                p.Connected == PlayerConnectedState.PlayerConnected &&
                                !p.IsBot)
                        .ToList();

                    foreach (var player in activePlayers)
                    {
                        if (player?.IsValid == true)
                        {
                            player.PrintToCenter($"⏳ New zone in {timeLeft.TotalSeconds:F0}s\nScore: CT {_ctScore} - {_tScore} T");
                        }
                    }
                }
                return;
            }

            // Rest of the HUD logic stays the same...
            if (activeZone != null)
            {
                var ctProgress = (_ctZoneTime / _captureTime) * 100f;
                var tProgress = (_tZoneTime / _captureTime) * 100f;

                var ctPlayersInZone = activeZone.PlayersInZone?.Count(p => p.TeamNum == (byte)CsTeam.CounterTerrorist) ?? 0;
                var tPlayersInZone = activeZone.PlayersInZone?.Count(p => p.TeamNum == (byte)CsTeam.Terrorist) ?? 0;

                var activePlayers = Utilities.GetPlayers()
                    .Where(p => p?.IsValid == true &&
                            p.Connected == PlayerConnectedState.PlayerConnected &&
                            !p.IsBot)
                    .ToList();

                foreach (var player in activePlayers)
                {
                    if (player?.IsValid == true)
                    {
                        string status = "";

                        if (ctPlayersInZone > 0 && tPlayersInZone == 0 && tProgress == 0)
                        {
                            status = $"🔵 CT Capturing: {ctProgress:F0}%";
                        }
                        else if (ctPlayersInZone > 0 && tPlayersInZone == 0 && tProgress > 0)
                        {
                            status = $"🔵 CT Capturing: {ctProgress:F0}% (T at {tProgress:F0}%)";
                        }
                        else if (tPlayersInZone > 0 && ctPlayersInZone == 0 && ctProgress == 0)
                        {
                            status = $"🔴 T Capturing: {tProgress:F0}%";
                        }
                        else if (tPlayersInZone > 0 && ctPlayersInZone == 0 && ctProgress > 0)
                        {
                            status = $"🔴 T Capturing: {tProgress:F0}% (CT at {ctProgress:F0}%)";
                        }
                        else if (ctPlayersInZone > 0 && tPlayersInZone > 0)
                        {
                            status = $"⚡ CONTESTED (CT: {ctProgress:F0}% | T: {tProgress:F0}%)";
                        }
                        else if (ctPlayersInZone == 0 && tPlayersInZone == 0 && (ctProgress > 0 || tProgress > 0))
                        {
                            status = $"⚪ Zone clear - CT: {ctProgress:F0}% | T: {tProgress:F0}%";
                        }
                        else
                        {
                            status = "⚪ Zone clear - No progress";
                        }

                        player.PrintToCenter($"📍 {activeZone.Name}\n{status}\nScore: CT {_ctScore} - {_tScore} T");
                    }
                }
            }
        }

        private void UpdateEndedGameHUD()
        {
            var activePlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot)
                .ToList();

            foreach (var player in activePlayers)
            {
                if (player?.IsValid == true)
                {
                    player.PrintToCenter($"🏆 GAME OVER!\nCT {_ctScore} - {_tScore} T\nReturning to warmup...");
                }
            }
        }

        private void PrintNotReadyPlayers(List<CCSPlayerController> activePlayers)
        {
            if (activePlayers.Count < 2)
                return; // Don't spam about ready status if not enough players

            var notReadyPlayers = activePlayers.Where(p => !_readyPlayers.Contains(p)).ToList();

            if (notReadyPlayers.Count > 0)
            {
                var notReadyNames = string.Join(", ", notReadyPlayers.Select(p => p.PlayerName));
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}Not ready: {notReadyNames}{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Yellow}Type !ready to start the game{ChatColors.Default}");
            }
        }

        private string GetNeutralMessage(string zoneName, float ctProgress, float tProgress)
        {
            if (ctProgress == 0 && tProgress == 0)
                return $"⚪ {zoneName} neutral - No progress";
            else
                return $"⚪ {zoneName} neutral - CT: {ctProgress:F0}% | T: {tProgress:F0}%";
        }

        private void ResetZoneAndSelectNew()
        {
            if (activeZone == null) return;

            _previousZone = activeZone;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;

            // Clear current zone visualization
            _zoneVisualization?.ClearZoneVisualization();

            // Set waiting state
            _waitingForNewZone = true;
            _zoneResetTime = DateTime.Now.AddSeconds(_newZoneDelay);

            activeZone = null; // Clear active zone immediately

            // Announce zone cleared and wait period
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} -{ChatColors.Orange} Zone cleared!{ChatColors.Default} Score: {ChatColors.LightBlue}CT {_ctScore}{ChatColors.Default} - {ChatColors.Red}T {_tScore}{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - ⏱{ChatColors.Yellow} New zone in 5 seconds...{ChatColors.Default}");
        }

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            // Stop timers during round transition
            _zoneCheckTimer?.Stop();
            _LockpointTimer?.Stop();

            // Clear all respawn timers
            CleanupAllRespawnTimers();

            // Only reset game state if game is active
            if (_gamePhase == GamePhase.Active)
            {
                // Reset scores and timers for new round
                _ctScore = 0;
                _tScore = 0;
                _ctZoneTime = 0f;
                _tZoneTime = 0f;
                _previousZone = null;

                // Reset team scores in the game
                UpdateTeamScore(CsTeam.CounterTerrorist, 0);
                UpdateTeamScore(CsTeam.Terrorist, 0);
            }
            else
            {
                // In warmup, clear any existing zones
                _zoneVisualization?.ClearZoneVisualization();
                activeZone = null;
            }

            AddTimer(3.0f, () =>
            {
                try
                {
                    // Clear players from all zones
                    foreach (var zone in _zoneManager?.Zones ?? new List<Zone>())
                    {
                        zone.PlayersInZone.Clear();
                    }

                    // Respawn all players with appropriate spawn logic
                    RespawnAllPlayers();

                    if (_gamePhase == GamePhase.Active)
                    {
                        // Wait for respawns to complete before starting zones
                        AddTimer(1.0f, () =>
                        {
                            Server.NextFrame(() =>
                            {
                                DrawRandomZone();

                                // Restart timers only if game is active
                                _zoneCheckTimer?.Start();
                                _LockpointTimer?.Start();
                                _hudTimer?.Start();

                                Server.PrintToChatAll($"{ChatColors.Green}🎮 Lockpoint round started!{ChatColors.Default}");
                            });
                        });
                    }
                    else
                    {
                        // In warmup, only start the HUD timer for warmup messages
                        _LockpointTimer?.Start();
                        Server.PrintToChatAll($"{ChatColors.Yellow}⏳ Warmup phase - Type !ready to start{ChatColors.Default}");
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error in OnRoundStart: {ex.Message}");
                    if (_gamePhase == GamePhase.Active)
                    {
                        _zoneCheckTimer?.Start();
                    }
                    _LockpointTimer?.Start();
                }
            });

            return HookResult.Continue;
        }

        private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            var player = @event.Userid;

            if (player?.IsValid == true)
            {
                // Clean up any respawn timers for disconnecting player
                CleanupRespawnTimer(player);

                // Remove from ready list if they were ready
                _readyPlayers.Remove(player);
            }

            return HookResult.Continue;
        }

        private void CleanupAllRespawnTimers()
        {
            try
            {
                foreach (var timer in _respawnTimers.Values)
                {
                    timer.Stop();
                    timer.Dispose();
                }
                _respawnTimers.Clear();
                _playerDeathTimes.Clear();

                Server.PrintToConsole("[Lockpoint] Cleaned up all respawn timers");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error cleaning up respawn timers: {ex.Message}");
            }
        }

        private void DrawAllZones()
        {
            // Only draw the active zone if one is selected
            if (activeZone != null)
            {
                try
                {
                    // Clear any existing visualizations first
                    _zoneVisualization?.ClearZoneVisualization();

                    Logger.LogInformation($"Drawing active zone: {activeZone.Name} with {activeZone.Points.Count} points");
                    _zoneVisualization?.DrawZone(activeZone);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error drawing active zone {activeZone.Name}: {ex.Message}");
                }
            }
            else
            {
                // If no active zone, clear all visualizations
                _zoneVisualization?.ClearZoneVisualization();
                Logger.LogInformation("No active zone selected - clearing all visualizations");
            }
        }

        private void DrawRandomZone()
        {
            try
            {
                if (_zoneManager?.Zones == null || _zoneManager.Zones.Count == 0)
                {
                    Server.PrintToConsole("[Lockpoint] No zones available to draw");
                    Server.PrintToChatAll($"{ChatColors.Red}[Lockpoint] No zones available!{ChatColors.Default}");
                    return;
                }

                // Get available zones (exclude the previous zone if there are multiple zones)
                var availableZones = _zoneManager.Zones.ToList();

                if (availableZones.Count > 1 && _previousZone != null)
                {
                    availableZones = availableZones.Where(z => z != _previousZone).ToList();
                }

                if (availableZones.Count == 0)
                {
                    Server.PrintToConsole("[Lockpoint] No available zones after filtering previous zone");
                    return;
                }

                // Select a random zone from available zones
                var random = new Random();
                var selectedZone = availableZones[random.Next(availableZones.Count)];

                // Set as active zone
                activeZone = selectedZone;
                activeZone.PlayersInZone.Clear();

                // Reset capture times and state tracking
                _ctZoneTime = 0f;
                _tZoneTime = 0f;
                _zoneEmptyTime = 0f;
                _zoneWasOccupied = false;
                _lastZoneState = ZoneState.Neutral; // Reset state tracking

                // Draw the zone
                _zoneVisualization?.DrawZone(activeZone);

                // Announce the new zone
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - Zone '{activeZone.Name}' is now active!");

                // Store as previous zone for next selection
                _previousZone = activeZone;

                Server.PrintToConsole($"[Lockpoint] New active zone: {activeZone.Name}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error in DrawRandomZone: {ex.Message}");
                Server.PrintToChatAll($"{ChatColors.Red}[Lockpoint] Error selecting new zone!{ChatColors.Default}");
            }
        }

        // Create a separate method for post-capture zone activation
        private void ActivateNewZone()
        {
            try
            {
                if (_zoneManager?.Zones == null || _zoneManager.Zones.Count == 0)
                {
                    Server.PrintToConsole("[Lockpoint] No zones available to activate");
                    return;
                }

                SelectRandomZone(); // This calls the base zone selection logic

                if (activeZone != null)
                {
                    // Reset state tracking for the new zone
                    _lastZoneState = ZoneState.Neutral;
                    
                    _zoneVisualization?.DrawZone(activeZone);
                    // This is the ONLY message that should appear after the 5-second wait
                    Server.PrintToChatAll($"{ChatColors.Yellow}📍 New Lockpoint: {ChatColors.Green}{activeZone.Name}{ChatColors.Default}");
                    Server.PrintToConsole($"[Lockpoint] New zone activated after capture: {activeZone.Name}");
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error activating new zone: {ex.Message}");
            }
        }

        private void CreateTimers()
        {
            // Main game timer - runs every 100ms during active gameplay
            _LockpointTimer = new System.Timers.Timer(TIMER_INTERVAL);
            _LockpointTimer.Elapsed += UpdateLockpointTimer;
            _LockpointTimer.AutoReset = true;

            // Zone check timer - runs every 50ms to check player positions
            _zoneCheckTimer = new System.Timers.Timer(50);
            _zoneCheckTimer.Elapsed += CheckPlayerZones;
            _zoneCheckTimer.AutoReset = true;

            // HUD timer - runs every 500ms to update HUD (should always run, not just during active)
            _hudTimer = new System.Timers.Timer(HUD_UPDATE_INTERVAL);
            _hudTimer.Elapsed += (sender, e) => Server.NextFrame(() => UpdateHUD()); // Call UpdateHUD, not UpdateLockpointHUD
            _hudTimer.AutoReset = true;
            _hudTimer.Start(); // Start immediately when plugin loads
        }

        public override void Unload(bool hotReload)
        {
            _zoneCheckTimer?.Stop();
            _zoneCheckTimer?.Dispose();
            _LockpointTimer?.Stop();
            _LockpointTimer?.Dispose();
            _hudTimer?.Stop();
            _hudTimer?.Dispose();

            CleanupAllRespawnTimers();

            _zoneVisualization?.ClearZoneVisualization();
            Logger.LogInformation("Lockpoint plugin unloaded");
        }

        private void CheckPlayerZones(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    if (_gamePhase == GamePhase.EditMode) return;
                    // Only check the active zone
                    if (activeZone != null)
                    {
                        var previousState = activeZone.GetZoneState();
                        var previousPlayers = new List<CCSPlayerController>(activeZone.PlayersInZone);
                        var previousPlayerCount = previousPlayers.Count; // Add this line

                        // Clear and rebuild the players list
                        activeZone.PlayersInZone.Clear();

                        foreach (var player in Utilities.GetPlayers())
                        {
                            if (player?.IsValid == true &&
                                player.Connected == PlayerConnectedState.PlayerConnected &&
                                player.PlayerPawn?.Value != null &&
                                player.PawnIsAlive)
                            {
                                var playerPos = new CSVector(
                                    player.PlayerPawn.Value.AbsOrigin!.X,
                                    player.PlayerPawn.Value.AbsOrigin!.Y,
                                    player.PlayerPawn.Value.AbsOrigin!.Z
                                );

                                if (activeZone.IsPlayerInZone(playerPos))
                                {
                                    activeZone.PlayersInZone.Add(player);

                                    // Check if this player just entered
                                    if (!previousPlayers.Contains(player))
                                    {
                                        Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} entered zone {activeZone.Name}");
                                    }
                                }
                                else
                                {
                                    // Check if this player just left
                                    if (previousPlayers.Contains(player))
                                    {
                                        Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} left zone {activeZone.Name}");
                                    }
                                }
                            }
                        }

                        var currentState = activeZone.GetZoneState();
                        var currentPlayerCount = activeZone.PlayersInZone.Count;

                        // Update zone color if state changed OR if player count changed
                        if (currentState != previousState)
                        {
                            try
                            {
                                _zoneVisualization?.UpdateZoneColor(activeZone, currentState);

                                if (currentState != previousState)
                                {
                                    Server.PrintToConsole($"[Lockpoint] Zone {activeZone.Name} state changed: {previousState} -> {currentState}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Server.PrintToConsole($"[Lockpoint] Error updating zone color: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error in CheckPlayerZones: {ex.Message}");
                }
            });
        }

        private void CheckReadyStatus()
        {
            var activePlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot)
                .ToList();

            if (activePlayers.Count < 2)
            {
                if (DateTime.Now.Subtract(_lastReadyMessage).TotalSeconds >= _readyMessageInterval)
                {
                    Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}Need at least 2 players to start!{ChatColors.Default}");
                    _lastReadyMessage = DateTime.Now;
                }
                return;
            }

            bool canStart = false;

            if (_requireTeamReady)
            {
                // Check if at least one player from each team is ready
                var readyCT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.CounterTerrorist);
                var readyT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.Terrorist);

                var ctPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
                var tPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();

                canStart = readyCT && readyT && ctPlayers.Count > 0 && tPlayers.Count > 0;
            }
            else
            {
                // Check if all players are ready
                canStart = activePlayers.All(p => _readyPlayers.Contains(p));
            }

            if (canStart)
            {
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}All players ready! Starting game in 3 seconds...{ChatColors.Default}");
                AddTimer(3.0f, StartGame);
            }
        }

        private void RespawnAllPlayers()
        {
            Server.PrintToConsole("[Lockpoint] Respawning all players...");

            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true &&
                    player.Connected == PlayerConnectedState.PlayerConnected &&
                    !player.IsBot &&
                    player.TeamNum != (byte)CsTeam.None &&
                    player.TeamNum != (byte)CsTeam.Spectator)
                {
                    try
                    {
                        // Get spawn point BEFORE respawning
                        Vector? spawnPoint;

                        if (_gamePhase == GamePhase.Warmup)
                        {
                            // Use random spawn during warmup
                            spawnPoint = GetRandomSpawnPointAny();
                        }
                        else if (_gamePhase == GamePhase.Active)
                        {
                            // Use zone-based spawn during active game
                            spawnPoint = GetZoneBasedSpawn(player.TeamNum);
                            Server.PrintToConsole($"[Lockpoint] Getting zone spawn for {player.PlayerName} (Team: {player.TeamNum})");
                        }
                        else
                        {
                            // Use team spawn for other phases
                            spawnPoint = GetRandomSpawnPoint(player.TeamNum);
                        }

                        // Force respawn everyone, dead or alive
                        player.Respawn();

                        // Teleport immediately after respawn - no delay
                        Server.NextFrame(() =>
                        {
                            if (player?.IsValid == true && player.PawnIsAlive && player.PlayerPawn?.Value != null)
                            {
                                if (_gamePhase == GamePhase.Active)
                                {
                                    // Use zone-based spawn with view angle
                                    var spawnPointWithAngle = GetZoneBasedSpawnWithAngle(player.TeamNum);
                                    if (spawnPointWithAngle != null)
                                    {
                                        player.PlayerPawn.Value.Teleport(
                                            new Vector(spawnPointWithAngle.Position.X, spawnPointWithAngle.Position.Y, spawnPointWithAngle.Position.Z),
                                            spawnPointWithAngle.ViewAngle, // Use stored view angle
                                            new Vector(0, 0, 0)
                                        );
                                        Server.PrintToConsole($"[Lockpoint] Teleported {player.PlayerName} to zone spawn with angle {spawnPointWithAngle.ViewAngle.Y:F1}°");
                                    }
                                    else if (spawnPoint != null)
                                    {
                                        player.PlayerPawn.Value.Teleport(spawnPoint, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                        Server.PrintToConsole($"[Lockpoint] Used fallback spawn for {player.PlayerName}");
                                    }
                                }
                                else if (spawnPoint != null)
                                {
                                    player.PlayerPawn.Value.Teleport(spawnPoint, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                    Server.PrintToConsole($"[Lockpoint] Instantly teleported {player.PlayerName} to appropriate spawn");
                                }
                                else
                                {
                                    Server.PrintToConsole($"[Lockpoint] Failed to get spawn point for {player.PlayerName}");
                                }
                            }
                        });

                        Server.PrintToConsole($"[Lockpoint] Respawned player: {player.PlayerName}");

                        if (_gamePhase == GamePhase.Active)
                        {
                            player.PrintToChat($"{ChatColors.Green}Game started! Fight for the zone!{ChatColors.Default}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Server.PrintToConsole($"[Lockpoint] Error respawning player {player.PlayerName}: {ex.Message}");
                    }
                }
            }
        }

        private Vector? GetRandomSpawnPoint(byte teamNum)
        {
            try
            {
                // Get spawn points for the team
                var spawnEntities = teamNum == (byte)CsTeam.CounterTerrorist
                    ? Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist")
                    : Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist");

                if (spawnEntities.Any())
                {
                    var random = new Random();
                    var spawnPoint = spawnEntities.ElementAt(random.Next(spawnEntities.Count()));
                    return spawnPoint.AbsOrigin;
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting spawn point: {ex.Message}");
            }

            return null;
        }

        private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            var player = @event.Userid;

            if (player?.IsValid != true || player.IsBot)
                return HookResult.Continue;
            try
            {
                if (_gamePhase == GamePhase.Warmup)
                {
                    // Instant respawn during warmup at a random spawn point
                    Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} died in warmup, respawning instantly");

                    AddTimer(0.1f, () => // Very short delay to ensure death is processed
                    {
                        if (player?.IsValid == true && !player.PawnIsAlive)
                        {
                            RespawnPlayerAtRandomSpawn(player);
                        }
                    });
                }
                else if (_gamePhase == GamePhase.Active)
                {
                    // 5-second respawn timer during active game
                    _playerDeathTimes[player] = DateTime.Now;
                    _respawningPlayers.Add(player);

                    // Clear any existing respawn timer for this player
                    if (_respawnTimers.ContainsKey(player))
                    {
                        _respawnTimers[player].Stop();
                        _respawnTimers[player].Dispose();
                        _respawnTimers.Remove(player);
                    }

                    Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} died, will respawn in {_respawnDelay} seconds");

                    // Create respawn timer with null check
                    var respawnTimer = new System.Timers.Timer(100); // Update every 100ms for smooth countdown
                    respawnTimer.Elapsed += (sender, e) =>
                    {
                        if (player?.IsValid == true)
                        {
                            UpdateRespawnCountdown(player);
                        }
                        else
                        {
                            respawnTimer.Stop();
                            respawnTimer.Dispose();
                        }
                    };
                    respawnTimer.Start();

                    _respawnTimers[player] = respawnTimer;

                    // Schedule the actual respawn with null check
                    AddTimer(_respawnDelay, () =>
                    {
                        if (player?.IsValid == true)
                        {
                            RespawnPlayer(player);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error handling player death: {ex.Message}");
            }

            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            var player = @event.Userid;
            if (player?.IsValid != true || player.IsBot)
                return HookResult.Continue;

            _respawningPlayers.Remove(player);

            // Give equipment after spawn with a small delay
            Task.Delay(100).ContinueWith(_ =>
            {
                Server.NextFrame(() =>
                {
                    GivePlayerEquipment(player);
                    GiveRandomUtility(player);
                });
            });

            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
        {
            var player = @event.Userid;
            if (player?.IsValid != true || player.IsBot)
                return HookResult.Continue;

            // Give equipment after team change with a delay
            Task.Delay(200).ContinueWith(_ =>
            {
                Server.NextFrame(() =>
                {
                    if (player.PawnIsAlive)
                    {
                        GivePlayerEquipment(player);
                    }
                });
            });

            return HookResult.Continue;
        }

        private void RespawnPlayerAtRandomSpawn(CCSPlayerController player)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    if (player?.IsValid != true)
                        return;

                    // Try to respawn the player first
                    player.Respawn();

                    Server.NextFrame(() =>
                    {
                        Server.NextFrame(() =>
                        {
                            if (player?.IsValid == true && player.PawnIsAlive && player.PlayerPawn?.Value != null)
                            {
                                var randomSpawn = GetDeathmatchSpawn(); // Use deathmatch spawns preferentially
                                if (randomSpawn != null)
                                {
                                    player.PlayerPawn.Value.Teleport(randomSpawn, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                    Server.PrintToConsole($"[Lockpoint] Teleported {player.PlayerName} to deathmatch spawn");
                                }
                            }
                            GivePlayerEquipment(player);
                        });
                    });

                    player.PrintToChat($"{ChatColors.Green}Respawned instantly (warmup mode)!{ChatColors.Default}");
                    Server.PrintToConsole($"[Lockpoint] Instantly respawned player: {player.PlayerName}");
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error instantly respawning player {player?.PlayerName}: {ex.Message}");
                }
            });
        }

        private Vector? GetRandomSpawnPointAny()
        {
            try
            {
                var allSpawns = new List<Vector>();

                // First priority: Use deathmatch spawns if they exist
                var dmSpawns = Utilities.FindAllEntitiesByDesignerName<CInfoDeathmatchSpawn>("info_deathmatch_spawn");
                if (dmSpawns.Any())
                {
                    allSpawns.AddRange(dmSpawns
                        .Where(spawn => spawn?.AbsOrigin != null)
                        .Select(spawn => spawn.AbsOrigin!));

                    Server.PrintToConsole($"[Lockpoint] Found {dmSpawns.Count()} deathmatch spawn points");
                }

                // If no deathmatch spawns, fall back to regular spawns
                if (!allSpawns.Any())
                {
                    // Add CT spawns
                    var ctSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist");
                    allSpawns.AddRange(ctSpawns
                        .Where(spawn => spawn?.AbsOrigin != null)
                        .Select(spawn => spawn.AbsOrigin!));

                    // Add T spawns
                    var tSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist");
                    allSpawns.AddRange(tSpawns
                        .Where(spawn => spawn?.AbsOrigin != null)
                        .Select(spawn => spawn.AbsOrigin!));

                    Server.PrintToConsole($"[Lockpoint] No deathmatch spawns found, using {allSpawns.Count} team spawn points");
                }

                // Try other spawn types as additional options
                var armsraceSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_armsrace_spawn");
                allSpawns.AddRange(armsraceSpawns
                    .Where(spawn => spawn?.AbsOrigin != null)
                    .Select(spawn => spawn.AbsOrigin!));

                // Also add zone center points as potential spawns during warmup
                if (_zoneManager?.Zones != null)
                {
                    foreach (var zone in _zoneManager.Zones)
                    {
                        if (zone.Points?.Count > 0)
                        {
                            // Calculate zone center
                            var centerX = zone.Points.Average(p => p.X);
                            var centerY = zone.Points.Average(p => p.Y);
                            var centerZ = zone.Points.Average(p => p.Z);
                            allSpawns.Add(new Vector(centerX, centerY, centerZ + 10)); // +10 to spawn slightly above ground
                        }
                    }
                }

                if (allSpawns.Any())
                {
                    var random = new Random();
                    var selectedSpawn = allSpawns[random.Next(allSpawns.Count)];
                    Server.PrintToConsole($"[Lockpoint] Selected random spawn from {allSpawns.Count} available spawns");
                    return selectedSpawn;
                }
                else
                {
                    Server.PrintToConsole("[Lockpoint] No spawn points found");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting random spawn point: {ex.Message}");
                return null;
            }
        }

        private Vector? GetDeathmatchSpawn()
        {
            try
            {
                // Try to get deathmatch spawns first
                var dmSpawns = Utilities.FindAllEntitiesByDesignerName<CInfoDeathmatchSpawn>("info_deathmatch_spawn");

                if (dmSpawns.Any())
                {
                    var random = new Random();
                    var selectedSpawn = dmSpawns.ElementAt(random.Next(dmSpawns.Count()));
                    Server.PrintToConsole($"[Lockpoint] Using deathmatch spawn point");
                    return selectedSpawn.AbsOrigin;
                }

                // Fallback to regular random spawn
                Server.PrintToConsole("[Lockpoint] No deathmatch spawns found, falling back to regular spawns");
                return GetRandomSpawnPointAny();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting deathmatch spawn: {ex.Message}");
                return GetRandomSpawnPointAny();
            }
        }

        private void UpdateRespawnCountdown(CCSPlayerController? player)
        {
            if (player?.IsValid != true)
                return;

            Server.NextFrame(() =>
            {
                try
                {
                    if (player?.IsValid != true || !_playerDeathTimes.ContainsKey(player))
                        return;

                    // Check if player is already alive (respawned by other means)
                    if (player.PawnIsAlive)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    var timeSinceDeath = (DateTime.Now - _playerDeathTimes[player]).TotalSeconds;
                    var remainingTime = Math.Max(0, _respawnDelay - timeSinceDeath);

                    if (remainingTime <= 0)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    // Show countdown message with only seconds (no decimals)
                    var remainingSeconds = (int)Math.Ceiling(remainingTime);
                    var countdownMessage = $"💀 Respawning in {remainingSeconds}s...";
                    player.PrintToCenter(countdownMessage);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error updating respawn countdown: {ex.Message}");
                }
            });
        }

        private void RespawnPlayer(CCSPlayerController? player)
        {
            if (player?.IsValid != true)
                return;

            Server.NextFrame(() =>
            {
                try
                {
                    if (player?.IsValid != true || player.PawnIsAlive)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    // Only respawn if game is still active
                    if (_gamePhase != GamePhase.Active)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    // Get spawn point BEFORE respawning
                    var spawnPointWithAngle = GetZoneBasedSpawnWithAngle(player.TeamNum);

                    player.Respawn();

                    // Teleport immediately after respawn - no delay
                    Server.NextFrame(() =>
                    {
                        if (player?.IsValid == true && player.PawnIsAlive && player.PlayerPawn?.Value != null)
                        {
                            if (spawnPointWithAngle != null)
                            {
                                // Use the full spawn point with view angle
                                player.PlayerPawn.Value.Teleport(
                                    new Vector(spawnPointWithAngle.Position.X, spawnPointWithAngle.Position.Y, spawnPointWithAngle.Position.Z),
                                    spawnPointWithAngle.ViewAngle, // Use the stored view angle instead of new QAngle(0, 0, 0)
                                    new Vector(0, 0, 0)
                                );
                                Server.PrintToConsole($"[Lockpoint] Teleported {player.PlayerName} to zone spawn with view angle: {spawnPointWithAngle.ViewAngle.Y:F1}°");
                            }
                            else
                            {
                                // Fallback to regular spawn without angle
                                var fallbackSpawn = GetRandomSpawnPoint(player.TeamNum);
                                if (fallbackSpawn != null)
                                {
                                    player.PlayerPawn.Value.Teleport(fallbackSpawn, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                    Server.PrintToConsole($"[Lockpoint] Used fallback spawn for {player.PlayerName}");
                                }
                            }
                        }
                    });

                    player.PrintToChat($"{ChatColors.Green}You have been respawned!{ChatColors.Default}");
                    Server.PrintToConsole($"[Lockpoint] Respawned player: {player.PlayerName}");

                    CleanupRespawnTimer(player);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error respawning player {player?.PlayerName}: {ex.Message}");
                }
            });
        }

        private void CleanupRespawnTimer(CCSPlayerController? player)
        {
            if (player?.IsValid != true)
                return;

            try
            {
                if (_respawnTimers.ContainsKey(player))
                {
                    _respawnTimers[player].Stop();
                    _respawnTimers[player].Dispose();
                    _respawnTimers.Remove(player);
                }

                _playerDeathTimes.Remove(player);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error cleaning up respawn timer: {ex.Message}");
            }
        }

        #region Commands

        [ConsoleCommand("css_savezones", "Saves all zones to file.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandSaveZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to create zones! Use !edit{ChatColors.Default}");
                return;
            }
            var mapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(mapName, _zoneManager.Zones);
            commandInfo.ReplyToCommand($"Saved {_zoneManager?.Zones.Count} zones for map {mapName}");
        }

        // Add command to reload zones
        [ConsoleCommand("css_reloadzones", "Reloads zones from file.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandReloadZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var mapName = Server.MapName;
            _zoneVisualization?.ClearZoneVisualization();
            _zoneManager?.LoadZonesForMap(mapName);

            foreach (var zone in _zoneManager?.Zones ?? new List<Zone>())
            {
                _zoneVisualization?.DrawZone(zone);
            }

            commandInfo.ReplyToCommand($"Reloaded {_zoneManager?.Zones.Count} zones for map {mapName}");
        }

        [ConsoleCommand("css_addzone", "Create a new zone (Admin only, Edit mode required).")]
        [CommandHelper(minArgs: 1, usage: "<zone_name>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use css_editmode{ChatColors.Default}");
                return;
            }

            var zoneName = commandInfo.GetArg(1);

            // Fix: Check if zone exists using the correct method
            if (_zoneManager?.Zones?.Any(z => z.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase)) == true)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Zone '{zoneName}' already exists!{ChatColors.Default}");
                return;
            }

            // Fix: Use the correct Zone constructor
            _zoneBeingEdited = new Zone
            {
                Name = zoneName,
                Points = new List<CSVector>(),
                CounterTerroristSpawns = new List<Zone.SpawnPoint>(),
                TerroristSpawns = new List<Zone.SpawnPoint>()
            };
            _isEditingExistingZone = false;

            // Set edit mode with the new zone being created (will make it yellow when points are added)
            _zoneVisualization?.SetEditMode(true, _zoneBeingEdited);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Created new zone: {zoneName}{ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Yellow}Zone will appear in yellow when you add points. Use css_addpoint to start defining the zone.{ChatColors.Default}");
        }

        [ConsoleCommand("css_z", "Create a new zone (Admin only, Edit mode required).")]
        [CommandHelper(minArgs: 1, usage: "<zone_name>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandZ(CCSPlayerController? player, CommandInfo commandInfo)
        {
            // Alias for css_addzone
            OnCommandAddZone(player, commandInfo);
        }

        [ConsoleCommand("css_addpoint", "Add a point to the zone being edited (Admin only, Edit mode required).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddPoint(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use css_editmode{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone is being edited. Use css_addzone to create a new zone or css_editzone to edit an existing one.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            _zoneBeingEdited.Points.Add(playerPos);

            // Update visualization to show yellow zone with new point
            _zoneVisualization?.SetEditMode(true, _zoneBeingEdited);
            _zoneVisualization?.DrawZone(_zoneBeingEdited);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Added point {_zoneBeingEdited.Points.Count} to zone '{_zoneBeingEdited.Name}'{ChatColors.Default}");

            if (_zoneBeingEdited.Points.Count >= 3)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}Zone now has {_zoneBeingEdited.Points.Count} points and is visible in yellow!{ChatColors.Default}");
            }

            Server.PrintToConsole($"[Lockpoint] Added point {_zoneBeingEdited.Points.Count} to zone '{_zoneBeingEdited.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_p", "Add a point to the zone being edited (Admin only, Edit mode required).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandP(CCSPlayerController? player, CommandInfo commandInfo)
        {
            // Alias for css_addpoint
            OnCommandAddPoint(player, commandInfo);
        }

        [ConsoleCommand("css_savezone", "Save the zone being edited (Admin only, Edit mode required).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandSaveZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use css_editmode{ChatColors.Default}");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone is being edited.{ChatColors.Default}");
                return;
            }

            if (_zoneBeingEdited.Points.Count < 3)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Zone must have at least 3 points to be valid! Current: {_zoneBeingEdited.Points.Count}{ChatColors.Default}");
                return;
            }

            if (_zoneBeingEdited.CounterTerroristSpawns.Count == 0 || _zoneBeingEdited.TerroristSpawns.Count == 0)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Zone must have at least one spawn point for each team!{ChatColors.Default}");
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}CT spawns: {_zoneBeingEdited.CounterTerroristSpawns.Count}, T spawns: {_zoneBeingEdited.TerroristSpawns.Count}{ChatColors.Default}");
                return;
            }

            try
            {
                if (_isEditingExistingZone)
                {
                    // Fix: Zone is already in the list, just save
                    // No need to update since we're modifying the existing object
                }
                else
                {
                    // Fix: Add the zone to the manager's list
                    _zoneManager?.Zones?.Add(_zoneBeingEdited);
                }

                // Fix: Use the correct save method
                _zoneManager?.SaveZonesForMap(Server.MapName, _zoneManager.Zones);

                var zoneName = _zoneBeingEdited.Name;
                _zoneBeingEdited = null;
                _isEditingExistingZone = false;

                // Clear edit mode visualization for this zone
                _zoneVisualization?.ClearZoneVisualization();

                commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{zoneName}' saved successfully!{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Zone '{zoneName}' saved by {player?.PlayerName}");
            }
            catch (Exception ex)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Error saving zone: {ex.Message}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Error saving zone: {ex.Message}");
            }
        }

        [ConsoleCommand("css_s", "Save the zone being edited (Admin only, Edit mode required).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandS(CCSPlayerController? player, CommandInfo commandInfo)
        {
            // Alias for css_savezone
            OnCommandSaveZone(player, commandInfo);
        }

        [ConsoleCommand("css_editzone", "Edit an existing zone (Admin only).")]
        [CommandHelper(minArgs: 1, usage: "<zone_name>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandEditZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use css_editmode{ChatColors.Default}");
                return;
            }

            var zoneName = commandInfo.GetArg(1);
            // Fix: Use the correct method name from your ZoneManager
            var zone = _zoneManager?.Zones?.FirstOrDefault(z => z.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));

            if (zone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Zone '{zoneName}' not found!{ChatColors.Default}");
                return;
            }

            _zoneBeingEdited = zone;
            _isEditingExistingZone = true;

            // Set edit mode with the specific zone being edited (will make it yellow)
            _zoneVisualization?.SetEditMode(true, _zoneBeingEdited);
            _zoneVisualization?.DrawZone(_zoneBeingEdited);
            _zoneVisualization?.DrawSpawnPoints(_zoneBeingEdited);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Now editing zone: {zoneName}{ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Yellow}Zone is now highlighted in yellow. Use zone commands to modify.{ChatColors.Default}");
        }

        [ConsoleCommand("css_cancelzone", "Cancel editing the current zone (Admin only, Edit mode required).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandCancelZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use css_editmode{ChatColors.Default}");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand("No zone is being edited.");
                return;
            }

            var zoneName = _zoneBeingEdited.Name;

            // Clear all visualizations for the zone we're canceling
            _zoneVisualization?.ClearSpawnPoints(_zoneBeingEdited);
            _zoneVisualization?.ClearZoneVisualization();

            _zoneBeingEdited = null;
            _isEditingExistingZone = false;

            // Reset edit mode without specific zone
            _zoneVisualization?.SetEditMode(true);

            commandInfo.ReplyToCommand($"{ChatColors.Yellow}Cancelled editing zone '{zoneName}'.{ChatColors.Default}");
        }

        [ConsoleCommand("css_ct", "Add a CT spawn point to the current zone being edited.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandCT(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use css_editmode{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone is being edited. Use css_editzone or create a new zone first.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            var playerAngle = new QAngle(
                player.PlayerPawn.Value.EyeAngles.X,
                player.PlayerPawn.Value.EyeAngles.Y,
                player.PlayerPawn.Value.EyeAngles.Z
            );

            var spawnPoint = new Zone.SpawnPoint(playerPos, playerAngle);
            _zoneBeingEdited.CounterTerroristSpawns.Add(spawnPoint);

            commandInfo.ReplyToCommand($"{ChatColors.Blue}Added CT spawn to zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.CounterTerroristSpawns.Count} CT spawns total){ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Yellow}Position: {playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1}{ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Yellow}View Angle: {playerAngle.X:F1}, {playerAngle.Y:F1}, {playerAngle.Z:F1}{ChatColors.Default}");

            // Update spawn visualization
            _zoneVisualization?.DrawSpawnPoints(_zoneBeingEdited);

            Server.PrintToConsole($"[Lockpoint] Added CT spawn to zone '{_zoneBeingEdited.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_t", "Add a T spawn point to the current zone being edited.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandT(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use css_editmode{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone is being edited. Use css_editzone or create a new zone first.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            var playerAngle = new QAngle(
                player.PlayerPawn.Value.EyeAngles.X,
                player.PlayerPawn.Value.EyeAngles.Y,
                player.PlayerPawn.Value.EyeAngles.Z
            );

            var spawnPoint = new Zone.SpawnPoint(playerPos, playerAngle);
            _zoneBeingEdited.TerroristSpawns.Add(spawnPoint);

            commandInfo.ReplyToCommand($"{ChatColors.Red}Added T spawn to zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.TerroristSpawns.Count} T spawns total){ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Yellow}Position: {playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1}{ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Yellow}View Angle: {playerAngle.X:F1}, {playerAngle.Y:F1}, {playerAngle.Z:F1}{ChatColors.Default}");

            // Update spawn visualization
            _zoneVisualization?.DrawSpawnPoints(_zoneBeingEdited);

            Server.PrintToConsole($"[Lockpoint] Added T spawn to zone '{_zoneBeingEdited.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_removespawn", "Remove the closest spawn point from the current zone being edited.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandRemoveSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone is being edited. Use css_editzone or create a new zone first.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            // Find closest spawn point
            CSVector? closestSpawn = null;
            bool isCtSpawn = false;
            float closestDistance = float.MaxValue;

            // Check CT spawns
            foreach (var spawn in _zoneBeingEdited.CounterTerroristSpawns)
            {
                var distance = CalculateDistance(playerPos, spawn.Position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestSpawn = spawn.Position;
                    isCtSpawn = true;
                }
            }

            // Check T spawns
            foreach (var spawn in _zoneBeingEdited.TerroristSpawns)
            {
                var distance = CalculateDistance(playerPos, spawn.Position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestSpawn = spawn.Position;
                    isCtSpawn = false;
                }
            }

            if (closestSpawn == null || closestDistance > 100.0f) // Within 100 units
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No spawn points found nearby (within 100 units).{ChatColors.Default}");
                return;
            }

            // Remove the closest spawn
            if (isCtSpawn)
            {
                var spawnToRemove = _zoneBeingEdited.CounterTerroristSpawns.FirstOrDefault(sp => sp.Position == closestSpawn);
                if (spawnToRemove != null)
                {
                    _zoneBeingEdited.CounterTerroristSpawns.Remove(spawnToRemove);
                }
                commandInfo.ReplyToCommand($"{ChatColors.Blue}Removed CT spawn from zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.CounterTerroristSpawns.Count} CT spawns remaining){ChatColors.Default}");
            }
            else
            {
                var spawnToRemove = _zoneBeingEdited.TerroristSpawns.FirstOrDefault(sp => sp.Position == closestSpawn);
                if (spawnToRemove != null)
                {
                    _zoneBeingEdited.TerroristSpawns.Remove(spawnToRemove);
                }
                commandInfo.ReplyToCommand($"{ChatColors.Red}Removed T spawn from zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.TerroristSpawns.Count} T spawns remaining){ChatColors.Default}");
            }

            // Update spawn visualization
            _zoneVisualization?.DrawSpawnPoints(_zoneBeingEdited);

            Server.PrintToConsole($"[Lockpoint] Removed {(isCtSpawn ? "CT" : "T")} spawn from zone '{_zoneBeingEdited.Name}' by {player.PlayerName}");
        }

        private float CalculateDistance(CSVector pos1, CSVector pos2)
        {
            var dx = pos1.X - pos2.X;
            var dy = pos1.Y - pos2.Y;
            var dz = pos1.Z - pos2.Z;

            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        [ConsoleCommand("css_removezone", "Remove a zone (Admin only, Edit mode required).")]
        [CommandHelper(minArgs: 0, usage: "[zone_name]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandRemoveZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to remove zones! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var zoneName = commandInfo.GetArg(1);
            Zone? zoneToRemove = null;

            if (string.IsNullOrWhiteSpace(zoneName))
            {
                // No zone name specified, find the closest zone
                zoneToRemove = FindClosestZone(player);

                if (zoneToRemove == null)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}No zones found nearby.{ChatColors.Default}");
                    return;
                }
            }
            else
            {
                // Zone name specified, find by name
                zoneToRemove = _zoneManager?.Zones?.FirstOrDefault(z =>
                    string.Equals(z.Name, zoneName, StringComparison.OrdinalIgnoreCase));

                if (zoneToRemove == null)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}Zone '{zoneName}' not found.{ChatColors.Default}");
                    return;
                }
            }

            try
            {
                // Remove from zone manager
                var mapName = Server.MapName;

                if (_zoneManager?.Zones != null)
                {
                    _zoneManager.Zones.Remove(zoneToRemove);
                    _zoneManager.SaveZonesForMap(mapName, _zoneManager.Zones); // Save the updated zone list
                }


                // Clear active zone if it was the one being removed
                if (activeZone == zoneToRemove)
                {
                    activeZone = null;
                }

                // Update visualization in edit mode
                Server.NextFrame(() =>
                {
                    DrawAllZonesForEdit();
                });

                commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{zoneToRemove.Name}' has been removed.{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Yellow}Zone '{zoneToRemove.Name}' was removed by {player.PlayerName}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Zone '{zoneToRemove.Name}' removed by {player.PlayerName}");
            }
            catch (Exception ex)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Error removing zone: {ex.Message}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Error removing zone '{zoneToRemove.Name}': {ex.Message}");
            }
        }

        private Zone? FindClosestZone(CCSPlayerController player)
        {
            if (player?.PlayerPawn?.Value?.AbsOrigin == null || _zoneManager?.Zones == null)
                return null;

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin.X,
                player.PlayerPawn.Value.AbsOrigin.Y,
                player.PlayerPawn.Value.AbsOrigin.Z
            );

            Zone? closestZone = null;
            float closestDistance = float.MaxValue;

            foreach (var zone in _zoneManager.Zones)
            {
                if (zone.Points == null || zone.Points.Count == 0)
                    continue;

                // Calculate distance to zone center
                var centerX = zone.Points.Average(p => p.X);
                var centerY = zone.Points.Average(p => p.Y);
                var centerZ = zone.Points.Average(p => p.Z);

                var zoneCenter = new CSVector(centerX, centerY, centerZ);
                var distance = CalculateDistance(playerPos, zoneCenter);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestZone = zone;
                }
            }

            // Only return if the closest zone is within reasonable distance (e.g., 500 units)
            if (closestDistance <= 500.0f)
            {
                return closestZone;
            }

            return null;
        }

        private Vector? GetZoneBasedSpawn(byte teamNum)
        {
            // Only use zone-based spawns during active game phase
            if (_gamePhase != GamePhase.Active || activeZone == null)
            {
                return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
            }

            try
            {
                List<Zone.SpawnPoint> teamSpawns = teamNum == (byte)CsTeam.CounterTerrorist
                    ? activeZone.CounterTerroristSpawns
                    : activeZone.TerroristSpawns;

                if (teamSpawns.Count > 0)
                {
                    // Try to find a spawn point that's not too close to other players
                    var availableSpawns = FindAvailableSpawnPointsWithAngles(teamSpawns);

                    if (availableSpawns.Count > 0)
                    {
                        var random = new Random();
                        var spawnPoint = availableSpawns[random.Next(availableSpawns.Count)];
                        return new Vector(spawnPoint.Position.X, spawnPoint.Position.Y, spawnPoint.Position.Z);
                    }
                    else
                    {
                        Server.PrintToConsole($"[Lockpoint] All zone spawns occupied for team {teamNum} in zone {activeZone.Name}, using default spawns");
                        return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
                    }
                }
                else
                {
                    Server.PrintToConsole($"[Lockpoint] No zone spawns for team {teamNum} in zone {activeZone.Name}, using default spawns");
                    return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting zone-based spawn: {ex.Message}");
                return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
            }
        }

        private Zone.SpawnPoint? GetZoneBasedSpawnWithAngle(byte teamNum)
        {
            if (_gamePhase != GamePhase.Active || activeZone == null)
            {
                return null;
            }

            try
            {
                List<Zone.SpawnPoint> teamSpawns = teamNum == (byte)CsTeam.CounterTerrorist
                    ? activeZone.CounterTerroristSpawns
                    : activeZone.TerroristSpawns;

                if (teamSpawns.Count > 0)
                {
                    var availableSpawns = FindAvailableSpawnPointsWithAngles(teamSpawns);

                    if (availableSpawns.Count > 0)
                    {
                        var random = new Random();
                        return availableSpawns[random.Next(availableSpawns.Count)];
                    }
                    else if (teamSpawns.Count > 0)
                    {
                        // If all spawns are occupied, just use a random one
                        var random = new Random();
                        return teamSpawns[random.Next(teamSpawns.Count)];
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting zone-based spawn with angle: {ex.Message}");
            }

            return null;
        }
        private List<CSVector> FindAvailableSpawnPoints(List<CSVector> spawnPoints)
        {
            var availableSpawns = new List<CSVector>();
            const float minDistance = 100.0f; // Minimum distance between players (adjust as needed)

            var allPlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot &&
                        p.PawnIsAlive &&
                        p.PlayerPawn?.Value?.AbsOrigin != null)
                .ToList();

            foreach (var spawnPoint in spawnPoints)
            {
                bool isTooClose = false;

                foreach (var player in allPlayers)
                {
                    var playerPos = new CSVector(
                        player.PlayerPawn!.Value!.AbsOrigin!.X,
                        player.PlayerPawn.Value.AbsOrigin.Y,
                        player.PlayerPawn.Value.AbsOrigin.Z
                    );

                    var distance = CalculateDistance(spawnPoint, playerPos);
                    if (distance < minDistance)
                    {
                        isTooClose = true;
                        break;
                    }
                }

                if (!isTooClose)
                {
                    availableSpawns.Add(spawnPoint);
                }
            }

            return availableSpawns;
        }

        private List<Zone.SpawnPoint> FindAvailableSpawnPointsWithAngles(List<Zone.SpawnPoint> spawnPoints)
        {
            var availableSpawns = new List<Zone.SpawnPoint>();
            const float minDistance = 100.0f; // Minimum distance between players

            var allPlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot &&
                        p.PawnIsAlive &&
                        p.PlayerPawn?.Value?.AbsOrigin != null)
                .ToList();

            foreach (var spawnPoint in spawnPoints)
            {
                bool isTooClose = false;

                foreach (var player in allPlayers)
                {
                    var playerPos = new CSVector(
                        player.PlayerPawn!.Value!.AbsOrigin!.X,
                        player.PlayerPawn.Value.AbsOrigin.Y,
                        player.PlayerPawn.Value.AbsOrigin.Z
                    );

                    var distance = CalculateDistance(spawnPoint.Position, playerPos);
                    if (distance < minDistance)
                    {
                        isTooClose = true;
                        break;
                    }
                }

                if (!isTooClose)
                {
                    availableSpawns.Add(spawnPoint);
                }
            }

            return availableSpawns;
        }

        [ConsoleCommand("css_listzones", "Lists all zones.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandListZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var zones = _zoneManager?.Zones ?? new List<Zone>();
            commandInfo.ReplyToCommand($"Total zones: {zones.Count}");

            foreach (var zone in zones)
            {
                commandInfo.ReplyToCommand($"- {zone.Name} ({zone.Points.Count} points)");
            }
        }

        [ConsoleCommand("css_redrawzones", "Redraws all zones.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandRedrawZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_zoneVisualization == null || _zoneManager == null)
            {
                commandInfo.ReplyToCommand("Zone system not initialized");
                return;
            }

            _zoneVisualization.ClearZoneVisualization();

            foreach (var zone in _zoneManager.Zones)
            {
                Server.PrintToConsole($"Redrawing zone: {zone.Name}");
                _zoneVisualization.DrawZone(zone);
            }

            commandInfo.ReplyToCommand($"Redrawn {_zoneManager.Zones.Count} zones");
        }

        [ConsoleCommand("css_selectzone", "Select a specific zone by ID or name for testing (Admin only).")]
        [CommandHelper(minArgs: 1, usage: "<zone_id_or_name>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandSelectZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_gamePhase != GamePhase.Active)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Game must be active to select zones!{ChatColors.Default}");
                return;
            }

            var zoneIdentifier = commandInfo.GetArg(1);
            Zone? selectedZone = null;

            // Try to parse as ID first
            if (int.TryParse(zoneIdentifier, out int zoneId))
            {
                selectedZone = _zoneManager?.Zones?.FirstOrDefault(z => z.Id == zoneId);
            }
            else
            {
                // Search by name
                selectedZone = _zoneManager?.Zones?.FirstOrDefault(z => z.Name.Equals(zoneIdentifier, StringComparison.OrdinalIgnoreCase));
            }

            if (selectedZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Zone '{zoneIdentifier}' not found!{ChatColors.Default}");

                // Show available zones
                if (_zoneManager?.Zones?.Count > 0)
                {
                    var zoneList = string.Join(", ", _zoneManager.Zones.Select(z => $"{z.Id}:{z.Name}"));
                    commandInfo.ReplyToCommand($"{ChatColors.Yellow}Available zones: {zoneList}{ChatColors.Default}");
                }
                return;
            }

            // Clear current zone
            _zoneVisualization?.ClearZoneVisualization();

            // Set new active zone
            activeZone = selectedZone;
            activeZone.PlayersInZone.Clear();

            // Reset capture times
            _ctZoneTime = 0f;
            _tZoneTime = 0f;
            _zoneEmptyTime = 0f;
            _zoneWasOccupied = false;

            // Draw the selected zone
            _zoneVisualization?.DrawZone(activeZone);

            // Announce the zone change
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - Zone '{activeZone.Name}' (ID: {activeZone.Id}) selected by admin!");
            commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{activeZone.Name}' (ID: {activeZone.Id}) is now active!{ChatColors.Default}");

            Server.PrintToConsole($"[Lockpoint] Admin selected zone: {activeZone.Name} (ID: {activeZone.Id})");
        }

        [ConsoleCommand("css_clearzone", "Clears the active zone.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandClearZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            activeZone = null;
            _zoneVisualization?.ClearZoneVisualization();
            commandInfo.ReplyToCommand("Cleared active zone - no zones are now active");
            Server.PrintToConsole("[Lockpoint] Cleared active zone");
        }

        [ConsoleCommand("css_ready", "Mark yourself as ready to start the game.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnCommandReady(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid)
                return;

            if (_gamePhase != GamePhase.Warmup)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Game is already in progress!{ChatColors.Default}");
                return;
            }

            if (_readyPlayers.Contains(player))
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}You are already ready!{ChatColors.Default}");
                return;
            }

            _readyPlayers.Add(player);
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.LightBlue}{player.PlayerName}{ChatColors.Default} is ready!");

            CheckReadyStatus();
        }

        [ConsoleCommand("css_unready", "Mark yourself as not ready.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnCommandUnready(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid)
                return;

            if (_gamePhase != GamePhase.Warmup)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Game is already in progress!{ChatColors.Default}");
                return;
            }

            if (_readyPlayers.Remove(player))
            {
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}{player.PlayerName}{ChatColors.Default} is no longer ready!");
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}You are no longer ready.{ChatColors.Default}");
            }
            else
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You were not ready!{ChatColors.Default}");
            }
        }

        [ConsoleCommand("css_start", "Start the Lockpoint game (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandStartGame(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_gamePhase == GamePhase.Active)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}Game is already active!{ChatColors.Default}");
                return;
            }

            // Check if we have zones available
            if (_zoneManager?.Zones == null || _zoneManager.Zones.Count == 0)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zones available for this map! Use css_editmode to create zones.{ChatColors.Default}");
                return;
            }

            // Reset all game state
            _ctScore = 0;
            _tScore = 0;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;
            _zoneEmptyTime = 0f;
            _zoneWasOccupied = false;
            _previousZone = null;
            activeZone = null;

            // Set to active phase
            _gamePhase = GamePhase.Active;

            // Start the main game timer
            _LockpointTimer?.Start();
            _zoneCheckTimer?.Start();
            _hudTimer?.Start();
            // Respawn all players and wait 5 seconds for first zone
            RespawnAllPlayers();

            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - Game started! First zone in 5 seconds...");
            commandInfo.ReplyToCommand($"{ChatColors.Green}Lockpoint game started!{ChatColors.Default}");

            // Start zone change countdown with 5 second delay
            StartZoneChangeCountdown();
        }

        [ConsoleCommand("css_stop", "Stop the game and return to warmup (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandStop(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_gamePhase == GamePhase.Warmup && !_isCountingDown)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}Game is already in warmup phase!{ChatColors.Default}");
                return;
            }

            // Cancel countdown if it's running
            _isCountingDown = false;
            _countdownMessage = "";
            _countdownTimer?.Kill();
            _countdownTimer = null;
            _gameStartCountdown = 0;

            _gamePhase = GamePhase.Warmup;
            _readyPlayers.Clear();

            // Stop timers and clear zones
            _zoneCheckTimer?.Stop();
            _LockpointTimer?.Stop();
            _zoneVisualization?.ClearZoneVisualization();
            activeZone = null;

            // Reset scores
            _ctScore = 0;
            _tScore = 0;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;
            _previousZone = null;

            UpdateTeamScore(CsTeam.CounterTerrorist, 0);
            UpdateTeamScore(CsTeam.Terrorist, 0);

            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}Game stopped by admin! Back to warmup.{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Yellow}⏳ Type !ready to start the game{ChatColors.Default}");

            // Respawn all players for warmup phase
            Server.NextFrame(() =>
            {
                RespawnAllPlayers();

                // Restart HUD timer for warmup messages
                _LockpointTimer?.Start();
            });
        }

        [ConsoleCommand("css_readyconfig", "Configure ready system (Admin only). Usage: css_readyconfig <team|all>")]
        [CommandHelper(minArgs: 1, usage: "<team|all>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandReadyConfig(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var mode = commandInfo.GetArg(1).ToLower();

            if (mode == "team")
            {
                _requireTeamReady = true;
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - Ready mode set to: {ChatColors.Yellow}One player per team{ChatColors.Default}");
            }
            else if (mode == "all")
            {
                _requireTeamReady = false;
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - Ready mode set to: {ChatColors.Yellow}All players{ChatColors.Default}");
            }
            else
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Usage: css_readyconfig <team|all>{ChatColors.Default}");
            }
        }

        [ConsoleCommand("css_kill", "Kill yourself or another player (admin).")]
        [CommandHelper(minArgs: 0, usage: "[player_name]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnCommandKill(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var targetName = commandInfo.GetArg(1);

            // If no target specified, kill yourself (client only)
            if (string.IsNullOrEmpty(targetName))
            {
                if (player?.IsValid != true)
                {
                    commandInfo.ReplyToCommand("Cannot kill yourself from server console. Specify a player name.");
                    return;
                }

                if (!player.PawnIsAlive)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}You are already dead!{ChatColors.Default}");
                    return;
                }

                try
                {
                    player.PlayerPawn?.Value?.CommitSuicide(false, true);
                    Server.PrintToChatAll($"{ChatColors.Yellow}{player.PlayerName}{ChatColors.Default} committed suicide");
                    Server.PrintToConsole($"[Lockpoint] {player.PlayerName} killed themselves");
                }
                catch (Exception ex)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}Error killing yourself: {ex.Message}{ChatColors.Default}");
                    Server.PrintToConsole($"[Lockpoint] Error in self-kill for {player.PlayerName}: {ex.Message}");
                }
                return;
            }

            // If target specified, admin kill (requires permissions)
            if (player?.IsValid == true && !AdminManager.PlayerHasPermissions(player, "@css/slay"))
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You don't have permission to kill other players!{ChatColors.Default}");
                return;
            }

            // Find the target player
            var targetPlayer = FindPlayerByName(targetName);

            if (targetPlayer == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Player '{targetName}' not found!{ChatColors.Default}");
                return;
            }

            if (!targetPlayer.PawnIsAlive)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}{targetPlayer.PlayerName} is already dead!{ChatColors.Default}");
                return;
            }

            try
            {
                targetPlayer.PlayerPawn?.Value?.CommitSuicide(false, true);

                var killerName = player?.IsValid == true ? player.PlayerName : "Server";
                Server.PrintToChatAll($"{ChatColors.Red}{targetPlayer.PlayerName}{ChatColors.Default} was killed by {ChatColors.Yellow}{killerName}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] {killerName} killed {targetPlayer.PlayerName}");

                commandInfo.ReplyToCommand($"{ChatColors.Green}Killed {targetPlayer.PlayerName}!{ChatColors.Default}");
            }
            catch (Exception ex)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Error killing {targetPlayer.PlayerName}: {ex.Message}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Error killing {targetPlayer.PlayerName}: {ex.Message}");
            }
        }

        private CCSPlayerController? FindPlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var players = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true &&
                        p.Connected == PlayerConnectedState.PlayerConnected &&
                        !p.IsBot)
                .ToList();

            // First try exact match
            var exactMatch = players.FirstOrDefault(p =>
                string.Equals(p.PlayerName, name, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
                return exactMatch;

            // Then try partial match
            var partialMatch = players.FirstOrDefault(p =>
                p.PlayerName.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (partialMatch != null)
                return partialMatch;

            // Try by user ID if it's a number
            if (int.TryParse(name, out int userId))
            {
                var userIdMatch = players.FirstOrDefault(p => p.UserId == userId);
                if (userIdMatch != null)
                    return userIdMatch;
            }

            return null;
        }

        [ConsoleCommand("css_addtspawn", "Add a terrorist spawn point to the active zone (Edit mode only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddTSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to add spawn points! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var closestZone = FindClosestZone(player);
            if (closestZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone found nearby. Stand closer to a zone.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            var playerAngle = new QAngle(
                player.PlayerPawn.Value.EyeAngles.X,
                player.PlayerPawn.Value.EyeAngles.Y,
                player.PlayerPawn.Value.EyeAngles.Z
            );
            var spawnPoint = new Zone.SpawnPoint(playerPos, playerAngle);
            closestZone.TerroristSpawns.Add(spawnPoint);

            // Save zones
            var currentMapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(currentMapName, _zoneManager.Zones);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Added terrorist spawn to zone '{closestZone.Name}' ({closestZone.TerroristSpawns.Count} T spawns total){ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Added T spawn to zone '{closestZone.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_addctspawn", "Add a counter-terrorist spawn point to the active zone (Edit mode only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddCTSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to add spawn points! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var closestZone = FindClosestZone(player);
            if (closestZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone found nearby. Stand closer to a zone.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            var playerAngle = new QAngle(
                player.PlayerPawn.Value.EyeAngles.X,
                player.PlayerPawn.Value.EyeAngles.Y,
                player.PlayerPawn.Value.EyeAngles.Z
            );
            var spawnPoint = new Zone.SpawnPoint(playerPos, playerAngle);
            closestZone.CounterTerroristSpawns.Add(spawnPoint);

            // Save zones
            var currentMapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(currentMapName, _zoneManager.Zones);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Added CT spawn to zone '{closestZone.Name}' ({closestZone.CounterTerroristSpawns.Count} CT spawns total){ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Added CT spawn to zone '{closestZone.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_clearspawns", "Clear all spawn points from the closest zone (Edit mode only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandClearSpawns(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to clear spawn points! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var closestZone = FindClosestZone(player);
            if (closestZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone found nearby. Stand closer to a zone.{ChatColors.Default}");
                return;
            }

            var tSpawnCount = closestZone.TerroristSpawns.Count;
            var ctSpawnCount = closestZone.CounterTerroristSpawns.Count;

            closestZone.TerroristSpawns.Clear();
            closestZone.CounterTerroristSpawns.Clear();

            // Save zones
            var currentMapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(currentMapName, _zoneManager.Zones);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Cleared all spawn points from zone '{closestZone.Name}' ({tSpawnCount} T + {ctSpawnCount} CT spawns removed){ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Cleared spawns from zone '{closestZone.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_testspawn", "Test spawn points for current zone (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandTestSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player?.IsValid != true)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (activeZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No active zone to test spawns for.{ChatColors.Default}");
                return;
            }

            var teamNum = player.TeamNum;
            var teamSpawns = teamNum == (byte)CsTeam.CounterTerrorist
                ? activeZone.CounterTerroristSpawns
                : activeZone.TerroristSpawns;

            if (teamSpawns.Count == 0)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No spawn points defined for your team in zone '{activeZone.Name}'.{ChatColors.Default}");
                return;
            }

            var availableSpawns = FindAvailableSpawnPointsWithAngles(teamSpawns);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{activeZone.Name}' - Team spawns: {teamSpawns.Count}, Available: {availableSpawns.Count}{ChatColors.Default}");

            if (availableSpawns.Count > 0)
            {
                // Teleport to a random available spawn for testing
                var random = new Random();
                var testSpawn = availableSpawns[random.Next(availableSpawns.Count)];
                var spawnVector = new Vector(testSpawn.Position.X, testSpawn.Position.Y, testSpawn.Position.Z);

                player.PlayerPawn?.Value?.Teleport(spawnVector, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                commandInfo.ReplyToCommand($"{ChatColors.Blue}Teleported to available spawn point.{ChatColors.Default}");
            }
            else
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}All spawn points are occupied, would use default spawns.{ChatColors.Default}");
            }
        }

        [ConsoleCommand("css_edit", "Enter/exit edit mode (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/root")]
        public void OnCommandEdit(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_editMode)
            {
                // Exit edit mode
                ExitEditMode(player);
            }
            else
            {
                // Enter edit mode
                EnterEditMode(player);
            }
        }

        private void EnterEditMode(CCSPlayerController? player)
        {
            try
            {
                // Store previous state
                _previousGamePhase = _gamePhase;
                _editMode = true;
                _gamePhase = GamePhase.EditMode;

                // Stop all game timers
                _zoneCheckTimer?.Stop();
                _LockpointTimer?.Stop();

                // Clear all respawn timers
                CleanupAllRespawnTimers();
                _zoneVisualization?.SetEditMode(true);


                // Enable cheats
                Server.ExecuteCommand("sv_cheats 1");
                _previousCheatsEnabled = false; // Assume cheats were off before

                var editorName = player?.IsValid == true ? player.PlayerName : "Server";
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}🔧 EDIT MODE ACTIVATED by {editorName}!{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.LightBlue}Use noclip to fly around and create/edit zones{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Red}Game is paused during edit mode{ChatColors.Default}");

                // Respawn all players
                Server.NextFrame(() =>
                {
                    RespawnAllPlayersForEdit();

                    // Draw all zones for visibility
                    AddTimer(1.0f, () =>
                    {
                        DrawAllZonesForEdit();
                        _LockpointTimer?.Start(); // Start timer for HUD updates only
                    });
                });

                Server.PrintToConsole($"[Lockpoint] Edit mode activated by {editorName}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error entering edit mode: {ex.Message}");
            }
        }

        private void ExitEditMode(CCSPlayerController? player)
        {
            try
            {
                _editMode = false;
                _gamePhase = _previousGamePhase;
                _zoneBeingEdited = null;
                _isEditingExistingZone = false;

                _zoneVisualization?.ClearEditMode();
                _zoneVisualization?.ClearZoneVisualization();

                // Disable cheats (restore previous state)
                if (!_previousCheatsEnabled)
                {
                    Server.ExecuteCommand("sv_cheats 0");
                }

                var editorName = player?.IsValid == true ? player.PlayerName : "Server";
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}🔧 EDIT MODE DEACTIVATED by {editorName}!{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Green}Game resumed{ChatColors.Default}");

                // Clear all zone visualizations
                _zoneVisualization?.ClearZoneVisualization();

                // Respawn all players back to normal mode
                Server.NextFrame(() =>
                {
                    RespawnAllPlayers();

                    // Restart game timers based on previous phase
                    AddTimer(1.0f, () =>
                    {
                        if (_gamePhase == GamePhase.Active)
                        {
                            StartZoneChangeCountdown();
                        }
                        _LockpointTimer?.Start();
                    });
                });

                Server.PrintToConsole($"[Lockpoint] Edit mode deactivated by {editorName}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error exiting edit mode: {ex.Message}");
            }
        }

        private void RespawnAllPlayersForEdit()
        {
            Server.PrintToConsole("[Lockpoint] Respawning all players for edit mode...");

            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true &&
                    player.Connected == PlayerConnectedState.PlayerConnected &&
                    !player.IsBot &&
                    player.TeamNum != (byte)CsTeam.None &&
                    player.TeamNum != (byte)CsTeam.Spectator)
                {
                    try
                    {
                        // Force respawn everyone
                        player.Respawn();

                        // Give noclip after respawn
                        AddTimer(0.3f, () =>
                        {
                            if (player?.IsValid == true && player.PawnIsAlive)
                            {
                                Server.ExecuteCommand($"noclip {player.UserId}");
                                player.PrintToChat($"{ChatColors.Green}Edit mode: Use noclip to fly around!{ChatColors.Default}");
                            }
                        });

                        Server.PrintToConsole($"[Lockpoint] Respawned player for edit: {player.PlayerName}");
                    }
                    catch (Exception ex)
                    {
                        Server.PrintToConsole($"[Lockpoint] Error respawning player for edit {player.PlayerName}: {ex.Message}");
                    }
                }
            }
        }

        private void DrawAllZonesForEdit()
        {
            try
            {
                if (_zoneManager?.Zones == null)
                {
                    Server.PrintToConsole("[Lockpoint] No zones to draw for edit mode");
                    return;
                }

                _zoneVisualization?.ClearZoneVisualization();

                foreach (var zone in _zoneManager.Zones)
                {
                    _zoneVisualization?.DrawZone(zone);
                }

                Server.PrintToConsole($"[Lockpoint] Drew {_zoneManager.Zones.Count} zones for edit mode");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error drawing zones for edit: {ex.Message}");
            }
        }

        [ConsoleCommand("css_forcerestart", "Force restart round with Lockpoint settings (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/root")]
        public void OnCommandForceRestart(CCSPlayerController? player, CommandInfo commandInfo)
        {
            AddTimer(0.1f, () =>
            {
                Server.ExecuteCommand("mp_restartgame 1");
            });

            var message = "Round restarted with Lockpoint settings!";
            if (player?.IsValid == true)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Green}{message}{ChatColors.Default}");
            }
            else
            {
                Server.PrintToConsole($"[Lockpoint] {message}");
            }
        }

        [ConsoleCommand("css_help", "Show all available Lockpoint commands.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnCommandHelp(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var isAdmin = player?.IsValid == true && AdminManager.PlayerHasPermissions(player, "@css/root");

            if (player?.IsValid == true)
            {
                // Send to player via chat
                player.PrintToChat($"{ChatColors.Green}[Lockpoint Commands]{ChatColors.Default}");
                player.PrintToChat($"{ChatColors.Yellow}=== Player Commands ==={ChatColors.Default}");
                player.PrintToChat($"{ChatColors.LightBlue}!ready{ChatColors.Default} - Mark yourself as ready to start");
                player.PrintToChat($"{ChatColors.LightBlue}!unready{ChatColors.Default} - Mark yourself as not ready");
                player.PrintToChat($"{ChatColors.LightBlue}!kill{ChatColors.Default} - Kill yourself");
                player.PrintToChat($"{ChatColors.LightBlue}!suicide{ChatColors.Default} - Kill yourself");
                player.PrintToChat($"{ChatColors.LightBlue}!help{ChatColors.Default} - Show this help menu");

                if (isAdmin)
                {
                    player.PrintToChat($"{ChatColors.Red}=== Admin Commands ==={ChatColors.Default}");
                    player.PrintToChat($"{ChatColors.Orange}!start{ChatColors.Default} - Force start the game");
                    player.PrintToChat($"{ChatColors.Orange}!stop{ChatColors.Default} - Stop the game and return to warmup");
                    player.PrintToChat($"{ChatColors.Orange}!edit{ChatColors.Default} - Enter/exit edit mode");
                    player.PrintToChat($"{ChatColors.Orange}!readyconfig <team|all>{ChatColors.Default} - Configure ready system");
                    player.PrintToChat($"{ChatColors.Orange}!kill <player>{ChatColors.Default} - Kill a specific player");
                    player.PrintToChat($"{ChatColors.Orange}!slay <player>{ChatColors.Default} - Kill a specific player");
                    player.PrintToChat($"{ChatColors.Orange}!configlockpoint{ChatColors.Default} - Configure server for Lockpoint");

                    player.PrintToChat($"{ChatColors.Purple}=== Zone Commands (Edit Mode Only) ==={ChatColors.Default}");
                    player.PrintToChat($"{ChatColors.Magenta}!addzone <name>{ChatColors.Default} - Start creating a new zone");
                    player.PrintToChat($"{ChatColors.Magenta}!addpoint{ChatColors.Default} - Add a point to current zone");
                    player.PrintToChat($"{ChatColors.Magenta}!lastpoint{ChatColors.Default} - Add final point and finish zone area");
                    player.PrintToChat($"{ChatColors.Magenta}!endzone{ChatColors.Default} - Save the current zone");
                    player.PrintToChat($"{ChatColors.Magenta}!editzone [name]{ChatColors.Default} - Edit existing zone (closest or by name)");
                    player.PrintToChat($"{ChatColors.Magenta}!removezone [name]{ChatColors.Default} - Remove zone (closest or by name)");
                    player.PrintToChat($"{ChatColors.Magenta}!cancelzone{ChatColors.Default} - Cancel current zone editing");

                    player.PrintToChat($"{ChatColors.DarkBlue}=== Spawn Commands (Edit Mode Only) ==={ChatColors.Default}");
                    player.PrintToChat($"{ChatColors.Blue}!ct|!t{ChatColors.Default} - Add spawn point to current zone for the chosen team.");
                    player.PrintToChat($"{ChatColors.Blue}!removespawn{ChatColors.Default} - Remove closest spawn point");
                    player.PrintToChat($"{ChatColors.Blue}!clearspawns{ChatColors.Default} - Clear all spawns from closest zone");
                    player.PrintToChat($"{ChatColors.Blue}!testspawn{ChatColors.Default} - Test spawn points for current zone");
                    player.PrintToChat($"{ChatColors.Blue}!spawninfo{ChatColors.Default} - Show spawn info for current zone");
                }
                else
                {
                    player.PrintToChat($"{ChatColors.Grey}Admin commands hidden - you need @css/root permission{ChatColors.Default}");
                }
            }
            else
            {
                // Send to server console
                Server.PrintToConsole("[Lockpoint Commands]");
                Server.PrintToConsole("=== Player Commands ===");
                Server.PrintToConsole("css_ready - Mark yourself as ready to start");
                Server.PrintToConsole("css_unready - Mark yourself as not ready");
                Server.PrintToConsole("css_kill - Kill yourself");
                Server.PrintToConsole("css_suicide - Kill yourself");
                Server.PrintToConsole("css_help - Show this help menu");

                Server.PrintToConsole("=== Admin Commands ===");
                Server.PrintToConsole("css_start - Force start the game");
                Server.PrintToConsole("css_stop - Stop the game and return to warmup");
                Server.PrintToConsole("css_edit - Enter/exit edit mode");
                Server.PrintToConsole("css_readyconfig <team|all> - Configure ready system");
                Server.PrintToConsole("css_kill <player> - Kill a specific player");
                Server.PrintToConsole("css_slay <player> - Kill a specific player");
                Server.PrintToConsole("css_configlockpoint - Configure server for Lockpoint");

                Server.PrintToConsole("=== Zone Commands (Edit Mode Only) ===");
                Server.PrintToConsole("css_addzone <name> - Start creating a new zone");
                Server.PrintToConsole("css_addpoint - Add a point to current zone");
                Server.PrintToConsole("css_savezone - Save the current zone");
                Server.PrintToConsole("css_editzone [name] - Edit existing zone (closest or by name)");
                Server.PrintToConsole("css_removezone [name] - Remove zone (closest or by name)");
                Server.PrintToConsole("css_cancelzone - Cancel current zone editing");

                Server.PrintToConsole("=== Spawn Commands (Edit Mode Only) ===");
                Server.PrintToConsole("css_addspawn <ct|t> - Add spawn point to current zone");
                Server.PrintToConsole("css_removespawn - Remove closest spawn point");
                Server.PrintToConsole("css_clearspawns - Clear all spawns from closest zone");
                Server.PrintToConsole("css_testspawn - Test spawn points for current zone");
                Server.PrintToConsole("css_spawninfo - Show spawn info for current zone");
            }
        }

        [ConsoleCommand("css_commands", "Alias for help command.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnCommandCommands(CCSPlayerController? player, CommandInfo commandInfo)
        {
            OnCommandHelp(player, commandInfo);
        }

        [ConsoleCommand("css_quickhelp", "Show context-sensitive help.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnCommandQuickHelp(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player?.IsValid != true) return;

            var isAdmin = AdminManager.PlayerHasPermissions(player, "@css/root");

            if (_gamePhase == GamePhase.Warmup)
            {
                player.PrintToChat($"{ChatColors.Green}[Warmup Phase]{ChatColors.Default} Use {ChatColors.Yellow}!ready{ChatColors.Default} to start the game");
                if (isAdmin)
                {
                    player.PrintToChat($"Admin: {ChatColors.Orange}!start{ChatColors.Default} to force start, {ChatColors.Orange}!edit{ChatColors.Default} for zone editing");
                }
            }
            else if (_gamePhase == GamePhase.EditMode)
            {
                if (isAdmin)
                {
                    if (_zoneBeingEdited != null)
                    {
                        player.PrintToChat($"{ChatColors.Purple}[Editing Zone]{ChatColors.Default} {ChatColors.Magenta}!addspawn <ct|t>{ChatColors.Default} or {ChatColors.Magenta}!endzone{ChatColors.Default} to save");
                    }
                    else
                    {
                        player.PrintToChat($"{ChatColors.Purple}[Edit Mode]{ChatColors.Default} {ChatColors.Magenta}!addzone <name>{ChatColors.Default} or {ChatColors.Magenta}!editzone{ChatColors.Default}");
                    }
                }
                else
                {
                    player.PrintToChat($"{ChatColors.Purple}[Edit Mode]{ChatColors.Default} Admin is editing zones");
                }
            }
            else if (_gamePhase == GamePhase.Active)
            {
                if (activeZone != null)
                {
                    player.PrintToChat($"{ChatColors.Green}[Active Game]{ChatColors.Default} Capture zone: {ChatColors.Yellow}{activeZone.Name}{ChatColors.Default}");
                }
                if (isAdmin)
                {
                    player.PrintToChat($"Admin: {ChatColors.Orange}!stop{ChatColors.Default} to end game");
                }
            }

            player.PrintToChat($"Use {ChatColors.LightBlue}!help{ChatColors.Default} for all commands");
        }

        [ConsoleCommand("css_capturetime", "Set zone capture time in seconds (Admin only).")]
        [CommandHelper(minArgs: 1, usage: "<seconds>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/root")]
        public void OnCommandCaptureTime(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!float.TryParse(commandInfo.GetArg(1), out float seconds) || seconds <= 0)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Invalid time! Use a positive number (e.g., 20.5){ChatColors.Default}");
                return;
            }

            _captureTime = seconds;
            
            var message = $"Zone capture time set to {_captureTime:F1} seconds";
            commandInfo.ReplyToCommand($"{ChatColors.Green}{message}{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}{message}{ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Capture time changed to {_captureTime:F1}s by {(player?.PlayerName ?? "Server")}");
        }

        [ConsoleCommand("css_respawntime", "Set respawn delay in seconds (Admin only).")]
        [CommandHelper(minArgs: 1, usage: "<seconds>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/root")]
        public void OnCommandRespawnTime(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!float.TryParse(commandInfo.GetArg(1), out float seconds) || seconds < 0)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Invalid time! Use 0 or higher (e.g., 5.0, use 0 for instant){ChatColors.Default}");
                return;
            }

            _respawnDelay = seconds;
            
            var message = _respawnDelay == 0 
                ? "Respawn set to instant"
                : $"Respawn delay set to {_respawnDelay:F1} seconds";
            
            commandInfo.ReplyToCommand($"{ChatColors.Green}{message}{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}{message}{ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Respawn time changed to {_respawnDelay:F1}s by {(player?.PlayerName ?? "Server")}");
        }

        [ConsoleCommand("css_zonedelay", "Set delay before next zone appears in seconds (Admin only).")]
        [CommandHelper(minArgs: 1, usage: "<seconds>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/root")]
        public void OnCommandZoneDelay(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!double.TryParse(commandInfo.GetArg(1), out double seconds) || seconds < 0)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Invalid time! Use 0 or higher (e.g., 5.0, use 0 for instant){ChatColors.Default}");
                return;
            }

            _newZoneDelay = seconds;
            
            var message = _newZoneDelay == 0 
                ? "New zone will appear instantly after capture"
                : $"New zone delay set to {_newZoneDelay:F1} seconds";
            
            commandInfo.ReplyToCommand($"{ChatColors.Green}{message}{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}{message}{ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Zone delay changed to {_newZoneDelay:F1}s by {(player?.PlayerName ?? "Server")}");
        }

        [ConsoleCommand("css_gamesettings", "Show current game timing settings.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnCommandGameSettings(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var settings = new[]
            {
                $"Zone Capture Time: {_captureTime:F1} seconds",
                $"Respawn Delay: {(_respawnDelay == 0 ? "Instant" : $"{_respawnDelay:F1} seconds")}",
                $"New Zone Delay: {(_newZoneDelay == 0 ? "Instant" : $"{_newZoneDelay:F1} seconds")}",
                $"Winning Score: {WINNING_SCORE} captures",
                $"Zone Detection Buffer: {ZONE_DETECTION_BUFFER:F1} units"
            };

            if (player?.IsValid == true)
            {
                player.PrintToChat($"{ChatColors.Green}[Lockpoint Game Settings]{ChatColors.Default}");
                foreach (var setting in settings)
                {
                    player.PrintToChat($"{ChatColors.Yellow}• {setting}{ChatColors.Default}");
                }
                
                if (AdminManager.PlayerHasPermissions(player, "@css/root"))
                {
                    player.PrintToChat($"{ChatColors.Orange}Use !capturetime, !respawntime, !zonedelay to adjust{ChatColors.Default}");
                }
            }
            else
            {
                Server.PrintToConsole("[Lockpoint Game Settings]");
                foreach (var setting in settings)
                {
                    Server.PrintToConsole($"• {setting}");
                }
            }
        }

        [ConsoleCommand("css_quickconfig", "Quick preset configurations (Admin only).")]
        [CommandHelper(minArgs: 1, usage: "<fast|normal|slow|instant>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/root")]
        public void OnCommandQuickConfig(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var preset = commandInfo.GetArg(1).ToLower();
            
            switch (preset)
            {
                case "fast":
                    _captureTime = 10f;
                    _respawnDelay = 2f;
                    _newZoneDelay = 2.0;
                    break;
                    
                case "normal":
                    _captureTime = 20f;
                    _respawnDelay = 5f;
                    _newZoneDelay = 5.0;
                    break;
                    
                case "slow":
                    _captureTime = 30f;
                    _respawnDelay = 8f;
                    _newZoneDelay = 8.0;
                    break;
                    
                case "instant":
                    _captureTime = 15f;
                    _respawnDelay = 0f;
                    _newZoneDelay = 0.0;
                    break;
                    
                default:
                    commandInfo.ReplyToCommand($"{ChatColors.Red}Invalid preset! Use: fast, normal, slow, or instant{ChatColors.Default}");
                    return;
            }
            
            var message = $"Applied '{preset}' preset: Capture={_captureTime:F0}s, Respawn={(_respawnDelay == 0 ? "instant" : $"{_respawnDelay:F0}s")}, Zone Delay={(_newZoneDelay == 0 ? "instant" : $"{_newZoneDelay:F0}s")}";
            
            commandInfo.ReplyToCommand($"{ChatColors.Green}{message}{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}{message}{ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Applied {preset} preset by {(player?.PlayerName ?? "Server")}.");
        }
        #endregion
    }
}
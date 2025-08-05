# Lockpoint Plugin for CS2

## Overview

Lockpoint is a comprehensive zone-based game mode plugin for Counter-Strike 2 using CounterStrikeSharp. Players compete to capture and hold designated zones on the map, with the first team to reach the winning score claiming victory. The plugin features dynamic zone selection, team-based spawning, and a complete zone editor for server administrators.

## Features

### Core Gameplay

- **Zone-based Combat**: Teams fight to capture and hold randomly selected zones
- **Dynamic Zone Selection**: New zones activate automatically after captures with a 5-second delay
- **Team-based Scoring**: First team to reach the winning score (default: 5) wins the match
- **Smart Spawning**: Players spawn at team-specific locations near active zones
- **Real-time HUD**: Live capture progress, zone status, and team scores
- **Instant Respawn**: 5-second respawn timer keeps the action flowing

### Zone Management

- **Visual Zone Editor**: Create and modify zones with in-game visual feedback
- **Polygon-based Zones**: Support for complex zone shapes with multiple points
- **Spawn Point Editor**: Set team-specific spawn locations for each zone
- **Persistent Storage**: Zone configurations saved in JSON format per map
- **Zone Validation**: Automatic checking for valid zone configurations

### Administrative Features

- **Ready System**: Optional team-ready or all-player-ready game start
- **Edit Mode**: Pause gameplay to modify zones without interference
- **Configuration System**: Automated server configuration via dedicated config file
- **Permission System**: Admin-only commands with proper permission checks

## Installation

1. Install [CounterStrikeSharp](https://docs.cssharp.dev/docs/guides/getting-started/)
2. Download the Lockpoint plugin files
3. Place the plugin in your `addons/counterstrikesharp/plugins/` directory
4. Restart your server or load the plugin with `css_plugins load Lockpoint`

## Commands

### Player Commands

| Command    | Description                              | Usage      |
| ---------- | ---------------------------------------- | ---------- |
| `!ready`   | Mark yourself as ready to start the game | `!ready`   |
| `!unready` | Remove your ready status                 | `!unready` |

### Administrative Commands

| Command                       | Permission  | Description                        | Usage                         |
| ----------------------------- | ----------- | ---------------------------------- | ----------------------------- |
| `css_start`                   | `@css/root` | Force start the game               | `css_start`                   |
| `css_stop`                    | `@css/root` | Stop the game and return to warmup | `css_stop`                    |
| `css_lockpoint_reload_config` | `@css/root` | Reload server configuration        | `css_lockpoint_reload_config` |

### Zone Management Commands

| Command          | Permission  | Description                        | Usage                        |
| ---------------- | ----------- | ---------------------------------- | ---------------------------- |
| `css_addzone`    | `@css/root` | Create a new zone                  | `css_addzone <zone_name>`    |
| `css_editzone`   | `@css/root` | Edit an existing zone              | `css_editzone <zone_name>`   |
| `css_savezone`   | `@css/root` | Save the current zone being edited | `css_savezone`               |
| `css_cancelzone` | `@css/root` | Cancel zone editing                | `css_cancelzone`             |
| `css_deletezone` | `@css/root` | Delete a zone                      | `css_deletezone <zone_name>` |
| `css_listzones`  | `@css/root` | List all zones for current map     | `css_listzones`              |

### Zone Point Management Commands

| Command           | Permission  | Description                             | Usage             |
| ----------------- | ----------- | --------------------------------------- | ----------------- |
| `css_addpoint`    | `@css/root` | Add a point to the current zone         | `css_addpoint`    |
| `css_removepoint` | `@css/root` | Remove the last point from current zone | `css_removepoint` |
| `css_clearpoints` | `@css/root` | Clear all points from current zone      | `css_clearpoints` |

### Spawn Point Management Commands

| Command           | Permission  | Description                        | Usage                    |
| ----------------- | ----------- | ---------------------------------- | ------------------------ |
| `css_addspawn`    | `@css/root` | Add spawn point for specified team | `css_addspawn <ct/t>`    |
| `css_removespawn` | `@css/root` | Remove last spawn point for team   | `css_removespawn <ct/t>` |
| `css_clearspawns` | `@css/root` | Clear all spawn points for team    | `css_clearspawns <ct/t>` |

### Edit Mode Commands

| Command        | Permission  | Description                   | Usage          |
| -------------- | ----------- | ----------------------------- | -------------- |
| `css_editmode` | `@css/root` | Enter edit mode (pauses game) | `css_editmode` |
| `css_exitedit` | `@css/root` | Exit edit mode (resumes game) | `css_exitedit` |

## Game Flow

### 1. Warmup Phase

- Players join teams and type `!ready` when prepared
- Game starts when all players are ready (or team representatives if team-ready mode enabled)
- Minimum 2 players required to start

### 2. Active Game Phase

- Random zone is selected and highlighted
- Teams fight to capture the zone by having players inside without enemy presence
- Capture progress shown in real-time HUD
- Zone captured when one team reaches 100% progress (default: 30 seconds)

### 3. Zone Capture

- Capturing team scores a point
- Zone is cleared and 5-second countdown begins
- New zone automatically selected after countdown
- Game continues until one team reaches winning score

### 4. Game End

- Winning team announced
- Scores reset after 10 seconds
- Returns to warmup phase for next game

## Zone Creation Guide

### Basic Zone Creation

1. **Enter Edit Mode**: `css_editmode`
2. **Create Zone**: `css_addzone "Zone Name"`
3. **Add Points**: Stand at desired corners and use `css_addpoint`
4. **Add Spawns**: Position yourself and use `css_addspawn ct` or `css_addspawn t`
5. **Save Zone**: `css_savezone`
6. **Exit Edit Mode**: `css_exitedit`

### Zone Requirements

- **Minimum 3 points** for valid polygon shape
- **At least 1 spawn point** per team per zone
- **Logical positioning** of spawns relative to zone location

### Advanced Zone Editing

- **Edit Existing**: `css_editzone "Zone Name"` to modify saved zones
- **Remove Points**: `css_removepoint` to undo last point
- **Clear All Points**: `css_clearpoints` to start over
- **Manage Spawns**: Use remove/clear commands for spawn point management

## Configuration

The plugin automatically creates a configuration file at:
addons/counterstrikesharp/plugins/Lockpoint/
├── Lockpoint.dll # Main plugin file
├── cfg/lockpoint.cfg # Server configuration
└── zones/ # Zone data directory
├── de_dust2.json # Zone data for de_dust2
├── de_mirage.json # Zone data for de_mirage
└── ... # Additional map zone files

## Troubleshooting

### Common Issues

**No zones available**

- Create zones using the zone management commands
- Ensure zones have minimum 3 points and spawn points for both teams

**Players not spawning correctly**

- Verify spawn points are set for both teams in active zones
- Check that spawn points are positioned on walkable surfaces

**Game not starting**

- Ensure minimum 2 players are present
- Check that players have typed `!ready`
- Verify both teams have players if team-ready mode is enabled

**Configuration not applying**

- Check server console for configuration load messages
- Manually reload config with `css_lockpoint_reload_config`
- Verify write permissions for config directory

### Debug Information

Enable console logging to monitor plugin activity:

- Zone selection and activation messages
- Player spawn and respawn events
- Configuration loading status
- Error messages with detailed stack traces

## Support

For issues, feature requests, or contributions:

- Check server console for detailed error messages
- Ensure all required permissions are properly configured
- Verify CounterStrikeSharp is up to date and compatible

## License

This plugin is provided as-is for educational and server administration purposes. Modify and distribute according to your server's needs.

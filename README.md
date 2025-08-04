# HardpointCS2 Plugin

## Overview
HardpointCS2 is a plugin designed for managing capture zones in a game environment. It allows administrators to define zones through simple commands, visualize them in-game, and store their configurations for future use.

## Features
- Define capture zones using commands.
- Add multiple points to create complex zone shapes.
- Visual feedback for players on zone boundaries.
- Persistent storage of zone definitions in JSON format.

## Command Usage
1. **Add Zone**: 
   - Command: `/addzone ZONE_NAME`
   - Description: Initiates the creation of a new zone with the specified name.

2. **Add Point**: 
   - Command: `/addpoint`
   - Description: Adds the current player position as a point in the zone being defined.

3. **End Zone**: 
   - Command: `/endzone`
   - Description: Completes the zone definition and saves it to the zones.json file.

## Installation
1. Clone the repository to your local machine.
2. Open the project in your preferred .NET development environment.
3. Restore the project dependencies.
4. Build the project to ensure everything is set up correctly.

## Configuration
The zones are stored in `src/Data/zones.json`. You can manually edit this file to adjust zone properties if needed.

## Example
To create a new zone named "Base A":
1. Type `/addzone Base A`
2. Move to the desired point and type `/addpoint` to add it.
3. Repeat step 2 for additional points.
4. Type `/endzone` to finalize the zone.

## Contributing
Contributions are welcome! Please submit a pull request or open an issue for any enhancements or bug fixes.

## License
This project is licensed under the MIT License. See the LICENSE file for more details.
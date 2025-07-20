# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

**Building:**
- `make` - Build in Release mode
- `make check` - Build in Debug mode with code style checks
- `dotnet build` - Alternative build using .NET CLI

**Testing:**
- `make tests` - Run unit tests (NUnit)
- `make test` - Run YAML validation tests for mods
- `make check-scripts` - Validate Lua script syntax
- `./utility.sh all --check-yaml` - Comprehensive YAML validation

**Running:**
- `./launch-game.sh` (Linux/macOS) or `launch-game.cmd` (Windows)
- `./launch-game.sh Game.Mod=ra` - Launch specific mod (ra, cnc, d2k, ts)

**Code Quality:**
- `make check` - Run all code style checks and analyzers
- Code style is enforced via StyleCop.Analyzers and Roslynator.Analyzers
- Configuration in `.editorconfig` (tab indentation, 160 char line limit)

## Architecture Overview

OpenRA uses a modular, data-driven architecture built around an Actor-Component system:

### Core Components

1. **OpenRA.Game** - Core engine providing:
   - Actor system (entities in the game world)
   - World management and game loop
   - Graphics/rendering pipeline
   - Network/multiplayer infrastructure
   - Mod loading and ruleset handling

2. **OpenRA.Mods.Common** - Shared gameplay code:
   - Trait system (components that define actor behavior)
   - Common traits for units, buildings, weapons
   - UI widgets and controls
   - Lua scripting integration
   - Pathfinding algorithms

3. **Mod-specific assemblies** (OpenRA.Mods.Cnc, D2k, etc.):
   - Game-specific traits and logic
   - Custom units and mechanics

### Key Design Patterns

- **Actor-Trait System**: Game entities (actors) gain behavior through composable traits defined in YAML
- **Data-Driven Design**: Game rules, units, and maps defined in YAML files rather than code
- **Platform Abstraction**: OpenRA.Platforms.Default handles platform-specific code (graphics, audio, input)

### Adding New Features

When implementing new game logic:
1. Create traits in appropriate mod assembly (Common for shared, mod-specific otherwise)
2. Follow existing trait patterns - inherit from appropriate base classes
3. Define trait configuration in YAML rules files
4. Use activities for complex actor behaviors
5. Implement orders for player commands

### File Organization

- `mods/*/rules/` - YAML definitions for units, buildings, weapons
- `mods/*/maps/` - Map files and scripted missions
- `OpenRA.Mods.*/Traits/` - C# trait implementations
- `OpenRA.Mods.*/Activities/` - Actor behavior implementations
- `OpenRA.Mods.*/Widgets/` - UI components

## Development Guidelines

1. **Always run `make check` before commits** - ensures code style compliance
2. **Test on multiple platforms** - OpenRA supports Windows, Linux, macOS
3. **Maintain mod compatibility** - changes to Common affect all mods
4. **Follow YAML conventions** - use existing traits/properties where possible
5. **Branch from `bleed`** - main development branch for pull requests
# External Orders System for OpenRA

This system allows external programs (like the LLMHarness) to issue orders to the game by writing text files to a monitored directory.

## Configuration

The system is configured in the mod's `world.yaml` file:

```yaml
ExternalOrderReader:
    InputDirectory: C:\OpenRATest_Orders\input
    CheckInterval: 25        # Check every second (25 ticks)
    Enabled: true
    MaxOrdersPerTick: 10     # Process up to 10 orders per tick
    DeleteAfterProcessing: true
```

## Order File Format

Create text files in the input directory with orders in this format:

```
PlayerName: OrderType (Parameters)
```

Lines starting with `#` or `//` are treated as comments and ignored.

## Supported Orders

### Movement Orders

```
Player1: Move (Units:Tank,Infantry Target:50,75)
Player1: Move (Units:Tank#3 Target:100,100 Queued:true)
```

- `Units`: Comma-separated list of unit types or specific units (Tank#3 = 3rd tank)
- `Target`: Map coordinates (X,Y)
- `Queued`: Optional, true/false (default false)

### Attack Orders

```
Player1: Attack (Units:Tank,Tank Target:EnemyBuilding@100,200)
Player1: Attack (Units:Tank#1,Tank#2 Target:150,150)
```

- `Units`: Attacking units
- `Target`: Can be coordinates or ActorType@X,Y
- `Queued`: Optional

### Production Orders

```
Player1: StartProduction (Building:Barracks Item:Infantry Count:5)
Player1: StartProduction (Item:MediumTank Count:1)
Player1: CancelProduction (Item:Infantry Count:2)
```

- `Building`: Optional, specific building or "any"
- `Item`: Unit/building to produce
- `Count`: Number to produce
- `Queued`: Optional (default true for production)

### Unit Control

```
Player1: Stop (Units:Tank,Infantry)
Player1: Guard (Units:Infantry Target:Tank#1)
```

## Unit Type Names

Common unit names that can be used:

**Infantry:**
- Minigunner, Grenadier, RocketSoldier, Engineer, Commando

**Vehicles:**
- Humvee, APC, MediumTank, MammothTank, LightTank, FlameTank
- RocketLauncher, Artillery, Harvester, MCV

**Buildings:**
- Barracks, WarFactory, PowerPlant, Refinery, Silo
- GuardTower, Turret, SAMSite

## Examples

### Example 1: Simple Attack
```
# Attack enemy base with all tanks
Player1: Attack (Units:MediumTank,MediumTank,MediumTank Target:200,300)
```

### Example 2: Build Queue
```
# Start producing units
Player1: StartProduction (Building:Barracks Item:Infantry Count:3)
Player1: StartProduction (Building:WarFactory Item:MediumTank Count:2)
```

### Example 3: Coordinated Movement
```
# Move units to different positions
Player1: Move (Units:Harvester#1 Target:100,50)
Player1: Move (Units:Tank#1,Tank#2 Target:150,100)
Player1: Move (Units:Infantry Target:150,100 Queued:true)
```

## Testing

1. Create the input directory: `C:\OpenRATest_Orders\input\`
2. Start the game with CNC or RA mod
3. Create a text file (e.g., `order_001.txt`) in the input directory
4. Add orders to the file
5. Save the file - it will be processed and deleted automatically

## Integration with LLMHarness

The LLMHarness can write order files to this directory to control Player 1. The format is designed to be simple enough for LLMs to generate while being precise enough for game control.

## Debugging

Enable debug logging in the game to see order processing:
- Orders are logged when read from files
- Parsing errors are logged
- Successful order execution is logged

Check the game's debug log for messages like:
- "Read X lines from order file: filename.txt"
- "Issuing external order: OrderType"
- "Failed to parse/issue order 'order text': error message"

## Limitations

- Control groups (CreateGroup, SelectGroup) are not yet fully implemented
- Unit selection is limited - no current selection tracking
- Orders are issued as Player 1 by default (can be changed in order text)
- Some complex orders may require additional implementation

## Future Enhancements

- Support for control group management
- Better unit selection (by health, distance, etc.)
- Support for special abilities and superweapons
- Order validation before execution
- Response files with order execution status
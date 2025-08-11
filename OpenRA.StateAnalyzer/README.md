# OpenRA State Analyzer

A console application that analyzes OpenRA game state snapshots to infer actions taken between two states.

## Usage

```bash
dotnet run OpenRA.StateAnalyzer <state1.txt> <state2.txt>
```

Where `state1.txt` and `state2.txt` are game state files exported by the GameStateExporter trait (typically 10 seconds apart from the same game and same player's perspective).

## What it Analyzes

The analyzer compares two game states and infers:

### Economic Activity
- Cash changes (income vs spending)
- Resource harvesting
- Economic implications of changes

### Military Activity
- Combat engagements (units killed/lost)
- Building destruction
- Experience gains
- Army value changes

### Construction Activity
- New buildings constructed
- Building positions and base expansion
- Power grid changes

### Production Activity
- Units/buildings queued for production
- Completed production items
- Production queue status changes

### Special Units
- Harvester gains/losses
- MCV deployment/construction

### Enemy Intelligence
- Visible enemy structures destroyed
- New enemy structures spotted

## Output Format

The analyzer outputs a chronological list of inferred actions organized by player, making it easy to understand what happened during the analyzed time period.

## Example Output

```
Time period analyzed: 10.0 seconds (ticks 1250 to 1500)

=== Player1 (GDI) ===
ECONOMIC ACTIVITY:
  Cash changed by -$800 ($2000 → $1200)
  Spent $800 on purchases/construction
  → Making purchases (units/buildings) faster than income

CONSTRUCTION ACTIVITY:
  Constructed 1 Barracks (GDI)
  Built Barracks (GDI) at position (25, 30)

PRODUCTION ACTIVITY:
  Queued Minigunner x3 for production
```
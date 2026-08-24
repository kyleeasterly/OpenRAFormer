# Tiberian Dawn field manual (all numbers taken from the mod's own rules files)

## 1. The tech tree — what unlocks what

**Nothing else in this guide matters if you order units you cannot build.** Every
entry below carries a `Requires:` line. Read it before you queue anything.

The one fact that decides most matches: **the Communications Center ($1000) is the
gate to your real army.** It needs only a Tiberium Refinery. A Weapons Factory or
Airstrip on its own gives you no tanks at all.

### GDI chain

```
Construction Yard (start)
├─ Power Plant $500
│   ├─ Barracks $500 ──── Minigunner, Rocket Soldier, Engineer,
│   │                     Guard Tower, Turret, and (via Weapons Factory) APC
│   └─ Tiberium Refinery $1500  [free Harvester included]
│       ├─ Weapons Factory $2000 ── Hum-vee, APC*, Harvester, MCV, Supply Truck
│       │     └─ Repair Facility $500, Concrete Barrier $150
│       ├─ Helipad $1000 ── Chinook Transport
│       ├─ Tiberium Silo $100
│       └─ COMMUNICATIONS CENTER $1000
│             ├─ Medium Tank, Rocket Launcher, Grenadier
│             ├─ Orca (also needs Helipad)
│             ├─ Advanced Guard Tower $1000, Advanced Power Plant $800
│             └─ Advanced Communications Center $1800
│                   └─ Mammoth Tank, Commando
```
`*` APC needs the Barracks (for the prerequisite) **and** the Weapons Factory (to build it in).

### Nod chain

```
Construction Yard (start)
├─ Power Plant $500
│   ├─ Hand of Nod $500 ── Minigunner, Rocket Soldier, Engineer,
│   │                      Guard Tower, Turret, SAM Site $650
│   └─ Tiberium Refinery $1500  [free Harvester included]
│       ├─ Airstrip $2000 ── Nod Buggy, Recon Bike, Harvester, MCV, Supply Truck
│       │     └─ Repair Facility $500, Concrete Barrier $150
│       ├─ Helipad $1000 ── Chinook Transport
│       ├─ Tiberium Silo $100
│       └─ COMMUNICATIONS CENTER $1000
│             ├─ Light Tank, Flame Tank, Artillery, Mobile SAM, Flamethrower
│             ├─ Apache Longbow (also needs Helipad)
│             ├─ Advanced Power Plant $800
│             └─ Temple of Nod $2000
│                   └─ Stealth Tank, Chemical Warrior, Obelisk of Light $1500
```

Notes that trip people up:

- **Both factions can build the Guard Tower AND the Turret** in this mod; both need
  only a Barracks / Hand of Nod. The SAM Site is Nod-only; the Advanced Guard Tower
  is GDI-only.
- **The MCV ($3000) has no tech prerequisite** beyond owning a Weapons Factory or
  Airstrip to build it in. Expanding to a second tiberium field never requires a
  Communications Center.
- Every Tiberium Refinery arrives with a **free Harvester**. A second refinery is
  therefore worth far more than $1500 of anything else.
- If an item appears in neither `buildable` nor `locked`, the lobby tech level caps
  it out of the game. Stop planning around it.

## 2. The first five minutes — fighting before the Comms Center

Most losses happen here, with an unbuilt tech tree and an army of nothing.

**GDI can field, pre-Comms:** Minigunner $100, Rocket Soldier $300, Engineer $500,
APC $600, Hum-vee $400, Guard Tower $600, Turret $600, Chinook Transport $750.

**Nod can field, pre-Comms:** Minigunner $100, Rocket Soldier $300, Engineer $500,
Nod Buggy $300, Recon Bike $500, Guard Tower $600, Turret $600, SAM Site $650,
Chinook Transport $750. Nod has **no ground transport** — the APC is GDI-only.

How to fight with that:

- **The Rocket Soldier is the cheapest anti-armor in the game per dollar** — 1591
  DPS against tanks for $300, and 1318 against buildings. Its weaknesses are speed
  (39, the slowest unit in the game) and helplessness against infantry (318 DPS).
  Escort with Minigunners; move it in a transport when you can.
- **The APC drop is GDI's strongest early strike.** $600, speed 128, carries 5
  infantry. Load 3 Rocket Soldiers + 2 Minigunners, drive around the map edge,
  unload inside the enemy base, kill production buildings (Wood armor — a Barracks
  has 60000 HP and dies to three Rocket Soldiers in about 15 seconds). Even a failed
  drop trades ~$1500 of infantry for a $500-2000 structure.
- **Hum-vee / Buggy are scouts and infantry-killers only.** 719 and 625 DPS against
  buildings respectively — roughly 15x worse than against infantry. Never send them
  to kill a base.
- **Guard Tower + a few Rocket Soldiers holds a base** cheaply while you tech, which
  is what the Communications Center money is for.
- The Grenadier is **not** an early option — it needs the Communications Center.

## 3. How damage works: armor classes

Every weapon has a damage multiplier per armor class. Wrong unit vs wrong target
wastes 80-95% of its damage.

- **Infantry** (armor None): all foot soldiers.
- **Light**: wheeled/fast vehicles (Hum-vee, Nod Buggy, Recon Bike, Artillery,
  Rocket Launcher, Mobile SAM, Stealth Tank, Supply Truck, Mobile HQ), aircraft,
  sandbag/chain-link barriers.
- **Heavy**: tanks (Light/Medium/Mammoth/Flame), APC, Harvester, MCV.
- **Wood**: EVERY production building — Construction Yard, Power Plants, Refinery,
  Barracks/Hand of Nod, Weapons Factory/Airstrip, Comms Centers, Helipad, Repair
  Facility, Silo, Temple of Nod.
- **Concrete**: defense structures (Turret, Guard Towers, Obelisk, SAM Site) and
  concrete barriers.

DPS figures below are `damage x burst / reload x versus%`, straight from the rules.

## 4. Infantry

- **Minigunner (E1) $100** — Requires: Barracks / Hand of Nod. Both factions.
  1875 vs infantry, 500 vs light vehicles, 375 vs buildings, 125 vs tanks. 5000 HP.
  Cheap meat, scouting, and the escort that keeps Rocket Soldiers alive.
- **Rocket Soldier (E3) $300** — Requires: Barracks / Hand of Nod. Both factions.
  1591 vs tanks and light vehicles, 1318 vs buildings, only 318 vs infantry. 4500
  HP, speed 39, range 6. The cheapest anti-armor per dollar in the game.
- **Engineer (E6) $500** — Requires: Barracks / Hand of Nod. Both factions.
  No weapon. Captures an enemy building on entry. Capturing an enemy Construction
  Yard hands you that faction's build options and is usually better than killing it.
- **Grenadier (E2) $160** — Requires: **Communications Center**. GDI only.
  2500 vs infantry, 2000 vs light vehicles, 1250 vs buildings, 850 vs tanks.
  Cheap area damage once you are teched, not an opening unit.
- **Flamethrower (E4) $200** — Requires: **Communications Center**. Nod only.
  2000 vs infantry, 1818 vs buildings and light vehicles, 182 vs tanks. 9000 HP.
- **Chemical Warrior (E5) $300** — Requires: **Temple of Nod**. Nod only.
  2154 vs infantry, 2308 vs vehicles and defenses, 1077 vs buildings. 9000 HP.
- **Commando (RMBO) $1500** — Requires: **Advanced Communications Center**. GDI only.
  6250 vs infantry only (cannot shoot vehicles or structures), but the C4 charge
  destroys ANY building instantly on contact. 15000 HP. Sneaks; dies to vehicles.

## 5. Vehicles

**Pre-Comms-Center:**

- **Hum-vee (JEEP) $400** — Requires: Weapons Factory. GDI.
  10781 vs infantry, 5031 vs light vehicles, **719 vs buildings and tanks**. Speed
  145, 16000 HP. Scout and infantry-shredder. Never send it at a base.
- **Nod Buggy (BGGY) $300** — Requires: Airstrip. Nod.
  9375 vs infantry, 4375 vs light vehicles, 625 vs buildings/tanks. Speed 170.
  Same rule as the Hum-vee.
- **APC $600** — Requires: Barracks (built at the Weapons Factory). GDI only.
  Carries 5 infantry, speed 128, 19000 HP, Heavy armor. Its own gun is weak (833 vs
  infantry, 2083 vs light vehicles, 694 vs buildings/tanks) plus a light AA gun.
  Its value is the cargo: this is the transport-drop vehicle.
- **Recon Bike (BIKE) $500** — Requires: Airstrip. Nod.
  2583 vs tanks and light vehicles, 1917 vs buildings, 583 vs infantry. Speed 192,
  11000 HP, Light armor. Hit-and-run on harvesters and undefended structures.
- **Harvester (HARV) $1100** — Requires: Tiberium Refinery. Both. Your economy.
  62500 HP. Protect yours, hunt theirs — a dead harvester costs $1100 plus income.
- **MCV $3000** — Requires: Weapons Factory / Airstrip only. Both. 120000 HP.
  Deploys into a Construction Yard. The expansion tool when your tiberium runs out.
- **Supply Truck (TRUCK) $1000** — Requires: Weapons Factory / Airstrip. Both.
  Not a weapon.

**Post-Comms-Center:**

- **Medium Tank (MTNK) $900** — Requires: **Communications Center**. GDI.
  2500 vs tanks, light vehicles AND buildings; 625 vs infantry. 45000 HP, speed 72.
  GDI's workhorse — masses of these win games. Mix in Minigunners for infantry.
- **Light Tank (LTNK) $750** — Requires: **Communications Center**. Nod.
  2083 vs light vehicles, 1833 vs tanks and buildings, 500 vs infantry. 32000 HP,
  speed 102. Loses one-on-one to a Medium Tank; win by numbers, speed and flanking.
- **Flame Tank (FTNK) $600** — Requires: **Communications Center**. Nod.
  6308 vs infantry, 5769 vs buildings and light vehicles, 1385 vs tanks. 27000 HP,
  range 3.5. The premier Nod base-cracker; keep it away from armor.
- **Artillery (ARTY) $600** — Requires: **Communications Center**. Nod.
  5385 vs infantry, 4308 vs light vehicles, 3846 vs buildings, 2885 vs tanks, at
  range 11. Only 7500 HP and Light armor — always escorted, never in front.
- **Rocket Launcher (MSAM) $900** — Requires: **Communications Center**. GDI.
  2500 vs light vehicles, 1500 vs buildings, 1200 vs tanks, 600 vs infantry. Range
  11 but **minimum range 3** — it cannot defend itself up close. 12000 HP, Light
  armor. GDI's siege piece and the counter to Nod Artillery.
- **Mobile SAM (MLRS) $600** — Requires: **Communications Center**. Nod.
  **Anti-air only.** Never buy it to fight ground.
- **Mammoth Tank (HTNK) $1800** — Requires: **Advanced Communications Center**. GDI.
  (Not the Repair Facility.) 5000 vs tanks, vehicles and buildings from the twin
  cannon, plus missiles that hit infantry and aircraft. 87000 HP, self-repairs to
  half. Speed 46 — slow enough that it must move with the army, not behind it.
- **Stealth Tank (STNK) $900** — Requires: **Temple of Nod**. Nod.
  Invisible until it fires. 4286 vs light vehicles, 3857 vs tanks, 3214 vs
  buildings. 15000 HP, Light armor. A raider, not a line unit.

## 6. Aircraft

- **Chinook Transport (TRAN) $750** — Requires: Helipad. Both factions.
  Unarmed, carries 10 infantry, 12500 HP. The only air drop, and Nod's only
  transport of any kind.
- **Orca (ORCA) $1200** — Requires: Helipad **and Communications Center**. GDI.
  5833 vs buildings and light vehicles, 4375 vs tanks, 1667 vs infantry. 10000 HP.
  Rearms at the Helipad between runs.
- **Apache Longbow (HELI) $1200** — Requires: Helipad **and Communications Center**.
  Nod. 5000 vs infantry, 3750 vs buildings and light vehicles, 1250 vs tanks.

## 7. Defenses (armor Concrete — rockets and shells hurt them, bullets and flames do not)

- **Guard Tower (GTWR) $600** — Requires: Barracks / Hand of Nod. **Both factions.**
  3000 vs infantry, 2100 vs light vehicles, 900 vs tanks. 40000 HP, range 6, -10 power.
  Stops infantry rushes; will not stop armor.
- **Turret (GUN) $600** — Requires: Barracks / Hand of Nod. **Both factions.**
  5000 vs tanks and light vehicles, 2500 vs buildings, 1000 vs infantry. 41000 HP,
  range 6, -20 power. The anti-armor emplacement — pair it with a Guard Tower.
- **Advanced Guard Tower (ATWR) $1000** — Requires: **Communications Center**. GDI.
  5000 vs tanks, vehicles and defenses, 2600 vs infantry, and it hits aircraft.
  55000 HP, range 7, -50 power.
- **SAM Site (SAM) $650** — Requires: **Hand of Nod**. Nod. **Anti-air only.**
- **Obelisk of Light (OBLI) $1500** — Requires: **Temple of Nod**. Nod.
  22500 vs every ground target (11250 vs buildings). 75000 HP, range 7.5.
  The strongest defense in the game; drains **90** power — build the power first.
- **Sandbag Barrier $25** (GDI) / **Chain Link Barrier $25** (Nod) — Requires:
  Construction Yard. **Concrete Barrier $150** — Requires: Weapons Factory/Airstrip.
- Any static line dies to Artillery or a Rocket Launcher parked outside its range.
  **Defenses buy time; only an army wins.**

## 8. Structures, cost and power

Provides: Power Plant +100, Advanced Power Plant +200.
Drains: Refinery 40, Weapons Factory / Airstrip 40, Communications Center 50,
Helipad 20, Repair Facility 20, Barracks / Hand of Nod 15, Silo 10, Guard Tower 10,
Turret 20, SAM Site 20, Advanced Guard Tower 50, Obelisk 90, Temple of Nod 150,
Advanced Communications Center 200.

Building HP (all Wood armor unless noted): Construction Yard 210000, Temple of Nod
210000, Advanced Comms Center 130000, Weapons Factory / Airstrip 110000, Refinery
100000, Communications Center 80000, Repair Facility 80000, Advanced Power Plant
70000, Barracks / Hand of Nod / Helipad 60000, Power Plant 55000, Silo 50000.

Production buildings are the softest valuable targets in the game — Wood armor,
and a Barracks or Helipad has barely more HP than a Medium Tank.

## 9. Composition and targeting

- Mix by target: anti-infantry (Hum-vee / Buggy / Minigunner) + anti-armor (Rocket
  Soldier / Turret / tanks) + a building-killer (Flame Tank / Artillery / Rocket
  Launcher) beats a same-cost single-type army.
- Counter what you SEE. Enemy infantry mass → Hum-vees, Guard Towers, Grenadiers.
  Enemy tanks → Rocket Soldiers, Turrets, Medium Tanks. Enemy turtling behind
  defenses → Artillery or Rocket Launcher outrange them, or fly over with Orcas.
- Kill economy first: Harvesters and Refineries. A dead Harvester costs more than a
  dead tank.
- Speed governs cohesion. A ball moves at its slowest member: Rocket Soldier 39,
  Mammoth 46, Minigunner 54, Medium Tank / Artillery / Rocket Launcher / Harvester
  72, Light Tank 102, APC 128, Hum-vee 145, Buggy 170, Recon Bike 192. Do not send
  Rocket Soldiers to walk with tanks — carry them, or leave them home on defense.
- GDI in one line: expensive, durable, direct — Medium Tank mass with Rocket
  Launcher support, APC drops early, Orcas for reach, Mammoths late.
- Nod in one line: cheap, fast, sneaky — Buggy and Bike harassment, Flame Tank
  base-cracks, Artillery sieges, Stealth Tanks and Obelisks late.

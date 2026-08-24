#!/usr/bin/env python3
"""Parser for OpenRA replay files (.orarep).

Format (authoritative source: OpenRA.Game/Network/ReplayRecorder.cs,
ReplayConnection.cs, OrderIO.cs, Order.cs, FileFormats/ReplayMetadata.cs):

    file := chunk* metadata?
    chunk := int32 clientId, int32 packetLength, byte[packetLength] packet
    packet := int32 frame, payload
    payload := either a single SyncHash order (0x65, 13 bytes),
               a Disconnect order (0xBF, 5 bytes),
               or a stream of serialized Orders.

Order serialization (Order.Serialize / Order.Deserialize):
    byte type
    0xFF Fields:
        string orderString           (7-bit-length-prefixed UTF8, BinaryWriter.Write(string))
        int16 flags                  (OrderFields)
        [0x80 Subject]       uint32 actorId
        [0x01 Target]        byte TargetType {0 Invalid,1 Actor,2 Terrain,3 FrozenActor}
                             Actor:       uint32 actorId, int32 generation
                             FrozenActor: uint32 playerActorId, uint32 frozenActorId
                             Terrain + [0x40 TargetIsCell]: int32 cellBits, byte subCell
                             Terrain otherwise: int32 x,y,z then int16 n;
                                 n == -1 -> single pos, else n * (int32 x,y,z)
        [0x04 TargetString]  string
        [0x02 ExtraActors]   int32 count, count * uint32 actorId
        [0x10 ExtraLocation] int32 cellBits
        [0x20 ExtraData]     uint32
        [0x100 Grouped]      int32 count, count * uint32 actorId
        (0x08 Queued is a flag only, no payload)
    0xFE Handshake: string name, string targetString

Timing (verified against the source, not assumed):
  * mods/cnc/mod.yaml GameSpeeds "default": Timestep 40ms  => 25 world ticks/second,
    OrderLatency 3.
  * OrderManager.TryTick only calls ProcessOrders (which does NetFrameNumber++) when
    `LocalFrameNumber % NetFrameInterval == 0`, and Session.GlobalSettings.NetFrameInterval
    is 3 in these replays => 1 net frame == 3 world ticks.
  * Server.ReceiveOrders does `frame += OrderLatency` before dispatching, so the frame
    stamped on a recorded packet is the EXECUTION frame; the player actually issued the
    order OrderLatency (3) net frames = 9 ticks = 0.36 s earlier.

  execution tick    = (frame - 1) * NET_FRAME_INTERVAL
  execution seconds = (frame - 1) * 3 / 25 = (frame - 1) * 0.12
  issue seconds     = execution seconds - 0.36
"""

import argparse
import csv
import io
import json
import os
import struct
import sys
from collections import Counter, defaultdict

TICKS_PER_SECOND = 25       # mods/cnc/mod.yaml GameSpeeds default -> Timestep 40ms
NET_FRAME_INTERVAL = 3      # Session.GlobalSettings.NetFrameInterval (read from SyncInfo)
ORDER_LATENCY = 3           # GameSpeed.OrderLatency for cnc "default"

META_START_MARKER = -1
META_END_MARKER = -2

ORDER_FIELDS = [
    (0x01, "Target"),
    (0x02, "ExtraActors"),
    (0x04, "TargetString"),
    (0x08, "Queued"),
    (0x10, "ExtraLocation"),
    (0x20, "ExtraData"),
    (0x40, "TargetIsCell"),
    (0x80, "Subject"),
    (0x100, "Grouped"),
]

TARGET_TYPES = {0: "Invalid", 1: "Actor", 2: "Terrain", 3: "FrozenActor"}


class Reader:
    def __init__(self, buf, pos=0):
        self.buf = buf
        self.pos = pos

    def remaining(self):
        return len(self.buf) - self.pos

    def _take(self, n):
        if self.pos + n > len(self.buf):
            raise EOFError(f"want {n} bytes at {self.pos}, have {len(self.buf) - self.pos}")
        b = self.buf[self.pos:self.pos + n]
        self.pos += n
        return b

    def u8(self):
        return self._take(1)[0]

    def i16(self):
        return struct.unpack("<h", self._take(2))[0]

    def i32(self):
        return struct.unpack("<i", self._take(4))[0]

    def u32(self):
        return struct.unpack("<I", self._take(4))[0]

    def string(self):
        # BinaryReader.ReadString: 7-bit encoded length prefix, then UTF8 bytes
        length = 0
        shift = 0
        while True:
            b = self.u8()
            length |= (b & 0x7F) << shift
            if not b & 0x80:
                break
            shift += 7
            if shift > 35:
                raise ValueError("bad 7-bit length prefix")
        return self._take(length).decode("utf-8", errors="replace")


def cpos_from_bits(bits):
    """CPos bit packing: XXXXXXXXXXXX YYYYYYYYYYYY LLLLLLLL (signed 12-bit x, y)."""
    x = bits >> 20                       # python ints are already sign-extended
    y = ((bits >> 4) & 0xFFFF)
    y = y - 0x10000 if y & 0x8000 else y
    y = y >> 4
    layer = bits & 0xFF
    return (x, y, layer)


def parse_order(r):
    """Parse one order; returns dict. Raises on malformed data."""
    otype = r.u8()
    if otype == 0xFE:  # Handshake
        name = r.string()
        ts = r.string()
        return {"type": "Handshake", "order": name, "target_string": ts}
    if otype == 0x65:  # SyncHash
        sync = r.i32()
        defeat = struct.unpack("<Q", r._take(8))[0]
        return {"type": "SyncHash", "order": "SyncHash", "sync": sync, "defeat_state": defeat}
    if otype == 0xBF:  # Disconnect
        return {"type": "Disconnect", "order": "Disconnect", "client": r.i32()}
    if otype == 0x10:  # Ack
        return {"type": "Ack", "order": "Ack", "count": r.u8()}
    if otype == 0x20:  # Ping
        ts = struct.unpack("<q", r._take(8))[0]
        return {"type": "Ping", "order": "Ping", "timestamp": ts}
    if otype == 0x76:  # TickScale
        return {"type": "TickScale", "order": "TickScale",
                "scale": struct.unpack("<f", r._take(4))[0]}
    if otype != 0xFF:
        raise ValueError(f"unknown order type 0x{otype:02X} at offset {r.pos - 1}")

    o = {"type": "Fields"}
    o["order"] = r.string()
    flags = r.i16() & 0xFFFF
    o["flags"] = flags
    o["flag_names"] = [n for m, n in ORDER_FIELDS if flags & m]

    if flags & 0x80:
        o["subject"] = r.u32()
    if flags & 0x01:
        tt = r.u8()
        o["target_type"] = TARGET_TYPES.get(tt, f"Unknown({tt})")
        if tt == 1:  # Actor
            o["target_actor"] = r.u32()
            o["target_generation"] = r.i32()
        elif tt == 3:  # FrozenActor
            o["target_player_actor"] = r.u32()
            o["target_frozen_actor"] = r.u32()
        elif tt == 2:  # Terrain
            if flags & 0x40:
                o["target_cell"] = cpos_from_bits(r.i32())
                o["target_subcell"] = r.u8()
            else:
                pos = (r.i32(), r.i32(), r.i32())
                o["target_pos"] = pos
                n = r.i16()
                if n == -1:
                    o["target_positions"] = [pos]
                else:
                    o["target_positions"] = [(r.i32(), r.i32(), r.i32()) for _ in range(n)]
        elif tt != 0:
            raise ValueError(f"unknown target type {tt}")
    if flags & 0x04:
        o["target_string"] = r.string()
    o["queued"] = bool(flags & 0x08)
    if flags & 0x02:
        n = r.i32()
        o["extra_actors"] = [r.u32() for _ in range(n)]
    if flags & 0x10:
        o["extra_location"] = cpos_from_bits(r.i32())
    if flags & 0x20:
        o["extra_data"] = r.u32()
    if flags & 0x100:
        n = r.i32()
        o["grouped_actors"] = [r.u32() for _ in range(n)]
    return o


def parse_packet(frame, payload):
    """Parse the order stream of one packet payload (bytes after the frame int32)."""
    orders = []
    r = Reader(payload)
    while r.remaining() > 0:
        start = r.pos
        try:
            o = parse_order(r)
        except Exception as e:  # noqa: BLE001
            orders.append({"type": "ParseError", "order": "<parse-error>",
                           "error": str(e), "offset": start,
                           "raw": payload[start:start + 32].hex()})
            break
        o["frame"] = frame
        orders.append(o)
    return orders


def read_metadata(path):
    """Read the trailing ReplayMetadata block (MiniYAML GameInformation), if present."""
    with open(path, "rb") as f:
        f.seek(0, os.SEEK_END)
        size = f.tell()
        if size < 20:
            return None
        f.seek(-8, os.SEEK_END)
        data_length = struct.unpack("<i", f.read(4))[0]
        end_marker = struct.unpack("<i", f.read(4))[0]
        if end_marker != META_END_MARKER:
            return None
        f.seek(-(8 + data_length + 8), os.SEEK_END)
        start = struct.unpack("<i", f.read(4))[0]
        version = struct.unpack("<i", f.read(4))[0]
        if start != META_START_MARKER:
            return None
        strlen = 0
        shift = 0
        while True:
            b = f.read(1)[0]
            strlen |= (b & 0x7F) << shift
            if not b & 0x80:
                break
            shift += 7
        return {"version": version, "yaml": f.read(strlen).decode("utf-8", errors="replace")}


def parse_replay(path):
    """Yield (client_id, frame, order_dict) for every order in the replay."""
    with open(path, "rb") as f:
        blob = f.read()
    r = Reader(blob)
    out = []
    while r.remaining() >= 8:
        client = r.i32()
        if client == META_START_MARKER:
            break
        length = r.i32()
        if length < 0 or r.pos + length > len(blob):
            sys.stderr.write(f"truncated packet at {r.pos}: len={length}\n")
            break
        packet = r._take(length)
        if len(packet) < 4:
            continue
        frame = struct.unpack("<i", packet[:4])[0]
        for o in parse_packet(frame, packet[4:]):
            out.append((client, frame, o))
    return out


# --------------------------------------------------------------------------
# Categorisation helpers (cnc mod order strings)
# --------------------------------------------------------------------------
PRODUCTION_ORDERS = {"StartProduction", "CancelProduction", "PauseProduction"}
PLACEMENT_ORDERS = {"PlaceBuilding", "LineBuild", "PlacePlug"}
MOVE_ORDERS = {"Move", "Stop", "Scatter", "ReturnToBase", "Enter", "Deploy",
               "DeployTransform", "Harvest", "Deliver", "Repair", "Sell",
               "Guard", "Unload", "Land", "Nudge", "Infiltrate", "Capture",
               "DemolishTarget", "Heal", "RepairBuilding"}
ATTACK_ORDERS = {"Attack", "AttackMove", "ForceAttack", "AssaultMove",
                 "SetUnitStance", "Airstrike", "NukePowerInfoOrder"}
META_ORDERS = {"CreateGroup", "SelectGroup", "AddToGroup", "CombineGroup",
               "Chat", "TeamChat", "HandshakeResponse", "SyncInfo", "StartGame",
               "PingResponse", "SyncConnectionQuality", "SyncClientPings",
               "SyncLobbyClients", "SyncLobbyGlobalSettings", "SyncLobbyInfo",
               "SyncLobbySlots", "Pause", "PauseGame"}


def categorize(order_string):
    if order_string in PRODUCTION_ORDERS:
        return "production"
    if order_string in PLACEMENT_ORDERS:
        return "placement"
    if order_string in ATTACK_ORDERS:
        return "attack"
    if order_string in MOVE_ORDERS:
        return "movement"
    return "other"


def frame_to_tick(frame):
    """Net frame -> world tick at which the order executes."""
    return (frame - 1) * NET_FRAME_INTERVAL


def frame_to_seconds(frame):
    return frame_to_tick(frame) / TICKS_PER_SECOND


def mmss(frame):
    s = frame_to_seconds(frame)
    return f"{int(s) // 60:d}:{int(s) % 60:02d}"


def describe(o):
    bits = []
    if "subject" in o:
        bits.append(f"subj={o['subject']}")
    if "target_string" in o:
        bits.append(f"str={o['target_string']}")
    if "target_cell" in o:
        bits.append(f"cell={o['target_cell'][0]},{o['target_cell'][1]}")
    if "target_actor" in o:
        bits.append(f"tactor={o['target_actor']}")
    if "target_frozen_actor" in o:
        bits.append(f"tfrozen={o['target_frozen_actor']}")
    if "target_pos" in o:
        p = o["target_pos"]
        bits.append(f"pos={p[0]},{p[1]}")
    if "extra_location" in o:
        bits.append(f"exloc={o['extra_location'][0]},{o['extra_location'][1]}")
    if "extra_data" in o:
        bits.append(f"data={o['extra_data']}")
    if "extra_actors" in o:
        bits.append(f"extra={len(o['extra_actors'])}")
    if "grouped_actors" in o:
        bits.append(f"grouped={len(o['grouped_actors'])}")
    if o.get("queued"):
        bits.append("queued")
    return " ".join(bits)


def player_commands(rows, client):
    """Collapse the raw order stream of one client into player COMMANDS.

    UnitOrderGenerator.OrderInner emits a synthetic "CreateGroup" order carrying the
    actors involved, immediately followed by one real order per actor, so a single
    mouse click shows up as 1 + N orders in the stream.  Hotkey orders (SetUnitStance,
    AttackMove, ...) have no CreateGroup: AttackMove instead carries GroupedActors.
    """
    seq = [(f, o) for c, f, o in rows if c == client]
    out = []
    i = 0
    while i < len(seq):
        f, o = seq[i]
        if o["order"] == "CreateGroup":
            sel = list(o.get("extra_actors", []))
            j = i + 1
            grp = []
            while j < len(seq) and seq[j][0] == f and seq[j][1]["order"] != "CreateGroup":
                grp.append(seq[j][1])
                j += 1
            byname = defaultdict(list)
            for g in grp:
                byname[g["order"]].append(g)
            if not grp:
                out.append((f, "<selection-only>", sel, None))
            for name, gs in byname.items():
                out.append((f, name, [g.get("subject") for g in gs], gs[0]))
            i = j
        else:
            j = i
            grp = []
            while j < len(seq) and seq[j][0] == f and seq[j][1]["order"] == o["order"]:
                grp.append(seq[j][1])
                j += 1
            actors = [g.get("subject") for g in grp]
            if len(grp) == 1 and grp[0].get("grouped_actors"):
                actors = list(grp[0]["grouped_actors"])
            out.append((f, o["order"], actors, o))
            i = j
    return out


def main():
    ap = argparse.ArgumentParser(description="Parse an OpenRA .orarep replay")
    ap.add_argument("file")
    ap.add_argument("--client", type=int, action="append",
                    help="only show orders from this client id (repeatable)")
    ap.add_argument("--csv", action="store_true", help="emit CSV")
    ap.add_argument("--json", action="store_true", help="emit JSON lines")
    ap.add_argument("--meta", action="store_true", help="dump trailing metadata yaml")
    ap.add_argument("--summary", action="store_true", help="per-client order histograms")
    ap.add_argument("--commands", action="store_true",
                    help="collapse per-actor order expansion into single player commands "
                         "(requires --client)")
    ap.add_argument("--include", help="comma separated order-string whitelist")
    ap.add_argument("--exclude-noise", action="store_true",
                    help="hide SyncHash/Ping/heartbeat orders")
    args = ap.parse_args()

    if args.meta:
        m = read_metadata(args.file)
        print(m["yaml"] if m else "<no metadata block>")
        return

    rows = parse_replay(args.file)
    include = set(args.include.split(",")) if args.include else None
    noise = {"SyncHash", "Ping", "Ack", "TickScale", "PingResponse",
             "SyncConnectionQuality", "SyncClientPings"}

    def keep(client, o):
        if args.client and client not in args.client:
            return False
        if include and o["order"] not in include:
            return False
        if args.exclude_noise and o["order"] in noise:
            return False
        return True

    rows = [(c, f, o) for c, f, o in rows if keep(c, o)]

    if args.summary:
        per = defaultdict(Counter)
        for c, f, o in rows:
            per[c][o["order"]] += 1
        for c in sorted(per):
            total = sum(per[c].values())
            print(f"=== client {c}: {total} orders")
            for name, n in per[c].most_common():
                print(f"  {n:6d}  {name}")
        return

    if args.commands:
        if not args.client:
            sys.exit("--commands requires --client N")
        for cid in args.client:
            for f, name, actors, o in player_commands(rows, cid):
                o = o or {}
                print(f"[c{cid}] {f:6d} {mmss(f):>6}  {categorize(name):<10} {name:<20} "
                      f"n={len(actors):<3} {describe(o)}")
        return

    if args.csv:
        w = csv.writer(sys.stdout)
        w.writerow(["client", "frame", "tick", "time", "category", "order", "queued",
                    "subject", "target_type", "target_string", "cell_x", "cell_y",
                    "target_actor", "extra_data", "extra_actors", "grouped_actors",
                    "extra_loc_x", "extra_loc_y"])
        for c, f, o in rows:
            cell = o.get("target_cell") or (None, None, None)
            ex = o.get("extra_location") or (None, None, None)
            w.writerow([c, f, frame_to_tick(f), mmss(f), categorize(o["order"]), o["order"],
                        int(bool(o.get("queued"))), o.get("subject", ""),
                        o.get("target_type", ""), o.get("target_string", ""),
                        cell[0], cell[1], o.get("target_actor", ""),
                        o.get("extra_data", ""),
                        len(o.get("extra_actors", []) or []) or "",
                        len(o.get("grouped_actors", []) or []) or "",
                        ex[0], ex[1]])
        return

    if args.json:
        for c, f, o in rows:
            o = dict(o)
            o["client"] = c
            print(json.dumps(o))
        return

    for c, f, o in rows:
        print(f"[c{c}] {f:6d} {mmss(f):>6}  {categorize(o['order']):<10} "
              f"{o['order']:<22} {describe(o)}")


if __name__ == "__main__":
    main()

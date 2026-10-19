"""
Builds MojaveDesktopRadio.esp byte-for-byte (no GECK/xEdit needed).

The plugin adds exactly one Pip-Boy radio station and overrides NOTHING from FalloutNV.esm:

  0x01000800  TACT  MDR_DesktopRadioStation      "Mojave Desktop Radio"  (record flag 0x00020000 = Radio Station)
  0x01000801  CELL  MDR_BroadcastBooth           private interior cell (never visited)
  0x01000802  REFR  MDR_DesktopRadioStationRef   persistent, XRDO broadcast range = Everywhere

The layout copies the vanilla "Mojave Music Radio" station (TACT vCountryRadio, persistent REFR in the
interior cell Vault11a with XRDO type 1), which was verified by parsing FalloutNV.esm
(see tools/inspect_plugin.py). The station has no dialogue/quest, so the game itself plays nothing
on it — the music comes from the desktop through the BridgeHost.

Usage:  python tools/build_esp.py [output.esp]
"""
import struct
import sys
from pathlib import Path

FORM_VERSION = 15          # Fallout: New Vegas
HEDR_VERSION = 1.34

MASTER = "FalloutNV.esm"
TACT_ID = 0x01000800
CELL_ID = 0x01000801
REFR_ID = 0x01000802
NEXT_OBJECT_ID = 0x803

# From vanilla vCountryRadio (Mojave Music Radio) in FalloutNV.esm
VANILLA_OBND = bytes.fromhex("f4ffeeffe9ff340016001300")
VANILLA_MODEL = "terminals\\terminal01.nif"
VANILLA_VOICE_TYPE = 0x0000002D
VANILLA_RADIO_TEMPLATE = 0x0004EC8A
# From vanilla interior TOPSAcesTheater
VANILLA_XCLL = bytes.fromhex("3937550000000000332222000000804200c0da4500000000000000000000803f0040ce450000803f")

FLAG_RADIO_STATION = 0x00020000
FLAG_PERSISTENT = 0x00000400

BROADCAST_EVERYWHERE = 1


def zstr(text: str) -> bytes:
    return text.encode("cp1252") + b"\0"


def sub(sig: str, data: bytes) -> bytes:
    if len(data) > 0xFFFF:
        raise ValueError(f"subrecord {sig} too large")
    return sig.encode("ascii") + struct.pack("<H", len(data)) + data


def record(sig: str, form_id: int, flags: int, payload: bytes) -> bytes:
    # type, dataSize, flags, formID, version-control info, form version, unknown
    return sig.encode("ascii") + struct.pack("<IIIIHH", len(payload), flags, form_id, 0, FORM_VERSION, 0) + payload


def group(label: bytes, group_type: int, content: bytes) -> bytes:
    assert len(label) == 4
    # 'GRUP', groupSize (incl. 24-byte header), label, type, stamp, unknown, version, unknown
    return b"GRUP" + struct.pack("<I", 24 + len(content)) + label + struct.pack("<iHHHH", group_type, 0, 0, 0, 0) + content


def build() -> bytes:
    tact = record("TACT", TACT_ID, FLAG_RADIO_STATION,
                  sub("EDID", zstr("MDR_DesktopRadioStation"))
                  + sub("OBND", VANILLA_OBND)
                  + sub("FULL", zstr("Mojave Desktop Radio"))
                  + sub("MODL", zstr(VANILLA_MODEL))
                  + sub("VNAM", struct.pack("<I", VANILLA_VOICE_TYPE))
                  + sub("INAM", struct.pack("<I", VANILLA_RADIO_TEMPLATE)))

    cell = record("CELL", CELL_ID, 0,
                  sub("EDID", zstr("MDR_BroadcastBooth"))
                  + sub("FULL", zstr("Mojave Desktop Radio Booth"))
                  + sub("DATA", bytes([0x01]))                     # Is Interior Cell
                  + sub("XCLL", VANILLA_XCLL)
                  + sub("LTMP", struct.pack("<I", 0))
                  + sub("LNAM", struct.pack("<I", 0x9F))
                  + sub("XCLW", bytes.fromhex("ffff7f7f")))         # water height: none (FLT_MAX)

    refr = record("REFR", REFR_ID, FLAG_PERSISTENT,
                  sub("EDID", zstr("MDR_DesktopRadioStationRef"))
                  + sub("NAME", struct.pack("<I", TACT_ID))
                  # range radius, broadcast range type, static percentage, position reference
                  + sub("XRDO", struct.pack("<fIfI", 0.0, BROADCAST_EVERYWHERE, 0.0, 0))
                  + sub("DATA", struct.pack("<6f", 0, 0, 0, 0, 0, 0)))

    object_id = CELL_ID & 0xFFFFFF
    block, sub_block = object_id % 10, (object_id // 10) % 10
    cell_label = struct.pack("<I", CELL_ID)
    persistent = group(cell_label, 8, refr)          # Cell Persistent Children
    children = group(cell_label, 6, persistent)      # Cell Children
    cells = group(b"CELL", 0,
                  group(struct.pack("<i", block), 2,
                        group(struct.pack("<i", sub_block), 3, cell + children)))
    tacts = group(b"TACT", 0, tact)

    num_records = 3 + 6  # records + groups, TES4 excluded
    header = record("TES4", 0, 0,
                    sub("HEDR", struct.pack("<fiI", HEDR_VERSION, num_records, NEXT_OBJECT_ID))
                    + sub("CNAM", zstr("Mojave Desktop Radio"))
                    + sub("SNAM", zstr("Adds the Pip-Boy station 'Mojave Desktop Radio' that controls the "
                                       "Spotify desktop app through the MDR BridgeHost. Overrides no vanilla records."))
                    + sub("MAST", zstr(MASTER))
                    + sub("DATA", struct.pack("<Q", 0)))
    return header + tacts + cells


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "mod" / "MojaveDesktopRadio.esp"
    out.parent.mkdir(parents=True, exist_ok=True)
    data = build()
    out.write_bytes(data)
    print(f"wrote {out} ({len(data)} bytes)")


if __name__ == "__main__":
    main()

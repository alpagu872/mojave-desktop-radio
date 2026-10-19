"""
Independent parser/validator for MojaveDesktopRadio.esp (and any FNV plugin).

  python tools/inspect_plugin.py mod/MojaveDesktopRadio.esp [--vanilla <path to FalloutNV.esm>]

Checks: header layout, every GRUP size, record/subrecord bounds, the station's radio flag,
persistent REFR with XRDO "Everywhere", that no master record is overridden, and — with
--vanilla — that our TACT/REFR use the same subrecord layout as the vanilla Mojave Music Radio.
Exit code 0 = all checks passed.
"""
import struct
import sys
import zlib

GROUP_TYPES = {0: "top", 1: "world children", 2: "interior block", 3: "interior sub-block", 4: "exterior block",
               5: "exterior sub-block", 6: "cell children", 7: "topic children", 8: "cell persistent", 9: "cell temporary",
               10: "cell visible distant"}


def subrecords(buf):
    out, i, big = [], 0, None
    while i < len(buf):
        if i + 6 > len(buf):
            raise ValueError("truncated subrecord header")
        sig = buf[i:i + 4].decode("latin1")
        size = struct.unpack_from("<H", buf, i + 4)[0]
        i += 6
        if sig == "XXXX":
            big = struct.unpack_from("<I", buf, i)[0]
            i += size
            continue
        if big is not None:
            size, big = big, None
        if i + size > len(buf):
            raise ValueError(f"subrecord {sig} overruns record")
        out.append((sig, buf[i:i + size]))
        i += size
    return out


class Plugin:
    def __init__(self, path, wanted=None, verbose=False):
        self.data = open(path, "rb").read()
        self.records = []  # (sig, formid, flags, ver, subrecords, group path)
        self.errors = []
        self.wanted = wanted
        self.verbose = verbose
        sig, size, flags, fid, vc, ver, _ = struct.unpack_from("<4sIIIIHH", self.data, 0)
        if sig != b"TES4":
            raise ValueError("not a TES4 plugin")
        self.header = subrecords(self.data[24:24 + size])
        self.header_ver = ver
        self.masters = [b.rstrip(b"\0").decode("cp1252") for s, b in self.header if s == "MAST"]
        self.walk(24 + size, len(self.data), [])

    def walk(self, start, end, path):
        i = start
        while i < end:
            if i + 24 > end:
                self.errors.append(f"truncated header at {i:#x}")
                return
            sig = self.data[i:i + 4]
            if sig == b"GRUP":
                gsize, label, gtype = struct.unpack_from("<I4si", self.data, i + 4)
                if gsize < 24 or i + gsize > end:
                    self.errors.append(f"bad GRUP size {gsize} at {i:#x}")
                    return
                if self.verbose:
                    print("  " * len(path) + f"GRUP {GROUP_TYPES.get(gtype, gtype)} label={label.hex()} size={gsize}")
                self.walk(i + 24, i + gsize, path + [(gtype, struct.unpack('<I', label)[0])])
                i += gsize
            else:
                rsig, size, flags, fid, vc, ver, _ = struct.unpack_from("<4sIIIIHH", self.data, i)
                rsig = rsig.decode("latin1")
                if i + 24 + size > end:
                    self.errors.append(f"record {rsig} {fid:08X} overruns its group")
                    return
                payload = self.data[i + 24:i + 24 + size]
                if self.wanted is None or rsig in self.wanted:
                    if flags & 0x40000:
                        payload = zlib.decompressobj().decompress(payload[4:])
                    try:
                        subs = subrecords(payload)
                    except ValueError as e:
                        self.errors.append(f"{rsig} {fid:08X}: {e}")
                        subs = []
                    self.records.append((rsig, fid, flags, ver, subs, path))
                    if self.verbose:
                        print("  " * len(path) + f"{rsig} {fid:08X} flags={flags:08X} ver={ver} subs={[s for s, _ in subs]}")
                i += 24 + size

    def find(self, sig, fid=None):
        return [r for r in self.records if r[0] == sig and (fid is None or r[1] == fid)]


def field(subs, sig):
    for s, b in subs:
        if s == sig:
            return b
    return None


def text(b):
    return b.rstrip(b"\0").decode("cp1252") if b is not None else None


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 2
    vanilla = None
    if "--vanilla" in args:
        k = args.index("--vanilla")
        vanilla = args[k + 1]
        del args[k:k + 2]
    verbose = "-v" in args
    args = [a for a in args if a != "-v"]

    p = Plugin(args[0], verbose=verbose)
    ok = True

    def check(cond, msg):
        nonlocal ok
        print(("  OK   " if cond else "  FAIL ") + msg)
        ok &= bool(cond)

    print(f"{args[0]}: masters={p.masters}, records={len(p.records)}")
    hedr = field(p.header, "HEDR")
    ver, num, nxt = struct.unpack("<fiI", hedr)
    check(abs(ver - 1.34) < 1e-3 and p.header_ver == 15, f"TES4 HEDR version {ver:.2f}, form version {p.header_ver}")
    check(not p.errors, "structure: " + ("all GRUP/record sizes consistent" if not p.errors else "; ".join(p.errors)))
    check(p.masters == ["FalloutNV.esm"], "only master is FalloutNV.esm")
    own_index = len(p.masters)
    overrides = [r for r in p.records if (r[1] >> 24) != own_index]
    check(not overrides, "no master records overridden" + (f" (found {[(r[0], hex(r[1])) for r in overrides]})" if overrides else ""))

    tacts = [r for r in p.find("TACT") if r[2] & 0x20000]
    check(len(tacts) == 1, f"exactly one radio-station TACT ({len(tacts)})")
    if tacts:
        t = tacts[0]
        check(text(field(t[4], "FULL")) == "Mojave Desktop Radio", f"TACT {t[1]:08X} FULL = {text(field(t[4], 'FULL'))!r}")
        refs = [r for r in p.find("REFR") if field(r[4], "NAME") and struct.unpack("<I", field(r[4], "NAME"))[0] == t[1]]
        check(len(refs) == 1, "one REFR places the station")
        if refs:
            r = refs[0]
            xrdo = field(r[4], "XRDO")
            rng, kind, static, pos = struct.unpack("<fIfI", xrdo)
            check(r[2] & 0x400 and not r[2] & 0x800, f"REFR {r[1]:08X} persistent and enabled (flags {r[2]:08X})")
            check(kind == 1 and static == 0.0, f"XRDO broadcast type {kind} (1 = Everywhere), static {static}")
            gtypes = [g for g, _ in r[5]]
            check(gtypes[-2:] == [6, 8] and 2 in gtypes, f"REFR lives in an interior cell's persistent group {gtypes}")
            cell_id = r[5][-1][1]
            cells = p.find("CELL", cell_id)
            check(len(cells) == 1 and field(cells[0][4], "DATA")[0] & 1, f"cell {cell_id:08X} exists and is interior")
            obj = cell_id & 0xFFFFFF
            labels = {g: l for g, l in r[5]}
            check(labels.get(2) == obj % 10 and labels.get(3) == (obj // 10) % 10, "interior block/sub-block numbering")

    if vanilla:
        print(f"comparing with vanilla Mojave Music Radio in {vanilla} ...")
        v = Plugin(vanilla, wanted={"TACT", "REFR"})
        vt = [r for r in v.find("TACT") if text(field(r[4], "EDID")) == "vCountryRadio"]
        check(len(vt) == 1, "vanilla vCountryRadio found")
        if vt and tacts:
            check([s for s, _ in vt[0][4]] == [s for s, _ in tacts[0][4]], f"TACT subrecord layout matches vanilla {[s for s, _ in vt[0][4]]}")
            check(field(vt[0][4], "INAM") == field(tacts[0][4], "INAM") and field(vt[0][4], "VNAM") == field(tacts[0][4], "VNAM"),
                  "same voice type / radio template as vanilla")
            vr = [r for r in v.find("REFR") if field(r[4], "NAME") == struct.pack("<I", vt[0][1])]
            if vr:
                ours = refs[0]
                check([s for s, _ in vr[0][4] if s != "EDID"] == [s for s, _ in ours[4] if s != "EDID"],
                      f"REFR subrecord layout matches vanilla {[s for s, _ in vr[0][4]]}")
                check(field(vr[0][4], "XRDO") == field(ours[4], "XRDO"), "XRDO bytes identical to vanilla")

    print("ALL CHECKS PASSED" if ok else "CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())

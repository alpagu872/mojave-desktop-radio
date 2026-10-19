"""
Static checks for the mod's xNVSE scripts, UDFs, UIO file and MCM JSON.

The game compiles these files only at runtime, so a typo is otherwise found by players.
This catches the classes of problems seen during development:

  * lines longer than the compiler handles reliably            (PushConfig.gek: "Mismatched quotes")
  * per-line unbalanced quotes / parentheses (outside strings and comments)
  * expressions the script compiler rejects: `x + (sv_Construct ...)` (PushConfig.gek: "Mismatched parentheses")
  * unbalanced begin/end, if/endif, while/loop blocks
  * CompileScript "MojaveDesktopRadio\\X.gek" pointing to a missing file
  * DispatchEventAlt with an MDR event the plugin does not register
  * MCM options using AuxVars that the load script never initialises, or calling unhandled events
  * UIO entries pointing to missing XML files

Usage:  python tools/lint_scripts.py [mod folder]      exit code 0 = clean
"""
import json
import re
import sys
from pathlib import Path

MAX_LINE = 300
PLUGIN_EVENTS = {"MDR:Cmd", "MDR:Set"}             # registered by MojaveDesktopRadio.dll with script dispatch
HANDLED_EVENTS = {"MDR:State", "MDR:Notify", "MDR_OnConfigChanged", "MDR_OnAssignKeys"}
FORBIDDEN = [
    (re.compile(r"\+\s*\(\s*sv_Construct", re.I), "string '+ (sv_Construct ...)' does not compile in game scripts"),
    (re.compile(r"&gt;|&lt;|&amp;"), "XML entities are not used by the game's UI parser"),
]


def strip_comment(line: str) -> str:
    """Remove a ';' comment that is not inside a string literal."""
    out, in_str = [], False
    for ch in line:
        if ch == '"':
            in_str = not in_str
        if ch == ";" and not in_str:
            break
        out.append(ch)
    return "".join(out)


def check_script(path: Path, errors: list, info: dict):
    text = path.read_text(encoding="utf-8", errors="replace")
    stack = []
    openers = {"begin": "end", "if": "endif", "while": "loop", "foreach": "loop"}
    closers = {"end": "begin", "endif": "if", "loop": ("while", "foreach")}
    for n, raw in enumerate(text.splitlines(), 1):
        where = f"{path.name}:{n}"
        if len(raw) > MAX_LINE:
            errors.append(f"{where}: line is {len(raw)} chars (max {MAX_LINE})")
        line = strip_comment(raw).strip()
        if not line:
            continue
        if line.count('"') % 2:
            errors.append(f"{where}: unbalanced quotes")
        depth, in_str = 0, False
        for ch in line:
            if ch == '"':
                in_str = not in_str
            elif not in_str:
                depth += ch == "("
                depth -= ch == ")"
                if depth < 0:
                    break
        if depth != 0:
            errors.append(f"{where}: unbalanced parentheses")
        for rx, why in FORBIDDEN:
            if rx.search(line):
                errors.append(f"{where}: {why}")

        word = re.split(r"[\s{(]", line.lower(), 1)[0]
        # xNVSE drops SetEventHandlerAlt handlers on every save load: they must not sit inside the
        # once-per-session "*_MDRInit" block (that broke the panel and MCM after the 2nd load in 0.2.0)
        if re.match(r"SetEventHandlerAlt\b", line, re.I) and any(g for _, _, g in stack):
            errors.append(f"{where}: SetEventHandlerAlt inside the once-per-session block - xNVSE removes it on the next load")
        if word in openers:
            stack.append((word, n, word == "if" and "_mdrinit" in line.lower()))
        elif word in closers:
            want = closers[word]
            if not stack or (stack[-1][0] not in (want if isinstance(want, tuple) else (want,))):
                errors.append(f"{where}: '{word}' without matching opener")
            else:
                stack.pop()

        for udf in re.findall(r'CompileScript\s+"([^"]+)"', line, re.I):
            info["compiled"].add(udf.replace("/", "\\").lower())
        for ev in re.findall(r'DispatchEventAlt\s+"(MDR[^"]*)"', line):
            if ev not in PLUGIN_EVENTS:
                errors.append(f"{where}: event '{ev}' is not registered by the plugin")
        for ev in re.findall(r'SetEventHandlerAlt\s+"([^"]+)"', line):
            info["handled"].add(ev)
        for var in re.findall(r'AuxVarSetFlt\s+"(\*_MDR[^"]+)"', line):
            info["auxvars"].add(var)
    for word, n, _ in stack:
        errors.append(f"{path.name}:{n}: '{word}' is never closed")


def main() -> int:
    mod = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "mod"
    errors: list[str] = []
    info = {"compiled": set(), "handled": set(), "auxvars": set()}
    udf_dir = mod / "NVSE" / "user_defined_functions"

    scripts = sorted((mod / "NVSE").rglob("*.gek")) + sorted((mod / "NVSE" / "Plugins" / "Scripts").glob("*.txt"))
    for s in scripts:
        check_script(s, errors, info)
        if s.suffix == ".gek" and not re.match(r"\s*begin\s+function", s.read_text(encoding="utf-8"), re.I):
            errors.append(f"{s.name}: a UDF must start with 'begin Function {{...}}'")

    for udf in sorted(info["compiled"]):
        if not (udf_dir / udf).exists():
            errors.append(f"CompileScript target missing: {udf}")

    for ev in HANDLED_EVENTS - info["handled"]:
        errors.append(f"no SetEventHandlerAlt for '{ev}' in the load script")

    mcm_file = mod / "MCM" / "MojaveDesktopRadio.json"
    mcm = json.loads(mcm_file.read_text(encoding="utf-8"))
    for sid, sub in mcm["submenus"].items():
        for oid, opt in sub["options"].items():
            where = f"MCM {sub['listTitle']}/{opt['title']}"
            for var in opt.get("vars", []):
                if var.get("auxVar") not in info["auxvars"]:
                    errors.append(f"{where}: auxVar {var.get('auxVar')} is not initialised in ln_MojaveDesktopRadio.txt")
                if "configINI" not in var:
                    errors.append(f"{where}: no configINI (the value would not persist)")
            call = opt.get("call", {})
            if call.get("type") == "event" and call.get("value") not in info["handled"]:
                errors.append(f"{where}: MCM event '{call.get('value')}' has no handler")
            if opt.get("type") == 1 and len(opt.get("strings", [])) < 2:
                errors.append(f"{where}: a type-1 option needs at least two strings")

    uio = mod / "UIO" / "Public" / "MojaveDesktopRadio.txt"
    for line in uio.read_text(encoding="utf-8").splitlines():
        if "::" in line:
            xml = mod / "Menus" / "Prefabs" / line.split("::")[0]
            if not xml.exists():
                errors.append(f"UIO: {xml} does not exist")
    for xml in (mod / "Menus").rglob("*.xml"):
        for n, raw in enumerate(xml.read_text(encoding="utf-8").splitlines(), 1):
            for rx, why in FORBIDDEN[1:]:
                if rx.search(raw):
                    errors.append(f"{xml.name}:{n}: {why}")

    print(f"checked {len(scripts)} scripts, {sum(len(s['options']) for s in mcm['submenus'].values())} MCM options")
    for e in errors:
        print("  FAIL", e)
    print("SCRIPT LINT PASSED" if not errors else f"SCRIPT LINT FAILED ({len(errors)})")
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())

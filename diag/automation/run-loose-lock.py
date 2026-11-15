"""Plays the protocol of "Checking the culprit": a small vessel closes in on Loose Lock during a time warp, Loose Lock
loads during it, then it is set spinning, and its locked piston should stay retracted.

It drives KSP through KSP-Util-MCPServer, a mod that answers HTTP requests on 127.0.0.1, and needs nothing but
Python 3: no AI, no package to install. Start KSP with KSP-Util-MCPServer installed, copy
loose-lock-rendezvous.sfs into a sandbox game, wait for the main menu, then run:

    python run-loose-lock.py --folder <your sandbox game> --out out

It loads the save, turns SAS on in retrograde mode, warps time at 10x until Loose Lock is about 100 m away, cancels
the relative speed at low throttle, switches to Loose Lock, opens the part action window of its piston, pitches for
a second with its reaction wheel, then reads the window of the piston for ten seconds. It takes a screenshot, prints a list of
checks, each PASS or FAIL, and exits with 0 when all pass, 1 otherwise. The check marked (fix) is what the mod
promises: it fails without it. KSP is left running at the end.
"""
import argparse
import json
import os
import re
import sys
import time
import urllib.request

URL = None
TARGET = "Loose Lock"
PISTON = "piston.03"


def call(tool, **args):
    """Calls one tool of KSP-Util-MCPServer and returns its answer, decoded from JSON when it is JSON."""
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                       "params": {"name": tool, "arguments": args}}).encode()
    request = urllib.request.Request(URL, body, {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=900) as response:
        result = json.loads(response.read())["result"]
    text = result["content"][0].get("text", "") if result["content"] else ""
    if result.get("isError"):
        raise RuntimeError(tool + ": " + text)
    try:
        return json.loads(text)
    except ValueError:
        return text


def log(*parts):
    print(time.strftime("%H:%M:%S"), *parts, flush=True)


def target():
    """The entry of Loose Lock in list_vessels."""
    for vessel in call("list_vessels"):
        if vessel["name"] == TARGET and vessel["situation"] == "ORBITING":
            return vessel
    raise RuntimeError("no vessel named %s in orbit: is the save the right one?" % TARGET)


def relative_speed():
    """The speed above the navball, in m/s: relative to the target, the navball being in Target mode."""
    vessel = call("get_state")["vessel"]
    if vessel["speedMode"] != "Target":
        raise RuntimeError("the navball is in %s mode, not Target" % vessel["speedMode"])
    return vessel["speed"]


def piston_window(part):
    """The switch "locked" and the extension of the piston, read in its open part action window."""
    locked = extension = None
    for menu in call("read_part_menus"):
        if menu["part"] != part:
            continue
        for item in menu["items"]:
            if item.get("name") == "servoIsLocked":
                locked = item["value"]
            elif item.get("name") == "currentExtension":
                # The line the window shows, such as "Current Extension: 4.18 m", in the language of the game.
                numbers = re.findall(r"-?\d+(?:[.,]\d+)?", item.get("text") or "")
                extension = float(numbers[-1].replace(",", ".")) if numbers else None
    return locked, extension


def main():
    global URL
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--folder", required=True, help="the sandbox game under saves/ the save was copied into")
    parser.add_argument("--save", default="loose-lock-rendezvous", help="the save, without .sfs")
    parser.add_argument("--stop", type=float, default=150.0, help="the distance to stop the warp at, in metres")
    parser.add_argument("--pitch", type=float, default=0.3, help="the pitch given to Loose Lock, 0 to 1")
    parser.add_argument("--pitch-seconds", type=float, default=1.0, help="how long to pitch")
    parser.add_argument("--watch", type=float, default=10.0, help="seconds to watch the piston for")
    parser.add_argument("--retracted", type=float, default=0.05,
                        help="the largest extension, in metres, that counts as retracted")
    parser.add_argument("--out", default="out", help="where the screenshot goes")
    parser.add_argument("--port", type=int, default=8770, help="the port of KSP-Util-MCPServer")
    options = parser.parse_args()
    URL = "http://127.0.0.1:%d/mcp/" % options.port
    os.makedirs(options.out, exist_ok=True)
    checks = []

    def check(fix, ok, text):
        checks.append((ok, ("(fix) " if fix else "") + text))

    if call("get_state")["scene"] == "MAINMENU":
        call("open_game", folder=options.folder)
    call("load_save", folder=options.folder, save=options.save)
    start = target()
    log("loaded: %s at %.0f m, loaded %s" % (TARGET, start["distance"], start["loaded"]))
    check(False, not start["loaded"], "%s is not loaded when the save is" % TARGET)

    # 1. SAS on, retrograde: the navball is in Target mode, so retrograde is relative to Loose Lock.
    call("set_flight", sas=True, sas_mode="Retrograde")
    time.sleep(3)

    # 2. Warp at 10x until Loose Lock is close.
    call("set_warp", rate_index=2)
    loaded_in_warp = False
    deadline = time.time() + 180
    while True:
        vessel = target()
        loaded_in_warp = loaded_in_warp or vessel["loaded"]
        if vessel["distance"] < options.stop or time.time() > deadline:
            break
        time.sleep(0.1)
    call("set_warp", rate_index=0)
    log("back to 1x at %.0f m" % target()["distance"])
    check(False, loaded_in_warp, "%s loads during the warp" % TARGET)

    # 3. Cancel the relative speed: the Terrier pushes this small vessel hard, hence the low throttle.
    call("set_flight", sas_mode="Retrograde")
    time.sleep(2)
    call("set_flight", throttle=0.05)
    deadline = time.time() + 15
    while relative_speed() > 0.3 and time.time() < deadline:
        time.sleep(0.05)
    call("set_flight", throttle=0)
    log("relative speed %.2f m/s" % relative_speed())

    # 4. Switch to Loose Lock, once the game no longer sees the vessel under acceleration, and open the window of
    #    its piston.
    time.sleep(2)
    vessel_id = target()["id"]
    for attempt in range(10):
        try:
            call("switch_vessel", vessel=vessel_id)
            break
        except RuntimeError as e:
            log("switch refused, again in a second:", e)
            time.sleep(1)
    piston = next(p for p in call("list_parts") if p["name"] == PISTON)
    call("part_menu", action="open", part=piston["id"])
    time.sleep(1)
    locked, extension = piston_window(piston["id"])
    log("piston window: locked %s, extension %s m" % (locked, extension))
    check(False, locked is True, "the part action window of the piston says it is locked")

    # 5. A short pitch: the reaction wheel sets the vessel spinning.
    call("set_controls", pitch=options.pitch)
    time.sleep(options.pitch_seconds)
    call("set_controls", pitch=None)
    most = 0.0
    still_locked = True
    deadline = time.time() + options.watch
    while time.time() < deadline:
        locked, extension = piston_window(piston["id"])
        still_locked = still_locked and locked is True
        if extension is not None:
            most = max(most, extension)
        time.sleep(0.2)
    call("screenshot", path=os.path.join(os.path.abspath(options.out), "loose-lock.png"), return_image=False)
    log("piston: %.2f m at most in %.0f s, %s m at the end" % (most, options.watch, extension))
    check(False, still_locked, "the part action window says it is locked all along")
    check(True, most < options.retracted,
          "the piston stays retracted while the vessel spins (%.2f m at most)" % most)

    for ok, text in checks:
        print("PASS" if ok else "FAIL", text)
    passed = sum(1 for ok, _ in checks if ok)
    print("%d of %d checks passed" % (passed, len(checks)))
    sys.exit(0 if passed == len(checks) else 1)


main()

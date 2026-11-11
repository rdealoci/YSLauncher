#!/usr/bin/env python3
import datetime
import os
import sys
import ds_store
from mac_alias import Alias, TargetInfo, VolumeInfo

def create_background_alias(volname, bg_rel_path):
    now = datetime.datetime.now(datetime.timezone.utc)
    a = Alias()
    a.appinfo = b"\x00\x00\x00\x00"
    a.version = 2

    a.volume = VolumeInfo(
        volname,
        now,
        b"H+",
        0,
        0,
        b"\x00\x00"
    )
    a.volume.posix_path = f"/Volumes/{volname}"

    clean_path = bg_rel_path.strip("/")
    parts = clean_path.split("/")
    filename = parts[-1]
    folder = parts[-2] if len(parts) > 1 else ""

    a.target = TargetInfo(
        1, # ALIAS_KIND_FILE
        filename,
        2,
        3,
        now,
        b"\x00\x00\x00\x00",
        b"\x00\x00\x00\x00"
    )
    if folder:
        a.target.folder_name = folder
    a.target.posix_path = f"/{clean_path}"
    a.target.carbon_path = f"{volname}:{clean_path.replace('/', ':')}"

    return a.to_bytes()

def main():
    if len(sys.argv) < 3:
        print("Usage: make_dmg_dsstore.py <mount_point> <volume_name>")
        sys.exit(1)

    mount_dir = sys.argv[1]
    volname = sys.argv[2]

    alias_data = create_background_alias(volname, ".background/background.png")

    icvp = {
        "viewOptionsVersion": 1,
        "backgroundType": 2,
        "backgroundImageAlias": alias_data,
        "backgroundColorRed": 1.0,
        "backgroundColorGreen": 1.0,
        "backgroundColorBlue": 1.0,
        "gridOffsetX": 0.0,
        "gridOffsetY": 0.0,
        "gridSpacing": 100.0,
        "arrangeBy": "none",
        "showIconPreview": False,
        "showItemInfo": False,
        "labelOnBottom": True,
        "textSize": 12.0,
        "iconSize": 128.0,
        "scrollPositionX": 0.0,
        "scrollPositionY": 0.0,
    }

    bwsp = {
        "ShowStatusBar": False,
        "WindowBounds": "{{140, 120}, {860, 512}}",
        "ContainerShowSidebar": False,
        "ShowPathbar": False,
        "ShowSidebar": False,
        "ShowToolbar": False,
        "ShowTabView": False
    }

    ds_path = os.path.join(mount_dir, ".DS_Store")
    if os.path.exists(ds_path):
        os.remove(ds_path)

    with ds_store.DSStore.open(ds_path, "w+") as d:
        d["."]["vSrn"] = ("long", 1)
        d["."]["bwsp"] = bwsp
        d["."]["icvp"] = icvp
        d["."]["icvl"] = (b"type", b"icnv")
        d[f"{volname}.app"]["Iloc"] = (172, 285)
        d["Applications"]["Iloc"] = (686, 285)

    print(f"Successfully generated styled .DS_Store for {volname} at {ds_path}")

if __name__ == "__main__":
    main()

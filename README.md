# ElectricityRealism — 7 Days to Die Mod

**Version:** 1.0.0  
**Compatible with:** 7 Days to Die v1.x / v2.x  
**Type:** Harmony (C# DLL) + XML modlet  
**EAC:** Requires EAC **off** (Harmony DLL)  
**Requires:** `0_TFP_Harmony` mod (ships with the game under `Mods/`)

---

## What this mod does

Makes three vanilla player-built workstations require **active electrical power** to open their crafting UI:

| Workstation       | Power draw | Why                               |
|-------------------|:----------:|-----------------------------------|
| Workbench         | **50 W**   | Motorised saws, drill press, welder |
| Cement Mixer      | **75 W**   | Large electric drum motor         |
| Chemistry Station | **25 W**   | Heating element + fluid pump      |

When a workstation is **not connected to a live power grid**, trying to open it shows a tooltip — *"Requires power — connect to an active electricity grid."* — and the crafting window stays closed. Items already inside are safe; they simply cannot be crafted until power is restored.

When wired to an active source with enough wattage the station functions **exactly like vanilla** — same recipes, same craft times, same UI.

---

## How it works

The mod uses three cooperating pieces:

### 1. `BlockPoweredWorkstation` (C#)
Subclasses the vanilla `BlockWorkstation`. The only additions are:
- Installs a `TileEntityPoweredWorkstation` when the block is placed, so it joins the wire/power grid.
- Overrides `GetActivationText` to show the "no power" hint when the block is unpowered.

### 2. `TileEntityPoweredWorkstation` (C#)
Subclasses `TileEntityPoweredBlock` (the same base used by blade traps, electric fences, etc.). Exposes an `IsReceivingPower` property that reads the vanilla `IsPowered` flag. Handles `read`/`write` for save/load and returns `true` from `IsTileEntitySavedInPrefab` so wiring survives prefab export.

### 3. Harmony patches (C#)
Three patches in `NicElectricityRealism`:

| Patch | Method | When |
|---|---|---|
| **Prefix** | `GameManager.OpenTileEntityUi` | Blocks the crafting window from opening when `IsReceivingPower` is false; shows tooltip |
| **Prefix** | `TileEntityPowered.CanHaveParent` | Ensures wires can be connected to the workstation |
| **Postfix** | `TileEntityPowered.InitializePowerData` | Pushes the XML `RequiredPower` value onto the `PowerItem` after every save-load |

### 4. `blocks.xml` (XML XPath patch)
Uses `<set>` to swap only the `Class` property of the three vanilla blocks to `ElectricityRealism.BlockPoweredWorkstation`, then `<append>` to add the `RequiredPower` property. Every other vanilla property (model, sounds, drops, materials, shape, recipes) is untouched.

---

## Project structure

```
ElectricityRealism/
├── ElectricityRealism.sln          ← Visual Studio 2022 solution
├── ElectricityRealism.csproj       ← .NET 4.8 class library project
├── packages.config                 ← NuGet package list (matches ElectricityLamps)
├── ModInfo.xml                     ← Mod metadata
├── Properties/
│   └── AssemblyInfo.cs
├── Harmony/
│   ├── NicElectricityRealism.cs    ← IModApi entry point + all Harmony patches
│   ├── BlockPoweredWorkstation.cs  ← Custom Block subclass
│   └── TileEntityPoweredWorkstation.cs ← Custom TileEntity subclass
└── Config/
    ├── blocks.xml                  ← XPath patch: swap Class + add RequiredPower
    └── Localization.txt            ← "no power" tooltip string
```

The compiled `ElectricityRealism.dll` goes into the mod root alongside `ModInfo.xml` when deploying.

---

## Building

1. Open `ElectricityRealism.sln` in Visual Studio 2022.
2. Restore NuGet packages (right-click solution → *Restore NuGet Packages*).
3. Verify the reference `HintPath` values in the `.csproj` point to your local 7DTD install.  
   Default: `C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die\`
4. Build → Release.
5. Copy `bin\Release\ElectricityRealism.dll` into the mod folder next to `ModInfo.xml`.

---

## Installation (mod folder layout)

```
Mods/
└── ElectricityRealism/
    ├── ModInfo.xml
    ├── ElectricityRealism.dll
    └── Config/
        ├── blocks.xml
        └── Localization.txt
```

- **Multiplayer / Dedicated servers:** install on both server and all clients.
- **Load order:** no special ordering needed. If you also use OCB Electricity Overhaul, rename this folder to `ZElectricityRealism` so it loads after.

---

## Compatibility

| Mod | Status |
|---|---|
| ElectricityLamps (same author) | ✅ Fully compatible — different blocks, shared pattern |
| OCB Electricity Overhaul | ✅ Compatible (rename to `ZElectricityRealism` if needed) |
| Any mod editing `workbench` / `cementMixer` / `chemistryStation` Class | ⚠️ Last-writer-wins on the `<set>` xpath; test load order |
| Darkness Falls / Undead Legacy | ❔ Untested; likely compatible as we only change the Class property |

---

## Uninstalling

1. Pick up all three workstations before removing the mod (they will drop the vanilla item).
2. Delete the `ElectricityRealism` folder from `Mods/`.
3. Re-place them — they will behave as vanilla again.

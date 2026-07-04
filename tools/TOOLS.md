# Database-generation tools

These scripts rebuild `db/tunnels.json` for a new game version or a different
DLC/map-mod setup. Regular users never need them — the release zip ships a
ready-made database.

## Pipeline

1. **Parse the map** with [truckermudgeon/maps](https://github.com/truckermudgeon/maps)
   (`packages/clis/parser`), patched to log two extra item types per sector
   (see *Parser patches* below):
   - `NW x z height width length rotation fogMaskPresetId` — **NoWeather** items,
     the game's "covered space" zones (they stop rain and switch the audio to
     closed-space reverb). These mark tunnel portals and short covered sections.
   - `CP x,z x,z ...` — CutPlane items (unused, logged for research).

   Run: `npx tsx packages/clis/parser/index.ts -i "<game folder>" -o <outdir> 2> parser.log`
   (needs Node ≥ 24 and a large heap: `NODE_OPTIONS=--max-old-space-size=16384`).

2. **Build the tunnel DB**: `node build-final.js`
   (edit the paths at the top: parser log + parser output dir).
   The algorithm:
   - every NoWeather zone becomes an oriented segment (HalfWidth = zone width/2 + 3);
   - portal-sized zones (area ≥ 800 m²) that are 40–550 m apart and connected by a
     road are bridged with a road-snapped polyline — that covers full tunnel
     interiors between portal zones;
   - Scandinavian mountain tunnels use `un*` road looks with the enclosed flag
     (rawFlags mask 0x5) instead of zones, and are added directly;
   - chains shorter than 200 m are dropped (realism rule: an overpass should not
     kill the radio; the threshold keeps every known real tunnel).
   The script self-tests against nine drive-verified reference points and writes
   `tunnels.json` + `tunnels.meta.json` (game version parsed from
   `Documents/Euro Truck Simulator 2/game.log.txt`).

3. **Optional map render**: `node inject-map2.js` builds an interactive HTML map
   of the DB over the road network (needs a `tunnel-map-template.html`).

`dump-schemes.ts` extracts the game's building-scheme definitions from the .scs
archives (used during research; kept for reference).

## Parser patches

Two small changes to `truckermudgeon/maps` (commit `36c274e`, the ETS2 1.57 era):

- `packages/clis/parser/game-files/sector-parser.ts` — after each sector decode,
  log NoWeather and CutPlane items with node-resolved coordinates
  (raw node pos = `[x*256, height*256, z*256]`).
- `packages/clis/parser/game-files/sector-parser.ts` (`toRoad`) — include
  `rawFlags` in the road output.

For game versions newer than ~1.58, use a newer commit of the parser and re-apply
the same two patches.

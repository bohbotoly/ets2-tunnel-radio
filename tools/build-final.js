// Tunnel DB builder v6.1 — NoWeather zones + road-snapped portal bridging + length rule.
// NoWeather items mark PORTALS/short covers, not interiors (A4: two zones 408m apart;
// German tunnel: two zones 166m apart; lone tiny zones = overpass blips).
// - every zone -> oriented segment (exactly v5, which passed all 9 refs)
// - PORTAL-sized zone pairs 40..550m apart connected by a road -> bridge polyline
//   (samples every 25m, each snapped to the nearest road point <=50m, so the bridge
//   follows curves and stays on the carriageway)
// - Tal's realism rule: chains with total extent < MIN_LEN don't kill the radio
const fs = require('fs');

const MIN_LEN = 200;       // min chain extent (m) for radio dropout (German ref tunnel ~230m must stay; overpass blips are 10-50m)
const PAIR_GAP = 550;      // max portal-pair distance to bridge (m)
const SAMPLE_TOL = 50;     // bridge samples must have a road within this (m)
const PORTAL_AREA = 1800;  // min zone area (m^2) to qualify as a bridgeable portal
                           // (real mouth slabs: >=2184; highway overpass covers: ~1000-1500)

const MAPOUT = process.env.ETR_MAPOUT || 'C:/Users/Tal/mapout160a';
const NWLOG = process.env.ETR_NWLOG || 'C:/Users/Tal/parser160a.log';
console.log('inputs:', MAPOUT, NWLOG);
console.log('loading nodes...');
const nm = new Map();
for (const n of JSON.parse(fs.readFileSync(MAPOUT + '/europe-nodes.json', 'utf8'))) nm.set(String(n.uid), n);
console.log('loading roads...');
const roads = JSON.parse(fs.readFileSync(MAPOUT + '/europe-roads.json', 'utf8'));
const rs = [];
for (const r of roads) {
  const a = nm.get(String(r.startNodeUid)), b = nm.get(String(r.endNodeUid));
  if (!a || !b) continue;
  // un* = underground road looks. Enclosed bores carry a BOTH-SIDES terrain-flag
  // pattern in the low nibble: {1,3}=0xA (modern reworks, e.g. 1.60 NL un02) or
  // {0,2}=0x5 (older Scandinavia style). Mixed/partial nibbles (0x9 etc.) are open
  // trenches (Dortmund sunken highway = un04/0x9) — excluded. Railway looks excluded.
  const lk = r.roadLookToken || '';
  const nib = (r.rawFlags >>> 0) & 0xF;
  rs.push({ ax: a.x, az: a.y, bx: b.x, bz: b.y, ha: a.z, hb: b.z, unNib: nib, un: /^un(?!rai|rdai)/i.test(lk) && (nib === 0xA || nib === 0x5) });
}
console.log('road segments:', rs.length);

const CELL = 150, grid = new Map();
rs.forEach((s, i) => {
  const k = Math.floor((s.ax + s.bx) / 2 / CELL) + '_' + Math.floor((s.az + s.bz) / 2 / CELL);
  if (!grid.has(k)) grid.set(k, []);
  grid.get(k).push(i);
});
function projSeg(px, pz, s) {
  const dx = s.bx - s.ax, dz = s.bz - s.az, l2 = dx * dx + dz * dz;
  let t = l2 ? ((px - s.ax) * dx + (pz - s.az) * dz) / l2 : 0; t = Math.max(0, Math.min(1, t));
  const cx = s.ax + t * dx, cz = s.az + t * dz;
  return { d: Math.hypot(px - cx, pz - cz), cx, cz };
}
function nearestRoadPt(px, pz, tol) {
  const cx = Math.floor(px / CELL), cz = Math.floor(pz / CELL);
  let best = null;
  for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) {
    const arr = grid.get((cx + dx) + '_' + (cz + dz)); if (!arr) continue;
    for (const i of arr) {
      const p = projSeg(px, pz, rs[i]);
      if (p.d <= tol && (!best || p.d < best.d)) best = p;
    }
  }
  return best;
}

const zones = [];
for (const ln of fs.readFileSync(NWLOG, 'utf8').split(/[\r\n]+/)) {
  const m = ln.match(/^NW (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) ([\d.]+) ([\d.]+) (-?[\d.]+) (-?\d+)$/);
  if (m) zones.push({ x: +m[1], z: +m[2], h: +m[3], w: +m[4], len: +m[5], rot: +m[6] });
}
console.log('zones:', zones.length);

const out = [];
// 1) zone -> oriented segment (v5 representation); `big` marks portal-strength evidence
for (const zn of zones) {
  const dx = Math.cos(zn.rot), dz = Math.sin(zn.rot);
  out.push({
    Ax: zn.x - dx * zn.len / 2, Az: zn.z - dz * zn.len / 2,
    Bx: zn.x + dx * zn.len / 2, Bz: zn.z + dz * zn.len / 2,
    HalfWidth: Math.max(zn.w / 2 + 3, 9),
    big: zn.w * zn.len >= PORTAL_AREA,
  });
}

// 2) road-snapped bridges between portal-sized zone pairs
const portals = zones.filter(zn => zn.w * zn.len >= PORTAL_AREA);
console.log('portal-sized zones:', portals.length);
const pGrid = new Map();
portals.forEach((zn, i) => {
  const k = Math.floor(zn.x / PAIR_GAP) + '_' + Math.floor(zn.z / PAIR_GAP);
  if (!pGrid.has(k)) pGrid.set(k, []);
  pGrid.get(k).push(i);
});
let bridges = 0;
const donePair = new Set();
for (let a = 0; a < portals.length; a++) {
  const za = portals[a];
  const cx = Math.floor(za.x / PAIR_GAP), cz = Math.floor(za.z / PAIR_GAP);
  for (let gx = -1; gx <= 1; gx++) for (let gz = -1; gz <= 1; gz++) {
    const arr = pGrid.get((cx + gx) + '_' + (cz + gz)); if (!arr) continue;
    for (const b of arr) {
      if (b <= a) continue;
      const key = a + '_' + b;
      if (donePair.has(key)) continue;
      donePair.add(key);
      const zb = portals[b];
      const gap = Math.hypot(za.x - zb.x, za.z - zb.z);
      // big mouth slabs (long bores, e.g. 1.60 NL dip tunnels) may pair across up to 1.4 km
      const maxGap = (za.w * za.len >= 2500 && zb.w * zb.len >= 2500) ? 1400 : PAIR_GAP;
      if (gap < 40 || gap > maxGap) continue;
      const steps = Math.ceil(gap / 25);
      const path = [];
      let ok = true;
      for (let s2 = 0; s2 <= steps; s2++) {
        const px = za.x + (zb.x - za.x) * s2 / steps, pz = za.z + (zb.z - za.z) * s2 / steps;
        const hit = nearestRoadPt(px, pz, SAMPLE_TOL);
        if (!hit) { ok = false; break; }
        path.push([hit.cx, hit.cz]);
      }
      if (!ok) continue;
      bridges++;
      const hw = Math.max(13, Math.min(za.w, zb.w) / 2 + 3);
      for (let p = 1; p < path.length; p++) {
        if (Math.hypot(path[p][0] - path[p-1][0], path[p][1] - path[p-1][1]) < 1) continue;
        out.push({ Ax: path[p-1][0], Az: path[p-1][1], Bx: path[p][0], Bz: path[p][1], HalfWidth: hw, big: true });
      }
    }
  }
}
console.log('bridged portal pairs:', bridges, '| segments so far:', out.length);

// 3) un-look enclosed roads. Old-style bores ({0,2} = nibble 0x5, Scandinavia) are
// portal-delimited by construction — trusted standalone. Modern reworks ({1,3} = 0xA,
// e.g. 1.60 NL) extend the SAME look+flags onto open approach ramps, so 0xA segments
// only count where the ZONE evidence agrees: within 35 m of the zone/bridge coverage
// built above (mouth slabs + portal-pair corridors). The elevated A15 approach sits
// beyond the mouth slab -> outside every corridor -> excluded.
const covGrid = new Map();
out.forEach((t, i) => {
  if (!t.big) return;   // only portal-strength evidence gates modern un segments
  const k = Math.floor((t.Ax + t.Bx) / 2 / 200) + '_' + Math.floor((t.Az + t.Bz) / 2 / 200);
  if (!covGrid.has(k)) covGrid.set(k, []);
  covGrid.get(k).push(i);
});
function ptSeg2(px, pz, t) {
  const dx = t.Bx - t.Ax, dz = t.Bz - t.Az, l2 = dx * dx + dz * dz;
  let u = l2 ? ((px - t.Ax) * dx + (pz - t.Az) * dz) / l2 : 0; u = Math.max(0, Math.min(1, u));
  return Math.hypot(px - (t.Ax + u * dx), pz - (t.Az + u * dz));
}
function nearCoverage(s) {
  const mx = (s.ax + s.bx) / 2, mz = (s.az + s.bz) / 2;
  const cx = Math.floor(mx / 200), cz = Math.floor(mz / 200);
  for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) {
    const arr = covGrid.get((cx + dx) + '_' + (cz + dz)); if (!arr) continue;
    for (const i of arr) {
      const t = out[i];
      if (ptSeg2(s.ax, s.az, t) < 35 || ptSeg2(s.bx, s.bz, t) < 35 || ptSeg2(mx, mz, t) < 35) return true;
    }
  }
  return false;
}
let un = 0, unSkipped = 0;
for (const s of rs) {
  if (!s.un) continue;
  if (s.unNib === 0xA && !nearCoverage(s)) { unSkipped++; continue; }
  out.push({ Ax: s.ax, Az: s.az, Bx: s.bx, Bz: s.bz, HalfWidth: 13 });
  un++;
}
console.log('un-enclosed roads:', un, '(0xA segs without zone evidence skipped:', unSkipped + ')');

// 4) chain + length filter (Tal's rule: short covers don't cut radio)
const par = out.map((_, i) => i);
const find = i => { while (par[i] !== i) { par[i] = par[par[i]]; i = par[i]; } return i; };
function ptSeg2(px, pz, t) {
  const dx = t.Bx - t.Ax, dz = t.Bz - t.Az, l2 = dx * dx + dz * dz;
  let u = l2 ? ((px - t.Ax) * dx + (pz - t.Az) * dz) / l2 : 0; u = Math.max(0, Math.min(1, u));
  return Math.hypot(px - (t.Ax + u * dx), pz - (t.Az + u * dz));
}
function ss(p, q) {
  return Math.min(ptSeg2(p.Ax, p.Az, q), ptSeg2(p.Bx, p.Bz, q), ptSeg2(q.Ax, q.Az, p), ptSeg2(q.Bx, q.Bz, p));
}
const g2 = new Map();
out.forEach((s, i) => {
  const k = Math.floor((s.Ax + s.Bx) / 2 / 200) + '_' + Math.floor((s.Az + s.Bz) / 2 / 200);
  if (!g2.has(k)) g2.set(k, []);
  g2.get(k).push(i);
});
for (const [k, arr] of g2) {
  const [cx, cz] = k.split('_').map(Number);
  for (let dx = 0; dx <= 1; dx++) for (let dz = (dx === 0 ? 0 : -1); dz <= 1; dz++) {
    const nb = g2.get((cx + dx) + '_' + (cz + dz)); if (!nb) continue;
    for (const i of arr) for (const j of nb) {
      if (i >= j && dx === 0 && dz === 0) continue;
      if (find(i) !== find(j) && ss(out[i], out[j]) < 40) par[find(i)] = find(j);
    }
  }
}
const box = new Map();
out.forEach((s, i) => {
  const r = find(i);
  let b = box.get(r); if (!b) { b = { minx: 1e18, maxx: -1e18, minz: 1e18, maxz: -1e18 }; box.set(r, b); }
  b.minx = Math.min(b.minx, s.Ax, s.Bx); b.maxx = Math.max(b.maxx, s.Ax, s.Bx);
  b.minz = Math.min(b.minz, s.Az, s.Bz); b.maxz = Math.max(b.maxz, s.Az, s.Bz);
});
const final = [];
let dropped = 0;
out.forEach((s, i) => {
  const b = box.get(find(i));
  if (Math.hypot(b.maxx - b.minx, b.maxz - b.minz) < MIN_LEN) { dropped++; return; }
  final.push({ Name: 't' + final.length, Ax: s.Ax, Az: s.Az, Bx: s.Bx, Bz: s.Bz, HalfWidth: s.HalfWidth });
});
console.log('length filter dropped', dropped, 'segs | final:', final.length);
fs.writeFileSync('C:/Users/Tal/tunnels.json', JSON.stringify(final, null, 0));
// version stamp: the game logs "Loaded pack set version <ver>" on every start
let gameVer = 'unknown';
try {
  const gl = fs.readFileSync(process.env.USERPROFILE + '/Documents/Euro Truck Simulator 2/game.log.txt', 'utf8');
  const vm = gl.match(/Loaded pack set version ([\d.]+)/);
  if (vm) gameVer = vm[1];
} catch {}
const OUTTAG = process.env.ETR_OUTTAG || '';   // '' = vanilla, 'promods', 'promods-me'
const meta = JSON.stringify({
  gameVersion: gameVer,
  mapTag: OUTTAG,
  map: process.env.ETR_MAPDESC || 'ETS2 + map DLCs (vanilla)',
  generator: 'v6.4 noweather-portal-bridge',
  segments: final.length,
}, null, 2);
fs.writeFileSync('C:/Users/Tal/tunnels.meta.json', meta);
// versioned (and map-tagged) copies so the app can hot-pick per game version + map mods
const mm = /^(\d+\.\d+)/.exec(gameVer);
if (mm) {
  const suffix = OUTTAG ? `${mm[1]}-${OUTTAG}` : mm[1];
  fs.writeFileSync(`C:/Users/Tal/tunnels-${suffix}.json`, JSON.stringify(final, null, 0));
  fs.writeFileSync(`C:/Users/Tal/tunnels-${suffix}.meta.json`, meta);
  console.log('also wrote tunnels-' + suffix + '.json');
}
console.log('wrote', final.length, 'tunnel segments (game', gameVer, OUTTAG || 'vanilla', ')');

function chk(name, x, z, want) {
  let cov = false, best = 1e9;
  for (const t of final) {
    const d = ptSeg2(x, z, t);
    if (d <= t.HalfWidth + 6) cov = true;
    if (d < best) best = d;
  }
  console.log('  ' + name + ': ' + best.toFixed(0) + 'm ' + (cov ? 'COVERED' : 'clear') + ' ' + (cov === want ? 'OK' : '*** WRONG ***'));
}
// Reference set for VANILLA 1.60 + map DLCs (Nordic Horizons points removed — that
// territory doesn't exist without the mod; 1.57 refs kept as observations since the
// 1.60 map rework may have moved things).
chk('NL tunnel 1.60 (drive-tested)', -21005, -9453, true);
chk('Kiruna tunnel (NH DLC)', 26908, -97826, true);
chk('Kiruna portal exit (open sky)', 27006, -97900, false);
chk('A15 elevated approach (open sky)', -20863, -8662, false);
chk('A73 Venlo overpasses (open sky)', -15454, -6562, false);
chk('German tunnel (1.57 ref)', 10235, -9226, true);
// retired: A4 landtunnel 1.57 ref (-19976,-10874) — the 1.60 NL rework relocated it
chk('Dortmund barrier (open sky)', -16784, -10825, false);
chk('Lodz LKW street (open sky)', 31300, -6198, false);
chk('Lodz east street (open sky)', 32191, -6003, false);
chk('Osnabrueck A1 (open sky)', -10168, -10445, false);

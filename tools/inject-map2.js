const fs = require('fs');
const S = 'C:/Users/Tal/AppData/Local/Temp/claude/C--Users-Tal/d192690d-c755-4b54-aad1-261b68886e89/scratchpad/';
const M = 'C:/Users/Tal/mapout157e/';

console.log('loading nodes...');
const nm = new Map();
for (const n of JSON.parse(fs.readFileSync(M + 'europe-nodes.json', 'utf8'))) nm.set(String(n.uid), n);
console.log('nodes:', nm.size);

console.log('resolving roads...');
const R = [];
for (const r of JSON.parse(fs.readFileSync(M + 'europe-roads.json', 'utf8'))) {
  const a = nm.get(String(r.startNodeUid)), b = nm.get(String(r.endNodeUid));
  if (!a || !b) continue;
  R.push(Math.round(a.x), Math.round(a.y), Math.round(b.x), Math.round(b.y));
}

const T = [];
for (const t of JSON.parse(fs.readFileSync('C:/Users/Tal/tunnels.json', 'utf8')))
  T.push(Math.round(t.Ax), Math.round(t.Az), Math.round(t.Bx), Math.round(t.Bz));

const seen = new Set(), C = [];
for (const c of JSON.parse(fs.readFileSync(M + 'europe-cities.json', 'utf8'))) {
  if (!c || !c.name || c.x == null || c.y == null) continue;
  const k = c.name + '|' + Math.round(c.x / 50); if (seen.has(k)) continue; seen.add(k);
  C.push([Math.round(c.x), Math.round(c.y), c.name, c.population || 0]);
}

// distinct tunnel count: union segments whose endpoints come within 25m (same bore/complex)
const tn = T.length / 4;
const par = Array.from({ length: tn }, (_, i) => i);
const find = i => { while (par[i] !== i) { par[i] = par[par[i]]; i = par[i]; } return i; };
const tg = new Map();
for (let i = 0; i < tn; i++) {
  for (const [px, pz] of [[T[i*4], T[i*4+1]], [T[i*4+2], T[i*4+3]]]) {
    const k = Math.floor(px / 200) + '_' + Math.floor(pz / 200);
    if (!tg.has(k)) tg.set(k, []); tg.get(k).push(i);
  }
}
for (const arr of tg.values()) {
  for (let a = 0; a < arr.length; a++) for (let b = a + 1; b < arr.length; b++) {
    const i = arr[a], j = arr[b];
    if (find(i) === find(j)) continue;
    let close = false;
    for (const p of [[T[i*4], T[i*4+1]], [T[i*4+2], T[i*4+3]]]) {
      for (const q of [[T[j*4], T[j*4+1]], [T[j*4+2], T[j*4+3]]])
        if (Math.hypot(p[0]-q[0], p[1]-q[1]) < 25) { close = true; break; }
      if (close) break;
    }
    if (close) par[find(i)] = find(j);
  }
}
const tunnelCount = new Set(Array.from({ length: tn }, (_, i) => find(i))).size;
console.log('distinct tunnels:', tunnelCount);

const DATA = { roads: R, tunnels: T, cities: C, home: [26908, -97826], tunnelCount };
const tpl = fs.readFileSync(S + 'tunnel-map-template.html', 'utf8');
fs.writeFileSync(S + 'tunnel-map.html', tpl.replace('__DATA__', JSON.stringify(DATA)));
console.log('roads:', R.length / 4, 'tunnels:', T.length / 4, 'cities:', C.length,
  'MB:', (fs.statSync(S + 'tunnel-map.html').size / 1048576).toFixed(1));

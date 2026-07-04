import * as fs from 'fs';
import * as path from 'path';
import { ScsArchive } from './packages/clis/parser/game-files/scs-archive';
import { CombinedEntries } from './packages/clis/parser/game-files/combined-entries';

const gameDir = 'C:/Program Files (x86)/Euro Truck Simulator 2 Nordic Horizons';
const archives = fs
  .readdirSync(gameDir)
  .filter(f => f.endsWith('.scs'))
  .map(f => new ScsArchive(path.join(gameDir, f)));
const entries = new CombinedEntries(archives);

const dir = entries.directories.get('def/world');
if (!dir) throw new Error('no def/world');
const bldFiles = dir.files.filter(f => /building/i.test(f));
console.log('building def files:', bldFiles.join(', '));

for (const f of bldFiles) {
  const fe = entries.files.get('def/world/' + f);
  if (!fe) continue;
  const txt = fe.read().toString('utf8');
  fs.writeFileSync('C:/Users/Tal/bld-defs/' + f, txt);
  console.log('dumped', f, txt.length, 'chars');
}

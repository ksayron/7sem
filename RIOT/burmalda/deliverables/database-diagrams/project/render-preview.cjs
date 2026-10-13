const fs = require('node:fs');
const path = require('node:path');
const sharp = require('C:/Users/user/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/sharp');

async function main() {
  const html = fs.readFileSync(path.join(__dirname, 'burmalda-all-databases.html'), 'utf8');
  // Native exporter omits the semicolon in some numeric entities (e.g. an em
  // dash in the title). Normalize XML syntax before SVG rasterization.
  const diagrams = [...html.matchAll(/<svg xmlns[\s\S]*?<\/svg>/g)]
    .map(m => m[0].replace(/&#(\d+)(?=[^\d;])/g, '&#$1;'));
  if (diagrams.length !== 9) throw new Error(`Expected 9 diagrams, found ${diagrams.length}`);
  const names = ['burmalda-overview', 'burmalda-complete', 'identity', 'catalog', 'economy',
                 'reputation', 'notifications', 'concurrency-lab', 'lending'];
  for (let i = 0; i < diagrams.length; i++) {
    fs.writeFileSync(path.join(__dirname, names[i] + '.svg'), diagrams[i]);
    if (i < 2) {
      await sharp(Buffer.from(diagrams[i])).flatten({ background: '#fff' })
        .png().toFile(path.join(__dirname, names[i] + '.png'));
    }
  }
  console.log('Rendered overview and complete PNGs; saved all nine SVG diagrams.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });

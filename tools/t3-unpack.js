// Unpacks T3 Code's bundled app so its code can be read and copied (see "Copy, don't invent" in HANDOFF.md).
//
//   node tools/t3-unpack.js <archive.asar> <out-folder> [path-regex]
//
// T3 Code is installed at %LOCALAPPDATA%\Programs\t3code. Its UI is in resources\server.asar, under
// apps/server/dist/client/assets (minified React); its server, with the provider adapters, is in
// apps/server/dist/*.mjs. For example:
//
//   node tools/t3-unpack.js "%LOCALAPPDATA%\Programs\t3code\resources\server.asar" <scratch>\t3 "^(?!node_modules/)"
//
// Unpack into a scratch folder, never into the repo.
const fs = require('fs');
const path = require('path');

const [, , archive, out, filter] = process.argv;
if (!archive || !out) {
  console.error('usage: node t3-unpack.js <archive.asar> <out-folder> [path-regex]');
  process.exit(1);
}

// An asar archive is a pickled JSON header (sizes at bytes 4 and 12) followed by the files' bytes.
const fd = fs.openSync(archive, 'r');
const head = Buffer.alloc(16);
fs.readSync(fd, head, 0, 16, 0);
const headerSize = head.readUInt32LE(4);
const jsonLength = head.readUInt32LE(12);
const json = Buffer.alloc(jsonLength);
fs.readSync(fd, json, 0, jsonLength, 16);
const header = JSON.parse(json.toString());
const base = 8 + headerSize;
const wanted = filter ? new RegExp(filter) : null;
const root = path.resolve(out);
let count = 0;

(function walk(node, relative) {
  for (const [name, entry] of Object.entries(node.files || {})) {
    const inner = path.join(relative, name);
    if (entry.files) {
      walk(entry, inner);
      continue;
    }
    if (entry.unpacked || entry.link) continue;
    if (wanted && !wanted.test(inner.split(path.sep).join('/'))) continue;
    const target = path.join(root, inner);
    if (!path.resolve(target).startsWith(root)) continue; // Nothing outside the out folder.
    fs.mkdirSync(path.dirname(target), { recursive: true });
    const bytes = Buffer.alloc(Number(entry.size));
    fs.readSync(fd, bytes, 0, bytes.length, base + Number(entry.offset));
    fs.writeFileSync(target, bytes);
    count++;
  }
})(header, '');
console.log(`unpacked ${count} files into ${root}`);

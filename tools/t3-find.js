// Finds text in T3 Code's unpacked bundles and prints what's around each match. grep is slow on
// these megabyte-long single lines, so this reads them with Node.
//
//   node tools/t3-find.js <assets-folder> <regex> [chars-before] [chars-after] [most-matches]
//
// For example, to find the code behind a label seen in T3:
//
//   node tools/t3-find.js <scratch>\t3\apps\server\dist\client\assets "Fork from this response" 2000 800
const fs = require('fs');
const path = require('path');

const [, , folder, pattern, before = '300', after = '300', most = '6'] = process.argv;
if (!folder || !pattern) {
  console.error('usage: node t3-find.js <assets-folder> <regex> [chars-before] [chars-after] [most-matches]');
  process.exit(1);
}
const finding = new RegExp(pattern, 'g');
let shown = 0;
for (const file of fs.readdirSync(folder).filter((name) => /\.m?js$/.test(name))) {
  const text = fs.readFileSync(path.join(folder, file), 'utf8');
  finding.lastIndex = 0;
  let match;
  while ((match = finding.exec(text)) && shown < Number(most)) {
    console.log(`== ${file} @${match.index}`);
    console.log(text.slice(Math.max(0, match.index - Number(before)), match.index + match[0].length + Number(after)));
    console.log();
    shown++;
  }
}

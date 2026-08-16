const fs = require('fs');
const { execSync } = require('child_process');

const srcRoot = 'C:\\Users\\Tarik\\Desktop\\lurp';
const files = execSync(`rg -l "EdgeKind\\." -g "*.cs" "${srcRoot}\\src" "${srcRoot}\\tests"`, { encoding: 'utf8' }).trim().split('\n');

let totalReplacements = 0;
for (const file of files) {
  const filePath = file.trim();
  if (!filePath) continue;
  let content = fs.readFileSync(filePath, 'utf8');
  const original = content;

  content = content.replace(/EdgeKind\.([A-Za-z_]+)\.ToString\(\)/g, (match, name) => {
    totalReplacements++;
    return `nameof(EdgeKind.${name})`;
  });

  if (content !== original) {
    fs.writeFileSync(filePath, content, 'utf8');
    console.log('Modified:', filePath);
  }
}
console.log('Total replacements:', totalReplacements);

const fs = require('fs');
const { execSync } = require('child_process');

const srcRoot = 'C:\\Users\\Tarik\\Desktop\\lurp';

// Get all .cs files that might have collection creation patterns
const files = execSync(`rg -l "new (List|Dictionary|HashSet|Array)\\b|Array\\.Empty|new\\[" -g "*.cs" "${srcRoot}\\src" "${srcRoot}\\tests"`, { encoding: 'utf8' }).trim().split('\n');

let totalReplacements = 0;
for (const file of files) {
  const filePath = file.trim();
  if (!filePath) continue;
  let content = fs.readFileSync(filePath, 'utf8');
  const original = content;

  // Pattern: new List<T>() with no initializer -> []
  // Only match when it's standalone: = new List<T>(); or similar
  content = content.replace(/=\s*new\s+List<[^>]+>\(\);/g, (match) => {
    totalReplacements++;
    return match.replace(/new\s+List<[^>]+>\(\)/, '[]');
  });

  // Pattern: new T[0] -> []
  content = content.replace(/=\s*new\s+\w+\[0\];/g, (match) => {
    totalReplacements++;
    return match.replace(/new\s+\w+\[0\]/, '[]');
  });

  // Pattern: Array.Empty<T>() -> []
  content = content.replace(/Array\.Empty<[^>]+>\(\)/g, (match) => {
    totalReplacements++;
    return '[]';
  });

  if (content !== original) {
    fs.writeFileSync(filePath, content, 'utf8');
    console.log('Modified:', filePath);
  }
}
console.log('Total replacements:', totalReplacements);

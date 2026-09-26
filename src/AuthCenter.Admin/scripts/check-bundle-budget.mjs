// Fails the build when the console's JavaScript grows past its budget (gzip sizes, the transfer
// cost). The entry chunk loads on every page; route chunks load on demand.
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { gzipSync } from "node:zlib";

const budgets = {
  entryKb: 110,
  chunkKb: 45,
  totalKb: 420
};

const assets = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../dist/assets");
const files = readdirSync(assets).filter((file) => file.endsWith(".js"));
if (files.length === 0) throw new Error(`No JavaScript in ${assets}: run npm run build first.`);

const sizes = files.map((file) => ({ file, kb: gzipSync(readFileSync(path.join(assets, file))).length / 1024 })).sort((a, b) => b.kb - a.kb);
const entry = sizes.find((item) => /^index-.*\.js$/.test(item.file));
const problems = [];
if (!entry) problems.push("The entry chunk (index-*.js) was not found.");
else if (entry.kb > budgets.entryKb) problems.push(`Entry ${entry.file}: ${entry.kb.toFixed(1)} KB gzip > ${budgets.entryKb} KB.`);
for (const item of sizes.filter((item) => item !== entry && item.kb > budgets.chunkKb))
  problems.push(`Chunk ${item.file}: ${item.kb.toFixed(1)} KB gzip > ${budgets.chunkKb} KB.`);
const total = sizes.reduce((sum, item) => sum + item.kb, 0);
if (total > budgets.totalKb) problems.push(`All JavaScript: ${total.toFixed(1)} KB gzip > ${budgets.totalKb} KB.`);

console.log(`Console bundle: entry ${entry?.kb.toFixed(1) ?? "?"} KB, largest chunk ${sizes.find((item) => item !== entry)?.kb.toFixed(1) ?? "0"} KB, total ${total.toFixed(1)} KB (gzip, ${files.length} files).`);
if (problems.length) {
  console.error(problems.join("\n"));
  console.error("Split the code (lazy routes, lighter dependencies) or raise the budget in scripts/check-bundle-budget.mjs with a reason.");
  process.exit(1);
}

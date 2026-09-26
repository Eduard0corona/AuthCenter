// QR Code (ISO/IEC 18004) encoder for the hosted pages: byte mode, error correction level M.
// It lets the login and the portal show the authenticator enrollment URI as a scannable code
// without loading a third-party script (the pages' CSP only allows their own scripts).

// Error correction codewords per block and number of blocks, indexed by version (level M).
const ECC_CODEWORDS_PER_BLOCK = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28];
const ERROR_CORRECTION_BLOCKS = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49];
const FORMAT_BITS_LEVEL_M = 0;

/** Returns the modules of the QR code for the text: `modules[y][x]` is true for a dark module. */
export function qrModules(text) {
  const bytes = new TextEncoder().encode(String(text));
  let version = 1;
  for (; version <= 40; version++) {
    if (4 + countBits(version) + bytes.length * 8 <= dataCodewords(version) * 8) break;
  }
  if (version > 40) throw new RangeError("The text is too long for a QR code.");

  const codewords = addErrorCorrection(encodeData(bytes, version), version);
  const size = version * 4 + 17;
  const modules = Array.from({ length: size }, () => new Array(size).fill(false));
  const reserved = Array.from({ length: size }, () => new Array(size).fill(false));
  const set = (x, y, dark) => { modules[y][x] = dark; reserved[y][x] = true; };

  drawFunctionPatterns(version, size, set);
  drawCodewords(codewords, size, modules, reserved);

  let bestMask = 0;
  let bestPenalty = Infinity;
  for (let mask = 0; mask < 8; mask++) {
    applyMask(mask, size, modules, reserved);
    drawFormatBits(mask, size, set);
    const penalty = penaltyScore(size, modules);
    if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
    applyMask(mask, size, modules, reserved);
  }
  applyMask(bestMask, size, modules, reserved);
  drawFormatBits(bestMask, size, set);
  return { size, modules };
}

/** An SVG element (4-module quiet zone) for the text, labelled for assistive technology. */
export function qrSvg(text, label) {
  const { size, modules } = qrModules(text);
  const namespace = "http://www.w3.org/2000/svg";
  const total = size + 8;
  const svg = document.createElementNS(namespace, "svg");
  svg.setAttribute("viewBox", `0 0 ${total} ${total}`);
  svg.setAttribute("role", "img");
  svg.setAttribute("aria-label", label);
  svg.setAttribute("shape-rendering", "crispEdges");
  const background = document.createElementNS(namespace, "rect");
  background.setAttribute("width", String(total));
  background.setAttribute("height", String(total));
  background.setAttribute("fill", "#ffffff");
  let path = "";
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (modules[y][x]) path += `M${x + 4},${y + 4}h1v1h-1z`;
    }
  }
  const dark = document.createElementNS(namespace, "path");
  dark.setAttribute("d", path);
  dark.setAttribute("fill", "#000000");
  svg.append(background, dark);
  return svg;
}

function countBits(version) {
  return version < 10 ? 8 : 16;
}

function rawDataModules(version) {
  let result = (16 * version + 128) * version + 64;
  if (version >= 2) {
    const alignments = Math.floor(version / 7) + 2;
    result -= (25 * alignments - 10) * alignments - 55;
    if (version >= 7) result -= 36;
  }
  return result;
}

function dataCodewords(version) {
  return Math.floor(rawDataModules(version) / 8) - ECC_CODEWORDS_PER_BLOCK[version] * ERROR_CORRECTION_BLOCKS[version];
}

function encodeData(bytes, version) {
  const bits = [];
  const append = (value, length) => { for (let i = length - 1; i >= 0; i--) bits.push((value >>> i) & 1); };
  append(0b0100, 4);
  append(bytes.length, countBits(version));
  for (const value of bytes) append(value, 8);
  const capacity = dataCodewords(version) * 8;
  append(0, Math.min(4, capacity - bits.length));
  append(0, (8 - (bits.length % 8)) % 8);
  const result = [];
  for (let i = 0; i < bits.length; i += 8) result.push(bits.slice(i, i + 8).reduce((byte, bit) => (byte << 1) | bit, 0));
  for (let pad = 0xec; result.length < capacity / 8; pad ^= 0xec ^ 0x11) result.push(pad);
  return result;
}

function addErrorCorrection(data, version) {
  const blockCount = ERROR_CORRECTION_BLOCKS[version];
  const eccLength = ECC_CODEWORDS_PER_BLOCK[version];
  const rawCodewords = Math.floor(rawDataModules(version) / 8);
  const shortBlocks = blockCount - (rawCodewords % blockCount);
  const shortBlockLength = Math.floor(rawCodewords / blockCount);
  const divisor = reedSolomonDivisor(eccLength);
  const blocks = [];
  for (let i = 0, offset = 0; i < blockCount; i++) {
    const block = data.slice(offset, offset + shortBlockLength - eccLength + (i < shortBlocks ? 0 : 1));
    offset += block.length;
    const ecc = reedSolomonRemainder(block, divisor);
    if (i < shortBlocks) block.push(0);
    blocks.push(block.concat(ecc));
  }
  // Interleave the blocks, skipping the padding byte of the short ones.
  const result = [];
  for (let i = 0; i < blocks[0].length; i++) {
    blocks.forEach((block, j) => {
      if (i !== shortBlockLength - eccLength || j >= shortBlocks) result.push(block[i]);
    });
  }
  return result;
}

function reedSolomonDivisor(degree) {
  const result = new Array(degree - 1).fill(0).concat([1]);
  let root = 1;
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < result.length; j++) {
      result[j] = multiply(result[j], root);
      if (j + 1 < result.length) result[j] ^= result[j + 1];
    }
    root = multiply(root, 0x02);
  }
  return result;
}

function reedSolomonRemainder(data, divisor) {
  const result = divisor.map(() => 0);
  for (const value of data) {
    const factor = value ^ result.shift();
    result.push(0);
    divisor.forEach((coefficient, i) => { result[i] ^= multiply(coefficient, factor); });
  }
  return result;
}

// Multiplication in GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1.
function multiply(x, y) {
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z;
}

function drawFunctionPatterns(version, size, set) {
  for (let i = 0; i < size; i++) {
    set(6, i, i % 2 === 0);
    set(i, 6, i % 2 === 0);
  }
  for (const [x, y] of [[3, 3], [size - 4, 3], [3, size - 4]]) {
    for (let dy = -4; dy <= 4; dy++) {
      for (let dx = -4; dx <= 4; dx++) {
        const distance = Math.max(Math.abs(dx), Math.abs(dy));
        if (x + dx >= 0 && x + dx < size && y + dy >= 0 && y + dy < size) set(x + dx, y + dy, distance !== 2 && distance !== 4);
      }
    }
  }
  const positions = alignmentPositions(version, size);
  const last = positions.length - 1;
  positions.forEach((y, i) => positions.forEach((x, j) => {
    if ((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) return;
    for (let dy = -2; dy <= 2; dy++) {
      for (let dx = -2; dx <= 2; dx++) set(x + dx, y + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }
  }));
  drawFormatBits(0, size, set);
  if (version >= 7) {
    let remainder = version;
    for (let i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >>> 11) * 0x1f25);
    const bits = (version << 12) | remainder;
    for (let i = 0; i < 18; i++) {
      const dark = ((bits >>> i) & 1) !== 0;
      const a = size - 11 + (i % 3);
      const b = Math.floor(i / 3);
      set(a, b, dark);
      set(b, a, dark);
    }
  }
}

function alignmentPositions(version, size) {
  if (version === 1) return [];
  const count = Math.floor(version / 7) + 2;
  const step = version === 32 ? 26 : Math.ceil((version * 4 + 4) / (count * 2 - 2)) * 2;
  const result = [6];
  for (let position = size - 7; result.length < count; position -= step) result.splice(1, 0, position);
  return result;
}

function drawFormatBits(mask, size, set) {
  const data = (FORMAT_BITS_LEVEL_M << 3) | mask;
  let remainder = data;
  for (let i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >>> 9) * 0x537);
  const bits = ((data << 10) | remainder) ^ 0x5412;
  const bit = i => ((bits >>> i) & 1) !== 0;
  for (let i = 0; i <= 5; i++) set(8, i, bit(i));
  set(8, 7, bit(6));
  set(8, 8, bit(7));
  set(7, 8, bit(8));
  for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
  for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
  for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
  set(8, size - 8, true);
}

function drawCodewords(codewords, size, modules, reserved) {
  let index = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vertical = 0; vertical < size; vertical++) {
      for (let j = 0; j < 2; j++) {
        const x = right - j;
        const upward = ((right + 1) & 2) === 0;
        const y = upward ? size - 1 - vertical : vertical;
        if (!reserved[y][x] && index < codewords.length * 8) {
          modules[y][x] = ((codewords[index >>> 3] >>> (7 - (index & 7))) & 1) !== 0;
          index++;
        }
      }
    }
  }
}

function applyMask(mask, size, modules, reserved) {
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (reserved[y][x]) continue;
      let invert;
      switch (mask) {
        case 0: invert = (x + y) % 2 === 0; break;
        case 1: invert = y % 2 === 0; break;
        case 2: invert = x % 3 === 0; break;
        case 3: invert = (x + y) % 3 === 0; break;
        case 4: invert = (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0; break;
        case 5: invert = ((x * y) % 2) + ((x * y) % 3) === 0; break;
        case 6: invert = (((x * y) % 2) + ((x * y) % 3)) % 2 === 0; break;
        default: invert = (((x + y) % 2) + ((x * y) % 3)) % 2 === 0; break;
      }
      if (invert) modules[y][x] = !modules[y][x];
    }
  }
}

// Penalty rules of the standard: runs, 2x2 blocks, finder-like sequences and dark balance.
function penaltyScore(size, modules) {
  let penalty = 0;
  const line = (get) => {
    let score = 0;
    let run = 1;
    for (let i = 1; i <= size; i++) {
      if (i < size && get(i) === get(i - 1)) { run++; continue; }
      if (run >= 5) score += 3 + (run - 5);
      run = 1;
    }
    for (let i = 0; i + 11 <= size; i++) {
      const window = Array.from({ length: 11 }, (_, k) => (get(i + k) ? 1 : 0)).join("");
      if (window === "10111010000" || window === "00001011101") score += 40;
    }
    return score;
  };
  for (let y = 0; y < size; y++) penalty += line(x => modules[y][x]);
  for (let x = 0; x < size; x++) penalty += line(y => modules[y][x]);
  let dark = 0;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (modules[y][x]) dark++;
      if (x + 1 < size && y + 1 < size) {
        const color = modules[y][x];
        if (color === modules[y][x + 1] && color === modules[y + 1][x] && color === modules[y + 1][x + 1]) penalty += 3;
      }
    }
  }
  const total = size * size;
  penalty += Math.floor(Math.abs(dark * 20 - total * 10) / total) * 10;
  return penalty;
}

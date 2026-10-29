// CLR TrueCentral VMS — Aplicaciones → Vista en vivo (panel web).
//
// Réplica en el navegador de la Vista en vivo del cliente de escritorio
// (Views\LiveView.xaml): árbol de cámaras por equipo o por ubicación con
// buscador, grilla con las MISMAS divisiones (ViewModels\VideoLayouts.cs),
// barra por cuadro (audio, P/S, captura, grabación local, cerrar), doble clic
// para maximizar (sube a principal y vuelve a secundario al restaurar),
// arrastrar del árbol al cuadro y de un cuadro a otro, zoom digital, vistas
// guardadas, pantalla completa, pantallas auxiliares (ventanas del
// navegador), PTZ con teclado y joystick (live-joystick.js) y el panel de
// parlantes IP.
//
// El video llega por WebRTC: el navegador manda su oferta a
// /api/streams/webrtc (mismas validaciones, alcance y auditoría que el RTSP
// del cliente) y el medio viaja directo desde el MediaMTX del servidor por su
// puerto ICE. Cada cámara puesta en un cuadro (LvFeed) tiene DOS <video>: los
// cambios de stream se abren en el de atrás y se intercambian cuando ya hay
// imagen, sin pantallazo negro (el doble buffer del cliente).
//
// Se carga después de resources.js (usa LOC_KINDS y resIcon).
"use strict";

// ---------------------------------------------------------------------------
// Divisiones de pantalla: las mismas del cliente y con las MISMAS claves (las
// guardan las vistas guardadas, compartidas entre el cliente y la web).
// ---------------------------------------------------------------------------
function lvCellRect(col, row, colSpan = 1, rowSpan = 1) { return { col, row, colSpan, rowSpan }; }

function lvUniform(columns, rows, key) {
  const cells = [];
  for (let r = 0; r < rows; r++) for (let c = 0; c < columns; c++) cells.push(lvCellRect(c, r));
  const name = String(columns * rows);
  return { key: key ?? name, name, columns, rows, cells };
}

/** Un cuadro grande de (lado-1)×(lado-1) arriba a la izquierda y la L en cuadros simples. */
function lvOneBig(side) {
  const cells = [lvCellRect(0, 0, side - 1, side - 1)];
  for (let r = 0; r < side - 1; r++) cells.push(lvCellRect(side - 1, r));
  for (let c = 0; c < side; c++) cells.push(lvCellRect(c, side - 1));
  return { key: `${2 * side} con principal`, name: String(2 * side), columns: side, rows: side, cells };
}

/** División dibujada como mapa de caracteres (cada letra, un cuadro rectangular). */
function lvMap(key, ...rows) {
  const columns = rows[0].length, order = [], bounds = new Map();
  rows.forEach((line, r) => [...line].forEach((ch, c) => {
    const b = bounds.get(ch);
    if (b) { b.col = Math.min(b.col, c); b.maxCol = Math.max(b.maxCol, c); b.maxRow = r; }
    else { order.push(ch); bounds.set(ch, { col: c, row: r, maxCol: c, maxRow: r }); }
  }));
  const cells = order.map((ch) => {
    const b = bounds.get(ch);
    return lvCellRect(b.col, b.row, b.maxCol - b.col + 1, b.maxRow - b.row + 1);
  });
  return { key, name: String(cells.length), columns, rows: rows.length, cells };
}

/** Cuadrícula a medida (las de "abrir un equipo completo"): clave "8×5". */
function lvGrid(columns, rows) {
  columns = Math.min(Math.max(columns | 0, 1), 16);
  rows = Math.min(Math.max(rows | 0, 1), 16);
  const layout = lvUniform(columns, rows);
  layout.key = layout.name = `${columns}×${rows}`;
  return layout;
}

/**
 * Tope de cuadros del panel web: el navegador no está hecho para muros de
 * 25-64 cámaras (cada una es una sesión WebRTC y un decodificador); para eso
 * está el cliente de escritorio, que conserva todas las divisiones.
 */
const LV_MAX_CELLS = 16;

/** Grilla con el mínimo de cuadros sobrantes, algo más ancha que alta (VideoLayout.FitFor). */
function lvFitFor(count) {
  count = Math.min(Math.max(count, 1), LV_MAX_CELLS);
  let best = [1, count], bestScore = Infinity;
  for (let cols = 1; cols <= count; cols++) {
    const rows = Math.ceil(count / cols);
    if (cols < rows || cols > rows * 2 + 1) continue;
    const score = (cols * rows - count) * 100 + (cols - rows);
    if (score < bestScore) { bestScore = score; best = [cols, rows]; }
  }
  return lvGrid(best[0], best[1]);
}

const LV_LAYOUT_GROUPS = [
  // Solo las de hasta 16 cuadros (LV_MAX_CELLS); las mayores quedan en el cliente de escritorio.
  { title: "Uniformes", layouts: [lvUniform(1, 1), lvUniform(2, 2), lvUniform(3, 3), lvUniform(4, 4)] },
  {
    title: "Con cuadro principal", layouts: [
      lvOneBig(3), lvOneBig(4), lvMap("9 con principal", "AABC", "AADE", "FGHI"), lvOneBig(5), lvOneBig(6), lvOneBig(8),
    ],
  },
  {
    title: "En columnas", layouts: [
      lvUniform(2, 1, "2 en columnas"), lvMap("3 en columnas", "AB", "AC"), lvMap("5 en columnas", "AABC", "AADE"),
      lvUniform(3, 2, "6 en columnas"), lvUniform(4, 2, "8 en columnas"),
    ],
  },
  {
    title: "En filas", layouts: [
      lvUniform(1, 2, "2 en filas"), lvMap("3 en filas", "AA", "BC"), lvMap("5 en filas", "AA", "AA", "BC", "DE"),
      lvUniform(2, 3, "6 en filas"), lvUniform(2, 4, "8 en filas"),
    ],
  },
  {
    title: "Combinadas", layouts: [
      lvMap("4 combinada A", "AB", "AC", "AD"),
      lvMap("4 combinada B", "AAA", "BCD"),
      lvMap("5 combinada", "AB", "AC", "AD", "AE"),
      lvMap("6 combinada", "AB", "AB", "CD", "EF"),
      lvMap("7 combinada A", "AABB", "AABB", "CCDE", "CCFG"),
      lvMap("7 combinada B", "AAABBBCC", "AAABBBCC", "AAABBBDD", "EEEFFFDD", "EEEFFFGG", "EEEFFFGG"),
      lvUniform(3, 4, "12 combinada"),
      lvMap("13 combinada", "ABCD", "EFFG", "HFFI", "JKLM"),
    ],
  },
];
const LV_LAYOUTS = LV_LAYOUT_GROUPS.flatMap((g) => g.layouts);
const LV_LEGACY_KEYS = { "6": "6 con principal", "8": "8 con principal", "13": "13 combinada" };

function lvFindLayout(key) {
  if (!key) return null;
  if (key.startsWith("m")) {
    const layout = lvDecodeLayout(key);
    return layout && layout.cells.length <= LV_MAX_CELLS ? layout : null;
  }
  key = LV_LEGACY_KEYS[key] ?? key;
  return LV_LAYOUTS.find((l) => l.key === key) ?? null;
}

/** Cómo se nombra la división en los textos (una ampliada dice de cuál salió). */
function lvLayoutLabel(layout) { return layout.key.startsWith("m") ? `a medida ${layout.columns}×${layout.rows}` : layout.key; }

// ---------------------------------------------------------------------------
// Divisiones a medida (grilla ampliada o con cuadros unidos en el cliente):
// misma clave compacta que VideoLayout.FromCells: "m" + columnas + filas + 2
// caracteres por cada cuadro de más de una celda (columna, fila, ancho-1 y
// alto-1 en 3 bits cada uno); las celdas libres son cuadros simples.
// ---------------------------------------------------------------------------
/** Tiempo que el puntero tiene que quedarse en el borde para que aparezca la franja. */
const LV_GROW_DWELL_MS = 1200;
const LV_GROW_MAX_COLUMNS = 8, LV_GROW_MAX_ROWS = 8, LV_GROW_MAX_CELLS = 64;
const LV_CUSTOM_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
const LV_MAX_BIG_CELLS = 14;

/** La división de esos cuadros: la del selector si coincide, "C×R" si es uniforme o una a medida (null si no cabe en la clave). */
function lvFromCells(columns, rows, cells) {
  cells.sort((a, b) => a.row - b.row || a.col - b.col);
  const same = (l) => l.columns === columns && l.rows === rows && l.cells.length === cells.length &&
    l.cells.every((c, i) => c.col === cells[i].col && c.row === cells[i].row && c.colSpan === cells[i].colSpan && c.rowSpan === cells[i].rowSpan);
  const standard = LV_LAYOUTS.find(same);
  if (standard) return standard;
  const big = cells.filter((c) => c.colSpan > 1 || c.rowSpan > 1);
  if (!big.length && cells.length === columns * rows) return lvGrid(columns, rows);
  if (big.length > LV_MAX_BIG_CELLS || columns > 8 || rows > 8) return null;
  const key = `m${columns}${rows}` + big.map((c) => {
    const v = (c.col << 9) | (c.row << 6) | ((c.colSpan - 1) << 3) | (c.rowSpan - 1);
    return LV_CUSTOM_ALPHABET[v >> 6] + LV_CUSTOM_ALPHABET[v & 63];
  }).join("");
  return { key, name: String(cells.length), columns, rows, cells };
}

/** Rearma una división a medida desde su clave (null si no es válida). */
function lvDecodeLayout(key) {
  if (key.length < 3 || (key.length - 3) % 2) return null;
  const columns = Number(key[1]), rows = Number(key[2]);
  if (!(columns >= 1 && columns <= 8 && rows >= 1 && rows <= 8)) return null;
  const taken = new Set(), cells = [];
  for (let i = 3; i < key.length; i += 2) {
    const hi = LV_CUSTOM_ALPHABET.indexOf(key[i]), lo = LV_CUSTOM_ALPHABET.indexOf(key[i + 1]);
    if (hi < 0 || lo < 0) return null;
    const v = (hi << 6) | lo;
    const c = lvCellRect((v >> 9) & 7, (v >> 6) & 7, ((v >> 3) & 7) + 1, (v & 7) + 1);
    if (c.col + c.colSpan > columns || c.row + c.rowSpan > rows) return null;
    for (let x = c.col; x < c.col + c.colSpan; x++)
      for (let y = c.row; y < c.row + c.rowSpan; y++) {
        if (taken.has(`${x},${y}`)) return null;
        taken.add(`${x},${y}`);
      }
    cells.push(c);
  }
  for (let y = 0; y < rows; y++) for (let x = 0; x < columns; x++) if (!taken.has(`${x},${y}`)) cells.push(lvCellRect(x, y));
  return lvFromCells(columns, rows, cells);
}

// Ampliar la grilla: soltar una cámara en un borde agrega una franja (fila o
// columna) de ese lado. Mismo algoritmo que el cliente (VideoLayout.Grow).
const lvSideways = (edge) => edge === "left" || edge === "right";

/** Por qué no se puede agregar una franja en ese borde (null = se puede). */
function lvGrowBlocked(layout, edge, maxCells) {
  const sideways = lvSideways(edge);
  if (sideways && layout.columns >= LV_GROW_MAX_COLUMNS) return `La grilla ya tiene ${LV_GROW_MAX_COLUMNS} columnas, el máximo.`;
  if (!sideways && layout.rows >= LV_GROW_MAX_ROWS) return `La grilla ya tiene ${LV_GROW_MAX_ROWS} filas, el máximo.`;
  const count = layout.cells.length + (sideways ? layout.rows : layout.columns);
  if (count > Math.min(maxCells, LV_GROW_MAX_CELLS))
    return maxCells >= LV_MAX_CELLS
      ? `El panel web muestra hasta ${LV_MAX_CELLS} cuadros; para una grilla más grande use el cliente de escritorio.`
      : `Con ${count} cuadros la grilla superaría las ${maxCells} cámaras disponibles.`;
  return null;
}

/**
 * La división con una franja más en edge, o null si se pasa de los topes.
 * placement[i] dice dónde queda el cuadro i actual y strip son los cuadros de
 * la franja (de izquierda a derecha o de arriba abajo).
 */
function lvGrow(layout, edge, maxCells) {
  if (lvGrowBlocked(layout, edge, maxCells)) return null;
  const sideways = lvSideways(edge);
  const columns = layout.columns + (sideways ? 1 : 0), rows = layout.rows + (sideways ? 0 : 1);
  const dx = edge === "left" ? 1 : 0, dy = edge === "top" ? 1 : 0;
  const all = layout.cells.map((c, i) => ({ cell: { ...c, col: c.col + dx, row: c.row + dy }, from: i }));
  const stripLength = sideways ? layout.rows : layout.columns;
  for (let k = 0; k < stripLength; k++)
    all.push({
      cell: sideways ? lvCellRect(edge === "left" ? 0 : layout.columns, k) : lvCellRect(k, edge === "top" ? 0 : layout.rows),
      from: -1 - k,
    });
  all.sort((a, b) => a.cell.row - b.cell.row || a.cell.col - b.cell.col);
  const placement = new Array(layout.cells.length), strip = new Array(stripLength);
  all.forEach((x, i) => { if (x.from >= 0) placement[x.from] = i; else strip[-1 - x.from] = i; });

  const grown = lvFromCells(columns, rows, all.map((x) => x.cell));
  if (!grown) return null;
  return { layout: grown, placement, strip };
}

// ---------------------------------------------------------------------------
// Unir y separar cuadros (mismo algoritmo que VideoLayout.Merge/Split del
// cliente): el rectángulo de un cuadro a otro se une en uno solo, siempre que
// no corte a otro cuadro por la mitad (un cuadro grande que asoma, o una L).
// ---------------------------------------------------------------------------
const lvOverlaps = (a, b) => a.col < b.col + b.colSpan && b.col < a.col + a.colSpan && a.row < b.row + b.rowSpan && b.row < a.row + a.rowSpan;
const lvContains = (o, i) => i.col >= o.col && i.row >= o.row && i.col + i.colSpan <= o.col + o.colSpan && i.row + i.rowSpan <= o.row + o.rowSpan;
const lvSameCell = (a, b) => a.col === b.col && a.row === b.row && a.colSpan === b.colSpan && a.rowSpan === b.rowSpan;
const lvIndexOfCell = (layout, cell) => layout.cells.findIndex((c) => lvSameCell(c, cell));

/** Rectángulo de los cuadros a y b, los cuadros que quedan adentro y los que lo cortan. */
function lvMergeSelection(layout, a, b) {
  const x = layout.cells[a], y = layout.cells[b];
  const col = Math.min(x.col, y.col), row = Math.min(x.row, y.row);
  const rect = lvCellRect(col, row,
    Math.max(x.col + x.colSpan, y.col + y.colSpan) - col, Math.max(x.row + x.rowSpan, y.row + y.rowSpan) - row);
  const inside = [], cut = [];
  layout.cells.forEach((c, i) => { if (lvContains(rect, c)) inside.push(i); else if (lvOverlaps(rect, c)) cut.push(i); });
  return { rect, inside, cut };
}

/** Une los cuadros de a a b: { layout, placement, merged, inside } o { reason }. */
function lvMerge(layout, a, b) {
  const { rect, inside, cut } = lvMergeSelection(layout, a, b);
  if (cut.length) return { reason: "La selección corta un cuadro por la mitad: tiene que formar un rectángulo completo." };
  if (inside.length < 2) return { reason: "Arrastre hasta otro cuadro para unirlos." };
  const merged = lvFromCells(layout.columns, layout.rows,
    [...layout.cells.filter((_, i) => !inside.includes(i)).map((c) => ({ ...c })), rect]);
  if (!merged) return { reason: `La grilla ya tiene ${LV_MAX_BIG_CELLS} cuadros unidos, el máximo.` };
  const target = lvIndexOfCell(merged, rect);
  return {
    layout: merged, merged: target, inside,
    placement: layout.cells.map((c, i) => (inside.includes(i) ? target : lvIndexOfCell(merged, c))),
  };
}

/** Separa un cuadro unido en cuadros simples (null si ya es simple). */
function lvSplit(layout, index) {
  const big = layout.cells[index];
  if (big.colSpan === 1 && big.rowSpan === 1) return null;
  const cells = layout.cells.filter((_, i) => i !== index).map((c) => ({ ...c }));
  for (let y = big.row; y < big.row + big.rowSpan; y++)
    for (let x = big.col; x < big.col + big.colSpan; x++) cells.push(lvCellRect(x, y));
  const split = lvFromCells(layout.columns, layout.rows, cells);
  const placement = layout.cells.map((c, i) => lvIndexOfCell(split, i === index ? lvCellRect(big.col, big.row) : c));
  return { layout: split, placement, first: placement[index] };
}
/**
 * División de una vista guardada: la del selector, o una grilla a medida del
 * tamaño guardado; null si es más grande de lo que admite el panel web (una
 * vista armada en el cliente de escritorio con 25 o más cuadros).
 */
function lvRestoreLayout(key, columns, rows) {
  const found = lvFindLayout(key);
  if (found) return found;
  const cols = columns || 2, rws = rows || 2;
  return cols * rws <= LV_MAX_CELLS ? lvGrid(cols, rws) : null;
}

/**
 * Cuadros de una vista guardada en esta grilla. Si la vista es más grande que
 * lo que admite el panel web, sus cámaras se acomodan en orden en una división
 * estándar de hasta 16 cuadros y las que sobran se omiten (compacted).
 */
function lvFitSavedItems(layout, items) {
  const sorted = [...items].sort((a, b) => a.cellIndex - b.cellIndex);
  if (layout && sorted.every((i) => i.cellIndex < layout.cells.length)) return { layout, items: sorted, compacted: false };
  const kept = sorted.slice(0, LV_MAX_CELLS);
  return {
    layout: lvSmallestFor(Math.max(kept.length, 1)),
    items: kept.map((item, index) => ({ ...item, cellIndex: index })),
    compacted: true,
  };
}
/** La división estándar más chica (uniformes o con principal) donde quepan count cuadros. */
function lvSmallestFor(count) {
  const pool = [...LV_LAYOUT_GROUPS[0].layouts, ...LV_LAYOUT_GROUPS[1].layouts]
    .filter((l) => l.cells.length >= count).sort((a, b) => a.cells.length - b.cells.length);
  return pool[0] ?? LV_LAYOUT_GROUPS[0].layouts.at(-1);
}

/** Miniatura de una división (rectángulos proporcionales). */
function lvLayoutPreview(layout, w, h) {
  return `<span class="lv-lp" style="width:${w}px;height:${h}px;grid-template-columns:repeat(${layout.columns},1fr);grid-template-rows:repeat(${layout.rows},1fr)">${
    layout.cells.map((c) => `<i style="grid-column:${c.col + 1}/span ${c.colSpan};grid-row:${c.row + 1}/span ${c.rowSpan}"></i>`).join("")}</span>`;
}

// ---------------------------------------------------------------------------
// Preferencias por navegador (lo que en el cliente es client.json)
// ---------------------------------------------------------------------------
const LV_PREFS_KEY = "tcvms.live.prefs";
const LV_LAST_KEY = "tcvms.live.last";
const LV_PREFS_DEFAULTS = {
  layout: "4", byLocation: false, treeCollapsed: false, collapsedNodes: [],
  ptzOpen: false, ptzSpeed: 4, spkOpen: false, speakers: [], tone: true, mic: "",
  profile: "auto", fitDevice: true, stretch: false, reopen: false, snapFormat: "jpg",
};

// Modo Flota (#/fleet/live): la misma grilla con el árbol de vehículos. Sus preferencias van aparte: por 4G conviene el
// secundario y cortar el video que nadie mira (consume datos móviles del vehículo).
const LV_FLEET_PREFS_KEY = "tcvms.fleet.prefs";
const LV_FLEET_LAST_KEY = "tcvms.fleet.last";
const LV_FLEET_DEFAULTS = { profile: "sub", idleMinutes: 5 };
/** Segundos de aviso antes de cortar el video por inactividad. */
const LV_IDLE_WARN_SECONDS = 30;

function lvPrefsKey() { return lv.fleet ? LV_FLEET_PREFS_KEY : LV_PREFS_KEY; }
function lvLastKey() { return lv.fleet ? LV_FLEET_LAST_KEY : LV_LAST_KEY; }

function lvLoadPrefs(aux) {
  let prefs = { ...LV_PREFS_DEFAULTS, ...(lv.fleet ? LV_FLEET_DEFAULTS : {}) };
  try { prefs = { ...prefs, ...JSON.parse(localStorage.getItem(lvPrefsKey()) || "{}") }; } catch { /* ilegible */ }
  // Las pantallas auxiliares recuerdan su propia división.
  if (aux) {
    try { prefs.layout = JSON.parse(localStorage.getItem(`${LV_PREFS_KEY}.aux${aux}`) || "{}").layout ?? "4"; } catch { prefs.layout = "4"; }
  }
  return prefs;
}

function lvSavePrefs() {
  const p = lv.prefs;
  try {
    if (lv.aux) {
      localStorage.setItem(`${LV_PREFS_KEY}.aux${lv.aux}`, JSON.stringify({ layout: p.layout }));
      // Lo demás (ajustes) es común a todas las ventanas: se guarda sin pisar la división de la principal.
      const main = JSON.parse(localStorage.getItem(LV_PREFS_KEY) || "{}");
      localStorage.setItem(LV_PREFS_KEY, JSON.stringify({ ...p, layout: main.layout ?? LV_PREFS_DEFAULTS.layout }));
    } else {
      localStorage.setItem(lvPrefsKey(), JSON.stringify(p));
    }
  } catch { /* modo privado */ }
}

// ---------------------------------------------------------------------------
// Íconos
// ---------------------------------------------------------------------------
function lvSvg(paths, size = 13, extra = "") {
  return `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" ${extra}>${paths}</svg>`;
}
const LV_ICO = {
  mute: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M17 9l4 6M21 9l-4 6"/>',
  audio: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 9a4 4 0 0 1 0 6M19 6.5a8 8 0 0 1 0 11"/>',
  snap: '<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/>',
  zoom: '<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4.3-4.3M11 8v6M8 11h6"/>',
  views: '<rect x="3" y="4" width="8" height="7" rx="1"/><rect x="13" y="4" width="8" height="7" rx="1"/><rect x="3" y="13" width="8" height="7" rx="1"/><rect x="13" y="13" width="8" height="7" rx="1"/>',
  aux: '<rect x="2" y="4" width="13" height="10" rx="1"/><path d="M9 18h7M12 14v4"/><rect x="17" y="8" width="5" height="9" rx="1"/>',
  full: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13.5 6.5l4 4"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
  play: '<path d="M7 5l12 7-12 7z"/>',
  ear: '<path d="M7 9a5 5 0 0 1 10 0c0 3-3 4-3 7a3 3 0 0 1-6 0"/><path d="M10 9a2 2 0 0 1 4 0"/>',
  download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
  checkAll: '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M8 12l3 3 5-6"/>',
  checkNone: '<rect x="3" y="3" width="18" height="18" rx="2"/>',
  chevDown: '<path d="M6 9l6 6 6-6"/>',
  chevUp: '<path d="M6 15l6-6 6 6"/>',
};

// ---------------------------------------------------------------------------
// Estado de la página (una vista en vivo por ventana)
// ---------------------------------------------------------------------------
const lv = {
  active: false,
  fleet: false,            // modo Flota: árbol de vehículos (#/fleet/live)
  lastInput: 0,            // última interacción del operador (corte por inactividad en Flota)
  idleWarnAt: 0,           // cuándo empezó el aviso de corte (0 = sin aviso)
  aux: 0,                  // 0 = ventana principal; 1..3 = pantalla auxiliar
  windowId: Math.random().toString(36).slice(2),
  prefs: { ...LV_PREFS_DEFAULTS },
  devices: [],             // [{ id, name, status, channels: [ch] }]
  channels: new Map(),     // id de canal → ch (con .device)
  locRoots: [],
  layout: null,
  cells: [],               // LvCell en el orden de la grilla
  selected: null,
  maximized: -1,
  promoted: null,          // feed subido a principal por estar maximizado
  promotedChannel: null,
  zoomMode: false,
  mergeMode: false,        // modo «Unir cuadros»
  search: "",
  selChannelId: null,
  dropCell: null,
  grow: null,              // franja nueva a la vista al arrastrar al borde: { edge, slot, el, slots }
  growPending: null,       // borde donde espera el puntero antes de abrir la franja: { edge, since, told }
  growWired: false,
  bulk: null,             // texto de la apertura masiva en curso
  views: [],
  activeView: "",
  ptzChain: Promise.resolve(),
  ptzHeld: null,
  keyHeld: null,
  shift: false,
  auxWindows: [],
  timers: [],
  bc: null,
  speakers: [],
  sounds: [],
  library: [],
  talk: null,
  preview: null,
  treeReload: null,
  lastSaveTimer: null,
};

// ---------------------------------------------------------------------------
// Sesión WebRTC con el media server (una por stream abierto)
// ---------------------------------------------------------------------------
const lvCancelled = () => ({ cancelled: true });

function lvIceGathered(pc, timeoutMs) {
  if (pc.iceGatheringState === "complete") return Promise.resolve();
  return new Promise((resolve) => {
    const check = () => { if (pc.iceGatheringState === "complete") done(); };
    const done = () => { clearTimeout(timer); pc.removeEventListener("icegatheringstatechange", check); resolve(); };
    const timer = setTimeout(done, timeoutMs);
    pc.addEventListener("icegatheringstatechange", check);
  });
}

/** Libera la sesión en el servidor aunque la página se esté cerrando (keepalive). */
function lvDeleteSession(id) {
  try {
    fetch(`/api/streams/webrtc/${encodeURIComponent(id)}`, {
      method: "DELETE", keepalive: true,
      headers: Api.token ? { Authorization: "Bearer " + Api.token } : {},
    }).catch(() => { /* MediaMTX la cerrará igual al perder al lector */ });
  } catch { /* noop */ }
}

class LvSession {
  constructor(channel, profile) {
    this.channel = channel;
    this.profile = profile;
    this.pc = null;
    this.stream = new MediaStream();
    this.id = null;
    this.closed = false;
    this.hasAudio = false;
    this.icePort = null;
    this.onDrop = null;
    this.onAudio = null;
  }

  /** Negocia con el servidor; resuelve con la respuesta aplicada (la imagen llega después). */
  async open() {
    const pc = this.pc = new RTCPeerConnection({ bundlePolicy: "max-bundle" });
    pc.addTransceiver("video", { direction: "recvonly" });
    pc.addTransceiver("audio", { direction: "recvonly" });
    pc.ontrack = (e) => {
      this.stream.addTrack(e.track);
      // MediaMTX anuncia una pista de audio aunque la cámara no tenga: hay
      // audio de verdad recién cuando llegan paquetes (la pista se "desmuta").
      if (e.track.kind === "audio") {
        const mark = () => { this.hasAudio = true; this.onAudio?.(); };
        if (!e.track.muted) mark(); else e.track.addEventListener("unmute", mark, { once: true });
      }
    };
    pc.onconnectionstatechange = () => {
      if (!this.closed && (pc.connectionState === "failed" || pc.connectionState === "closed")) this.onDrop?.();
    };
    await pc.setLocalDescription(await pc.createOffer());
    // Sin STUN los candidatos locales salen al instante: la oferta va completa (sin trickle).
    await lvIceGathered(pc, 1500);
    if (this.closed) throw lvCancelled();
    const res = await this.negotiate(pc.localDescription.sdp);
    this.id = res.session;
    if (this.closed) { this.release(); throw lvCancelled(); }
    const port = /a=candidate:\S+ \d+ \S+ \d+ \S+ (\d+) typ host/.exec(res.answer);
    this.icePort = port ? port[1] : null;
    await pc.setRemoteDescription({ type: "answer", sdp: res.answer });
  }

  /** Oferta al servidor → { answer, session }. La reproducción (playback.js) pide otra concesión. */
  negotiate(offer) {
    return Api.post("/api/streams/webrtc", {
      deviceId: this.channel.deviceId, rtspChannel: this.channel.rtspChannel,
      profile: this.profile, offer,
    });
  }

  /** Bytes y cuadros de video recibidos (vigía de señal). */
  async videoStats() {
    if (!this.pc || this.closed) return null;
    try {
      const stats = await this.pc.getStats();
      for (const r of stats.values())
        if (r.type === "inbound-rtp" && r.kind === "video") return { bytes: r.bytesReceived || 0, frames: r.framesDecoded || 0 };
    } catch { /* cerrada entretanto */ }
    return { bytes: 0, frames: 0 };
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    try { this.pc?.close(); } catch { /* ya cerrada */ }
    this.stream.getTracks().forEach((t) => t.stop());
    this.release();
  }

  release() {
    if (!this.id) return;
    const id = this.id;
    this.id = null;
    lvDeleteSession(id);
  }
}

// ---------------------------------------------------------------------------
// Entrega alternativa por MSE para lo que WebRTC no admite (H.265 con
// B-frames: MediaMTX corta la sesión al primer B-frame). El servidor
// reempaqueta la ruta a MP4 fragmentado sin recodificar (/api/streams/mse) y
// el navegador la decodifica con Media Source Extensions en un <video> oculto;
// la sesión expone su captureStream(), así que el cuadro la trata igual que a
// una WebRTC (captura, grabación local, zoom y vigía de señal incluidos).
// ---------------------------------------------------------------------------
const LV_MSE_KEY = "tcvms.live.mse";

/** Canales (y perfil) que deben ir por MSE: los aprendidos se recuerdan en este navegador. */
const lvMseChannels = (() => {
  let list = [];
  try { list = JSON.parse(localStorage.getItem(LV_MSE_KEY) || "[]"); } catch { /* sin almacenamiento */ }
  return new Set(Array.isArray(list) ? list : []);
})();
function lvMseKey(channel, profile) { return `${channel.deviceId}/${channel.rtspChannel}/${profile}`; }
function lvMarkMse(key) {
  lvMseChannels.add(key);
  try { localStorage.setItem(LV_MSE_KEY, JSON.stringify([...lvMseChannels])); } catch { /* modo privado */ }
}

/** Sesión WebRTC o MSE según lo aprendido del canal. */
function lvNewSession(channel, profile) {
  return lvMseChannels.has(lvMseKey(channel, profile)) ? new LvMseSession(channel, profile) : new LvSession(channel, profile);
}

/**
 * H.265 en este navegador (se mide una vez por página). Chromium solo lo
 * decodifica con la GPU, y Edge no lo ofrece por WebRTC aunque la GPU sepa:
 * ahí el MSE sí sirve. Decide la vía cuando el servidor rechaza la conexión
 * WebRTC por el códec de la cámara.
 */
const lvHevc = (() => {
  let webrtc = false, mse = false;
  try { webrtc = !!RTCRtpReceiver.getCapabilities?.("video")?.codecs?.some((c) => /h265|hevc/i.test(c.mimeType)); } catch { /* sin WebRTC */ }
  try { mse = !!window.MediaSource?.isTypeSupported('video/mp4; codecs="hvc1.1.6.L120.90"'); } catch { /* sin MSE */ }
  return { webrtc, mse };
})();

/** Canal y perfil cuyo formato este navegador no reproduce por ninguna vía (hasta recargar la página). */
const lvNoDecode = new Set();

/** ¿El servidor rechazó la conexión WebRTC porque este navegador no recibe el códec de la cámara? */
function lvCodecRejected(err) { return String(err?.data?.detail || "").toLowerCase().includes("codec"); }

/** Aviso cuando ninguna vía sirve para el formato del canal. */
function lvCodecMessage(err) {
  if (err?.codec) return err.error; // el MSE ya dice qué formato no admite
  return "Este navegador no reproduce el formato de video de la cámara: típicamente H.265, que en este equipo no " +
    "decodifica ni por WebRTC ni por la entrega alternativa (le falta decodificación por hardware). Use Chrome " +
    "actualizado, configure el stream de la cámara en H.264 o use el cliente de escritorio.";
}

/**
 * Códec RFC 6381 del MP4 (init segment): "hvc1.1.6.L153.B0", "avc1.640028"…
 * Recorre moov › trak › mdia › minf › stbl › stsd hasta la entrada de video y
 * su caja de configuración (hvcC / avcC). null si el init todavía está incompleto.
 */
function lvMp4Codec(bytes) {
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const type = (o) => String.fromCharCode(bytes[o + 4], bytes[o + 5], bytes[o + 6], bytes[o + 7]);
  const find = (start, end, name) => {
    for (let o = start; o + 8 <= end;) {
      const size = dv.getUint32(o);
      if (size < 8 || o + size > end) return null;
      if (type(o) === name) return { o, end: o + size };
      o += size;
    }
    return null;
  };
  let box = { o: -8, end: bytes.length };
  for (const name of ["moov", "trak", "mdia", "minf", "stbl", "stsd"]) {
    box = find(box.o + 8, box.end, name);
    if (!box) return null;
  }
  const entry = box.o + 16; // stsd: cabecera (8) + versión/flags (4) + cantidad (4)
  if (entry + 8 > box.end) return null;
  const fmt = type(entry), entryEnd = entry + dv.getUint32(entry);
  const hex = (n) => n.toString(16).toUpperCase().padStart(2, "0");
  if (fmt === "hvc1" || fmt === "hev1") {
    const c = find(entry + 86, entryEnd, "hvcC");
    if (!c) return null;
    const d = c.o + 8;
    const b1 = bytes[d + 1];
    const space = ["", "A", "B", "C"][b1 >> 6], tier = (b1 >> 5) & 1 ? "H" : "L", profile = b1 & 0x1f;
    let compat = dv.getUint32(d + 2), rev = 0;
    for (let i = 0; i < 32; i++) { rev = (rev << 1) | (compat & 1); compat >>>= 1; }
    const cons = [...bytes.subarray(d + 6, d + 12)];
    while (cons.length && cons[cons.length - 1] === 0) cons.pop();
    return [`${fmt}.${space}${profile}`, (rev >>> 0).toString(16).toUpperCase(), `${tier}${bytes[d + 12]}`,
      ...cons.map((b) => b.toString(16).toUpperCase())].join(".");
  }
  if (fmt === "avc1" || fmt === "avc3") {
    const c = find(entry + 86, entryEnd, "avcC");
    if (!c) return null;
    const d = c.o + 8;
    return `${fmt}.${hex(bytes[d + 1])}${hex(bytes[d + 2])}${hex(bytes[d + 3])}`;
  }
  return null;
}

class LvMseSession {
  constructor(channel, profile) {
    this.channel = channel;
    this.profile = profile;
    this.kind = "mse";
    this.stream = new MediaStream();
    this.pc = null;
    this.id = null;
    this.closed = false;
    this.hasAudio = false;
    this.icePort = null;
    this.onDrop = null;
    this.onAudio = null;
    this.bytes = 0;
    this.ws = null;
    this.video = null;
    this.ms = null;
    this.sb = null;
    this.queue = [];
    this.codec = "";
  }

  /** URL del WebSocket (la reproducción de grabaciones la cambia). */
  url() {
    const q = new URLSearchParams({ deviceId: this.channel.deviceId, rtspChannel: this.channel.rtspChannel,
      profile: this.profile, access_token: Api.token || "" });
    return `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/api/streams/mse?${q}`;
  }

  /** Resuelve cuando hay imagen y su captureStream() ya trae la pista de video. */
  open() {
    return new Promise((resolve, reject) => {
      // codec: el navegador no admite este formato (reintentar no sirve).
      const fail = (error, codec = false) => { if (!this.closed) { this.close(); reject({ error, codec }); } };
      const video = this.video = document.createElement("video");
      video.muted = true;
      video.playsInline = true;
      video.style.cssText = "position:fixed;left:-10px;top:-10px;width:2px;height:2px;opacity:0;pointer-events:none";
      document.body.append(video);
      const ms = this.ms = new MediaSource();
      video.src = URL.createObjectURL(ms);
      let pending = new Uint8Array(0), started = false, resolved = false;
      const timer = setTimeout(() => { if (!resolved) fail("El equipo no entregó imagen a tiempo."); }, 25000);
      ms.addEventListener("sourceopen", () => {
        const ws = this.ws = new WebSocket(this.url());
        ws.binaryType = "arraybuffer";
        ws.onmessage = (e) => {
          if (this.closed) return;
          const chunk = new Uint8Array(e.data);
          this.bytes += chunk.byteLength;
          if (!this.sb) {
            // Se junta hasta tener el init segment completo para saber el códec exacto.
            const merged = new Uint8Array(pending.length + chunk.length);
            merged.set(pending); merged.set(chunk, pending.length);
            pending = merged;
            const codec = lvMp4Codec(pending);
            if (!codec) { if (pending.length > 4 * 1024 * 1024) fail("El video recibido no tiene un formato reconocible.", true); return; }
            this.codec = codec;
            const mime = `video/mp4; codecs="${codec}"`;
            if (!MediaSource.isTypeSupported(mime)) {
              fail(codec.startsWith("hev") || codec.startsWith("hvc")
                ? "Este navegador no decodifica H.265 (sin decodificación por hardware). Use Chrome o Edge actualizados en un equipo con soporte H.265, o el cliente de escritorio."
                : `Este navegador no admite el formato de video de la cámara (${codec}).`, true);
              return;
            }
            try { this.sb = ms.addSourceBuffer(mime); } catch (err) { fail(`No se pudo preparar el video: ${err.message}`); return; }
            this.sb.mode = "segments";
            this.sb.addEventListener("updateend", () => this.pump());
            this.queue.push(pending);
            pending = null;
          } else {
            this.queue.push(chunk);
          }
          this.pump();
        };
        ws.onclose = (e) => {
          clearTimeout(timer);
          if (this.closed) return;
          if (!resolved) { fail(e.reason || "El servidor cerró la conexión de video."); return; }
          this.onDrop?.();
        };
      }, { once: true });
      // Primer cuadro: recién ahí captureStream() trae la pista de video.
      const ready = () => {
        if (resolved || this.closed || video.readyState < 2 || !video.videoWidth) return;
        resolved = true;
        clearTimeout(timer);
        const captured = video.captureStream ? video.captureStream() : video.mozCaptureStream?.();
        if (!captured) { fail("Este navegador no permite mostrar este video."); return; }
        captured.getVideoTracks().forEach((t) => this.stream.addTrack(t));
        resolve();
      };
      video.addEventListener("loadeddata", ready);
      video.addEventListener("playing", ready);
      video.addEventListener("canplay", () => { if (!started) { started = true; video.play().catch(() => {}); } ready(); });
    });
  }

  /** Pasa al SourceBuffer lo recibido y mantiene el video cerca del borde en vivo. */
  pump() {
    const sb = this.sb, v = this.video;
    if (!sb || sb.updating || this.closed) return;
    const buf = sb.buffered;
    if (buf.length) {
      const end = buf.end(buf.length - 1);
      if (end - v.currentTime > 1.5) v.currentTime = Math.max(end - 0.3, buf.start(buf.length - 1));
      // Lo ya visto sobra: el búfer no crece sin límite.
      if (v.currentTime - buf.start(0) > 30) { try { sb.remove(buf.start(0), v.currentTime - 10); return; } catch { /* ocupado */ } }
    }
    const next = this.queue.shift();
    if (!next) return;
    try { sb.appendBuffer(next); }
    catch (err) {
      if (err.name === "QuotaExceededError") { this.queue.unshift(next); return; }
      if (!this.closed) this.onDrop?.();
    }
  }

  async videoStats() {
    if (this.closed) return null;
    const q = this.video?.getVideoPlaybackQuality?.();
    return { bytes: this.bytes, frames: q ? q.totalVideoFrames : 0 };
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    try { this.ws?.close(); } catch { /* ya cerrado */ }
    this.stream.getTracks().forEach((t) => t.stop());
    if (this.video) {
      const src = this.video.src;
      this.video.removeAttribute("src");
      this.video.load();
      this.video.remove();
      if (src) URL.revokeObjectURL(src);
    }
  }

  release() { /* no hay sesión que liberar en el servidor: cerrar el WebSocket basta */ }
}

/**
 * ¿La sesión WebRTC recién abierta es H.265 y dejó de recibir video? Es la
 * firma de los B-frames (MediaMTX la corta al primero, a los ~2 s). Resuelve
 * true en ese caso; false si sigue fluyendo o no es H.265.
 */
async function lvLooksLikeHevcBframes(session) {
  if (!session.pc) return false;
  let codec = "";
  try {
    const stats = await session.pc.getStats();
    for (const r of stats.values())
      if (r.type === "inbound-rtp" && r.kind === "video") codec = stats.get(r.codecId)?.mimeType || "";
  } catch { return false; }
  if (!/h265|hevc/i.test(codec)) return false;
  await new Promise((r) => setTimeout(r, 3500));
  if (session.closed) return false;
  const a = await session.videoStats();
  await new Promise((r) => setTimeout(r, 2000));
  if (session.closed) return false;
  const b = await session.videoStats();
  return !!a && !!b && b.bytes === a.bytes;
}

/** Resuelve cuando el <video> ya tiene imagen; rechaza si la conexión se cae o vence el plazo. */
function lvWaitFrame(video, session, timeoutMs) {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const tick = () => {
      if (session.closed) return reject(lvCancelled());
      if (video.readyState >= 2 && video.videoWidth > 0) return resolve();
      const state = session.pc?.connectionState;
      const ice = session.pc?.iceConnectionState;
      if (state === "failed" || Date.now() - started > timeoutMs) {
        const blocked = state === "failed" || ice === "new" || ice === "checking";
        return reject({
          error: blocked
            ? `El video no llegó al navegador: revise que el puerto WebRTC del servidor${session.icePort ? ` (${session.icePort} UDP/TCP)` : ""} esté abierto en el firewall y alcanzable desde este equipo.`
            : "La cámara no entregó imagen a tiempo.",
        });
      }
      setTimeout(tick, 150);
    };
    tick();
  });
}

// ---------------------------------------------------------------------------
// Una cámara puesta en un cuadro. Viaja con su cámara al arrastrarla a otro
// cuadro (lo que se mueve es el escenario con sus <video>: el video no se corta).
// ---------------------------------------------------------------------------
const LV_REC_TYPES = [
  "video/mp4;codecs=avc1,opus", "video/mp4;codecs=avc1,mp4a.40.2", "video/mp4",
  "video/webm;codecs=vp9,opus", "video/webm;codecs=vp8,opus", "video/webm",
];

class LvFeed {
  constructor(channel, profile) {
    this.channel = channel;
    this.profile = profile;           // "Main" | "Sub"
    this.cell = null;
    this.session = null;
    this.pending = null;              // cambio de stream en vuelo
    this.pendingTarget = null;
    this.parked = null;               // secundario estacionado al maximizar
    this.front = 0;
    this.audioOn = false;
    this.connecting = false;
    this.status = "";
    this.error = "";                  // mensaje grande sobre el cuadro (sin video)
    this.seq = 0;
    this.switchSeq = 0;
    this.retryTimer = null;
    this.retryDelay = 5000;
    this.lastBytes = -1;
    this.stall = 0;
    this.rec = null;
    this.flashTimer = null;
    this.zoom = { s: 1, x: 0, y: 0 }; // x, y: desplazamiento en fracción del cuadro
    this.disposed = false;
    this.stage = document.createElement("div");
    this.stage.className = "lv-stage";
    this.stage.innerHTML = `<video class="lv-v front" autoplay playsinline muted></video><video class="lv-v" autoplay playsinline muted></video>`;
    this.videos = [...this.stage.querySelectorAll("video")];
  }

  get title() { return `${this.channel.device?.name ?? "Equipo"} · ${this.channel.name}`; }
  frontVideo() { return this.videos[this.front]; }
  backVideo() { return this.videos[this.front ^ 1]; }

  render() { if (this.cell) lvRenderCell(this.cell); }

  applyFront() {
    this.videos.forEach((v, i) => {
      v.classList.toggle("front", i === this.front);
      v.muted = i !== this.front || !this.audioOn;
    });
    const v = this.frontVideo();
    if (v.paused) v.play().catch(() => { /* sin gesto del usuario: queda en silencio */ });
  }

  setStatus(text) { this.status = text || ""; this.render(); }

  /** Aviso pasajero en la barra del cuadro. */
  flash(text, ms = 6000) {
    clearTimeout(this.flashTimer);
    this.setStatus(text);
    this.flashTimer = setTimeout(() => { if (this.status === text) this.setStatus(""); }, ms);
  }

  clearRetry() { clearTimeout(this.retryTimer); this.retryTimer = null; }

  scheduleRetry(delay) {
    this.clearRetry();
    const wait = delay ?? this.retryDelay;
    this.retryDelay = Math.min(this.retryDelay * 2, 30000);
    this.retryTimer = setTimeout(() => { if (!this.disposed) this.open(); }, wait);
  }

  /** Apertura dura: corta lo que hubiera y negocia de nuevo. Resuelve al tener la respuesta del servidor. */
  async open() {
    const seq = ++this.seq;
    this.switchSeq++;
    this.clearRetry();
    this.pending?.close(); this.pending = null; this.pendingTarget = null;
    this.releaseParked();
    this.session?.close(); this.session = null;
    this.videos.forEach((v) => { v.srcObject = null; });
    this.connecting = true;
    this.render();
    // Un perfil que este navegador no reproduce se cambia por el otro, si ese sí.
    const other = this.profile === "Main" ? "Sub" : "Main";
    if (lvNoDecode.has(lvMseKey(this.channel, this.profile)) && !lvNoDecode.has(lvMseKey(this.channel, other))) this.profile = other;
    const s = lvNewSession(this.channel, this.profile);
    s.onAudio = () => this.render();
    this.session = s;
    try {
      await s.open();
      if (seq !== this.seq) { s.close(); return; }
      const v = this.frontVideo();
      v.srcObject = s.stream;
      this.applyFront();
    } catch (err) {
      this.failed(seq, s, err);
      return;
    }
    this.awaitFirstFrame(seq, s);
  }

  async awaitFirstFrame(seq, s) {
    try {
      await lvWaitFrame(this.frontVideo(), s, 20000);
    } catch (err) {
      this.failed(seq, s, err);
      return;
    }
    if (seq !== this.seq) return;
    this.connecting = false;
    this.error = "";
    this.retryDelay = 5000;
    this.lastBytes = -1; this.stall = 0;
    s.onDrop = () => this.dropped(s);
    this.status = this.status.startsWith("Señal perdida") || this.status.includes("Reintentando") ? "" : this.status;
    const notes = [];
    if (s.kind === "mse") notes.push("H.265 por entrega alternativa: sin audio en el panel web.");
    if (lvNoDecode.has(lvMseKey(this.channel, this.profile === "Main" ? "Sub" : "Main")))
      notes.push(`Se muestra el ${this.profile === "Main" ? "principal" : "secundario"}: este navegador no reproduce el formato del ${this.profile === "Main" ? "secundario" : "principal"}.`);
    if (notes.length) this.flash(notes.join(" "), 10000);
    this.render();
    lvRefreshTreeLive();
    if (s.kind !== "mse") this.watchHevc(seq, s);
  }

  /** H.265 con B-frames por WebRTC: se aprende el canal y se reabre por MSE (el servidor reempaqueta). */
  async watchHevc(seq, s) {
    if (!(await lvLooksLikeHevcBframes(s)) || seq !== this.seq || s !== this.session || this.disposed) return;
    lvMarkMse(lvMseKey(this.channel, this.profile));
    this.status = "H.265 con B-frames: cambiando a la entrega alternativa…";
    this.render();
    this.open();
  }

  failed(seq, s, err) {
    if (err?.cancelled || seq !== this.seq || this.disposed) return;
    s.close();
    if (this.session === s) this.session = null;
    this.connecting = false;
    // Formato que esta vía no admite: se prueba la siguiente (MSE, después el otro perfil).
    if (lvCodecRejected(err) || err?.codec) {
      const next = this.codecFallback(s);
      if (next) { this.status = next; this.render(); this.open(); return; }
      err = { ...err, error: lvCodecMessage(err) };
    }
    const message = err?.error || err?.message || "No se pudo abrir el video.";
    const detail = String(err?.data?.detail || "").toLowerCase();
    // Lo que no se arregla reintentando: sin permiso, fuera de alcance, canal
    // inexistente o deshabilitado, o un formato que este navegador no decodifica.
    const fatal = err?.status === 403 || err?.status === 404 || detail.includes("codec") || err?.codec ||
      (err?.status === 422 && message.includes("deshabilitado")) || err?.status === 503 && message.includes("deshabilitada");
    this.error = message;
    this.status = fatal ? "Sin video" : "Reintentando…";
    if (!fatal) this.scheduleRetry();
    this.render();
  }

  /**
   * La vía recién probada no admite el formato del canal. Devuelve el aviso de
   * la siguiente (MSE si este navegador decodifica H.265 ahí; si no, el otro
   * perfil, que suele venir en H.264) o null si ya no queda ninguna.
   */
  codecFallback(s) {
    const key = lvMseKey(this.channel, this.profile);
    if (s.kind !== "mse" && lvHevc.mse && !lvMseChannels.has(key)) {
      lvMarkMse(key);
      return "Este navegador no recibe H.265 por WebRTC: abriendo por la entrega alternativa…";
    }
    lvNoDecode.add(key);
    const other = this.profile === "Main" ? "Sub" : "Main";
    if (lvNoDecode.has(lvMseKey(this.channel, other))) return null;
    this.profile = other;
    return `Este navegador no reproduce el ${other === "Sub" ? "principal" : "secundario"}: abriendo el ${other === "Sub" ? "secundario" : "principal"}…`;
  }

  dropped(s) {
    if (s !== this.session || this.disposed) return;
    this.session = null;
    s.close();
    this.frontVideo().srcObject = null;
    if (this.rec) this.stopRecording();
    this.connecting = true;
    this.status = "Señal perdida. Reconectando…";
    this.render();
    this.scheduleRetry(2000);
  }

  /** Vigía: sin bytes de video por ~12 s = señal perdida (MediaMTX no siempre avisa). */
  async watch() {
    const s = this.session;
    if (!s || this.connecting || this.disposed) return;
    const st = await s.videoStats();
    if (!st || s !== this.session) return;
    if (st.bytes === this.lastBytes) {
      if (++this.stall >= 3) this.dropped(s);
    } else {
      this.stall = 0;
      this.lastBytes = st.bytes;
    }
  }

  /**
   * Cambio suave de stream (P/S, maximizar): el destino se abre en el <video>
   * de atrás y se intercambia cuando ya hay imagen. Dos intentos (el primero
   * deja el pull tibio en MediaMTX). Sin éxito se conserva el actual, salvo
   * hardFallback (la vuelta a secundario al restaurar es obligatoria).
   */
  async switchProfile(target, { hardFallback = false, keepForRestore = false } = {}) {
    if (!this.session || this.connecting) {
      // Todavía conectando: se reabre directo con el perfil pedido.
      this.profile = target;
      return this.open();
    }
    if (this.pendingTarget) {
      if (this.pendingTarget === target) return;            // ya va en camino
      if (target === this.profile) { this.cancelSwitch(); return; } // "quédate como estabas"
    }
    if (target === this.profile) return;
    const targetName = target === "Main" ? "principal" : "secundario";
    if (lvNoDecode.has(lvMseKey(this.channel, target))) {
      this.flash(`Este navegador no reproduce el formato del ${targetName} de este canal: se mantiene el actual.`);
      return;
    }
    this.releaseParked();
    const seq = ++this.switchSeq, openSeq = this.seq;
    this.pendingTarget = target;
    this.status = target === "Main" ? "Cambiando a principal…" : "Cambiando a secundario…";
    this.render();
    const stale = () => seq !== this.switchSeq || openSeq !== this.seq || this.disposed;
    for (let attempt = 0; attempt < 2; attempt++) {
      const s = lvNewSession(this.channel, target);
      s.onAudio = () => this.render();
      this.pending = s;
      const back = this.backVideo();
      try {
        await s.open();
        if (stale()) { s.close(); return; }
        back.srcObject = s.stream;
        back.muted = true;
        back.play().catch(() => {});
        await lvWaitFrame(back, s, 15000);
        if (stale()) { s.close(); back.srcObject = null; return; }
      } catch (err) {
        s.close();
        if (this.pending === s) back.srcObject = null;
        if (err?.cancelled || stale()) return;
        // Formato que esta vía no admite: se reintenta por MSE (sin gastar el intento) o se desiste.
        if (lvCodecRejected(err) || err?.codec) {
          const key = lvMseKey(this.channel, target);
          if (s.kind !== "mse" && lvHevc.mse && !lvMseChannels.has(key)) { lvMarkMse(key); attempt--; continue; }
          lvNoDecode.add(key);
          break;
        }
        continue;
      }
      if (this.rec) this.stopRecording();
      const old = this.session, oldProfile = this.profile, oldVideo = this.frontVideo();
      this.front ^= 1;
      this.session = s;
      this.profile = target;
      this.pending = null;
      this.pendingTarget = null;
      this.status = "";
      s.onDrop = () => this.dropped(s);
      this.lastBytes = -1; this.stall = 0;
      this.applyFront();
      if (s.kind !== "mse") this.watchHevc(this.seq, s);
      if (keepForRestore) {
        // Restaurar el maximizado vuelve a este al instante.
        old.onDrop = () => { if (this.parked?.session === old) this.releaseParked(); };
        this.parked = { session: old, profile: oldProfile };
        oldVideo.muted = true;
      } else {
        old.close();
        oldVideo.srcObject = null;
      }
      this.render();
      if (s.kind === "mse") this.flash("H.265 por entrega alternativa: sin audio en el panel web.", 8000);
      lvScheduleLastSave();
      return;
    }
    this.pending = null;
    this.pendingTarget = null;
    if (hardFallback) {
      this.profile = target;
      this.open();
    } else if (lvNoDecode.has(lvMseKey(this.channel, target))) {
      this.flash(`Este navegador no reproduce el formato del ${targetName} de este canal: se mantiene el actual.`, 10000);
    } else {
      this.flash("El stream destino no entregó imagen: se mantiene el actual.");
    }
  }

  cancelSwitch() {
    this.switchSeq++;
    if (this.pending) {
      this.pending.close();
      this.backVideo().srcObject = null;
    }
    this.pending = null;
    this.pendingTarget = null;
    this.status = "";
    this.render();
  }

  /** Vuelve al secundario estacionado (instantáneo). false si ya no está. */
  restoreParked() {
    const p = this.parked;
    this.parked = null;
    if (!p || p.session.closed) return false;
    if (this.rec) this.stopRecording();
    const cur = this.session, curVideo = this.frontVideo();
    this.switchSeq++;
    this.front ^= 1;
    this.session = p.session;
    this.profile = p.profile;
    p.session.onDrop = () => this.dropped(p.session);
    this.lastBytes = -1; this.stall = 0;
    this.applyFront();
    cur?.close();
    curVideo.srcObject = null;
    this.render();
    lvScheduleLastSave();
    return true;
  }

  releaseParked() {
    if (!this.parked) return;
    this.parked.session.close();
    this.parked = null;
    if (!this.pending) this.backVideo().srcObject = null;
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    if (this.rec) this.stopRecording();
    this.clearRetry();
    clearTimeout(this.flashTimer);
    this.seq++; this.switchSeq++;
    this.pending?.close();
    this.releaseParked();
    this.session?.close();
    this.videos.forEach((v) => { v.srcObject = null; });
    this.stage.remove();
  }

  // --- Herramientas del operador ---------------------------------------

  async snapshot() {
    const v = this.frontVideo();
    if (!v.videoWidth) { this.flash("Todavía no hay imagen para capturar."); return; }
    const canvas = document.createElement("canvas");
    canvas.width = v.videoWidth;
    canvas.height = v.videoHeight;
    canvas.getContext("2d").drawImage(v, 0, 0);
    const png = lv.prefs.snapFormat === "png";
    const name = lvFileName(this.channel, png ? "png" : "jpg");
    canvas.toBlob((blob) => {
      if (!blob) { this.flash("No se pudo generar la captura."); return; }
      lvDownload(blob, name);
      lvAuditClient("live-snapshot", this.channel, name);
      lvStatus(`Captura guardada en Descargas: ${name}`);
    }, png ? "image/png" : "image/jpeg", 0.92);
  }

  toggleRecording() {
    if (this.rec) { this.stopRecording(); return; }
    if (!this.session || this.connecting) { this.flash("Espere a que haya imagen para grabar."); return; }
    if (typeof MediaRecorder === "undefined") { this.flash("Este navegador no permite grabar video."); return; }
    const mime = LV_REC_TYPES.find((t) => MediaRecorder.isTypeSupported(t)) || "";
    let recorder;
    try { recorder = new MediaRecorder(this.session.stream, mime ? { mimeType: mime, videoBitsPerSecond: 4000000 } : undefined); }
    catch (err) { this.flash(`No se pudo iniciar la grabación: ${err.message}`); return; }
    const ext = (recorder.mimeType || mime).includes("mp4") ? "mp4" : "webm";
    const name = lvFileName(this.channel, ext);
    const chunks = [];
    const channel = this.channel;
    recorder.ondataavailable = (e) => { if (e.data && e.data.size) chunks.push(e.data); };
    recorder.onstop = () => {
      if (!chunks.length) return;
      lvDownload(new Blob(chunks, { type: recorder.mimeType || `video/${ext}` }), name);
      lvAuditClient("live-clip-saved", channel, name);
      lvStatus(`Grabación guardada en Descargas: ${name}`);
    };
    recorder.start(1000);
    this.rec = { recorder, startedAt: Date.now(), name };
    lvAuditClient("live-clip-start", channel, name);
    this.render();
  }

  stopRecording() {
    const r = this.rec;
    this.rec = null;
    if (r && r.recorder.state !== "inactive") { try { r.recorder.stop(); } catch { /* ya detenido */ } }
    this.render();
  }

  // --- Zoom digital (recorta la imagen recibida; no mueve la cámara) ---

  applyZoom() {
    const z = this.zoom;
    this.stage.style.transform = z.s > 1.001 ? `translate(${z.x * 100}%, ${z.y * 100}%) scale(${z.s})` : "";
    this.render();
  }

  clampZoom() {
    const z = this.zoom;
    if (z.s <= 1.001) { z.s = 1; z.x = 0; z.y = 0; return; }
    z.x = Math.min(0, Math.max(1 - z.s, z.x));
    z.y = Math.min(0, Math.max(1 - z.s, z.y));
  }

  /** Acerca o aleja manteniendo fijo el punto (fx, fy) del cuadro (fracciones 0..1). */
  zoomAt(fx, fy, factor) {
    const z = this.zoom;
    const s = Math.min(Math.max(z.s * factor, 1), 8);
    z.x = fx - (fx - z.x) * (s / z.s);
    z.y = fy - (fy - z.y) * (s / z.s);
    z.s = s;
    this.clampZoom();
    this.applyZoom();
  }

  /** Lleva el rectángulo marcado (fracciones del cuadro) a todo el cuadro. */
  zoomToRect(x0, y0, x1, y1) {
    const w = Math.abs(x1 - x0), h = Math.abs(y1 - y0);
    if (w < 0.02 || h < 0.02) return;
    const z = this.zoom;
    const s = Math.min(Math.max(z.s * Math.min(1 / w, 1 / h), 1), 8);
    const cx = ((x0 + x1) / 2 - z.x) / z.s, cy = ((y0 + y1) / 2 - z.y) / z.s;
    z.s = s;
    z.x = 0.5 - cx * s;
    z.y = 0.5 - cy * s;
    this.clampZoom();
    this.applyZoom();
  }

  resetZoom() { this.zoom = { s: 1, x: 0, y: 0 }; this.applyZoom(); }
}

// ---------------------------------------------------------------------------
// Cuadros de la grilla (la posición; la cámara es su LvFeed)
// ---------------------------------------------------------------------------
class LvCell {
  constructor() {
    this.index = 0;
    this.feed = null;
    this.el = document.createElement("div");
    this.el.className = "lv-cell empty";
    this.el.innerHTML = `
      <div class="lv-bar" title="">
        <span class="lv-num"></span>
        <span class="lv-title"></span>
        <span class="lv-spin" hidden></span>
        <span class="lv-status"></span>
        <span class="lv-zoomtag" hidden></span>
        <span class="lv-actions">
          <button type="button" class="lv-act" data-act="audio"></button>
          <button type="button" class="lv-act lv-prof" data-act="profile"></button>
          <button type="button" class="lv-act" data-act="snap" title="Guardar una captura de imagen de este cuadro">${lvSvg(LV_ICO.snap, 13)}</button>
          <button type="button" class="lv-act lv-rec" data-act="rec">●</button>
          <span class="lv-rectime" hidden></span>
          <button type="button" class="lv-act lv-close" data-act="close" title="Cerrar el video de este cuadro">✕</button>
        </span>
      </div>
      <div class="lv-body">
        <div class="lv-empty">Clic para seleccionar este cuadro y doble clic en un canal</div>
        <div class="lv-error" hidden></div>
        <div class="lv-zoomrect" hidden></div>
      </div>`;
    this.bar = this.el.querySelector(".lv-bar");
    this.body = this.el.querySelector(".lv-body");
    this.el._lvCell = this;
  }
  get isEmpty() { return !this.feed; }

  /** Pone una cámara (o ninguna) en este cuadro; la anterior se libera. */
  attach(feed) {
    if (this.feed && this.feed !== feed) this.feed.dispose();
    this.feed = feed;
    if (feed) {
      feed.cell = this;
      this.body.prepend(feed.stage);
    }
    lvRenderCell(this);
  }

  /** Suelta la cámara sin liberarla (para moverla a otro cuadro). */
  detach() {
    const feed = this.feed;
    this.feed = null;
    if (feed) feed.cell = null;
    return feed;
  }

  clear() {
    if (lv.promoted && lv.promoted === this.feed) { lv.promoted = null; lv.promotedChannel = null; }
    this.feed?.dispose();
    this.feed = null;
    lvRenderCell(this);
  }
}

function lvRenderCell(cell) {
  const f = cell.feed;
  const el = cell.el;
  el.classList.toggle("empty", !f);
  el.classList.toggle("sel", lv.selected === cell);
  el.classList.toggle("drop", lv.dropCell === cell);
  el.querySelector(".lv-num").textContent = cell.index;
  const title = el.querySelector(".lv-title");
  title.textContent = f ? f.title : "Cuadro libre";
  title.classList.toggle("muted", !f);
  cell.bar.draggable = !!f;
  cell.bar.title = f ? "Arrastre este cuadro sobre otro para cambiar la cámara de ubicación (si el otro tiene video, se intercambian)" : "";
  el.querySelector(".lv-spin").hidden = !(f && (f.connecting || f.pendingTarget));
  const status = el.querySelector(".lv-status");
  status.textContent = f?.status ?? "";
  status.title = f?.error || f?.status || "";
  const ztag = el.querySelector(".lv-zoomtag");
  ztag.hidden = !(f && f.zoom.s > 1.001);
  if (f) ztag.textContent = `${f.zoom.s.toFixed(1).replace(".", ",")}×`;
  el.querySelector(".lv-actions").hidden = !f;
  const errBox = el.querySelector(".lv-error");
  // El motivo queda a la vista mientras no haya imagen (también entre reintentos).
  errBox.hidden = !(f && f.error && (f.connecting || !f.session));
  if (f) errBox.textContent = f.error;
  if (!f) return;
  const audio = el.querySelector('[data-act="audio"]');
  audio.innerHTML = lvSvg(f.audioOn ? LV_ICO.audio : LV_ICO.mute, 13);
  audio.classList.toggle("on", f.audioOn);
  audio.title = f.session && !f.connecting && !f.session.hasAudio
    ? "Esta cámara no envía audio"
    : "Audio de este cuadro (se silencia el resto)";
  const prof = el.querySelector('[data-act="profile"]');
  const shown = f.pendingTarget ?? f.profile;
  prof.textContent = shown === "Main" ? "P" : "S";
  prof.classList.toggle("on", shown === "Main");
  prof.title = shown === "Main"
    ? "Stream principal (máxima calidad) — clic para cambiar al secundario"
    : "Stream secundario (liviano) — clic para cambiar al principal";
  const rec = el.querySelector('[data-act="rec"]');
  rec.classList.toggle("on", !!f.rec);
  rec.title = f.rec ? "Grabando… (clic para detener y guardar)" : "Grabar una cápsula de video local (clic para iniciar)";
  const time = el.querySelector(".lv-rectime");
  time.hidden = !f.rec;
  if (f.rec) time.textContent = lvElapsed(f.rec.startedAt);
}

function lvElapsed(since) {
  const s = Math.floor((Date.now() - since) / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

// ---------------------------------------------------------------------------
// Utilidades
// ---------------------------------------------------------------------------
function lvStatus(message, isError) {
  const el = $("#lv-status");
  if (!el) return;
  el.textContent = message || "";
  el.classList.toggle("error", !!isError);
}

function lvNorm(s) { return String(s ?? "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase(); }

function lvSafeName(s) { return String(s ?? "").normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/[^A-Za-z0-9._-]+/g, "_").replace(/^_+|_+$/g, "") || "camara"; }

function lvFileName(channel, ext) {
  const d = new Date(), p = (n) => String(n).padStart(2, "0");
  const stamp = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
  return `${lvSafeName(channel.device?.name)}_${lvSafeName(channel.name)}_${stamp}.${ext}`;
}

function lvDownload(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

/** Bitácora (ISO 27001): capturas y grabaciones que quedan en el equipo del operador. */
function lvAuditClient(action, channel, fileName) {
  Api.post("/api/audit/client-event", {
    action, deviceId: channel.deviceId, deviceName: channel.device?.name ?? null,
    channelNumber: channel.channelNumber, channelName: channel.name,
    filePath: fileName, detail: "panel web, carpeta de descargas del navegador",
  }).catch(() => { /* la auditoría nunca bloquea al operador */ });
}

function lvDefaultProfile() {
  if (lv.prefs.profile === "main") return "Main";
  if (lv.prefs.profile === "sub") return "Sub";
  return lv.cells.length <= 4 ? "Main" : "Sub";
}

function lvAllFeeds() { return lv.cells.map((c) => c.feed).filter(Boolean); }

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
const LV_DOC_TITLE = document.title;

/** Flota en vivo: la vista en vivo con el árbol de vehículos (sin PTZ, parlantes ni pantallas auxiliares). */
function renderFleetLive() { return renderLiveView(true); }

async function renderLiveView(fleet = false) {
  const params = new URLSearchParams(location.hash.split("?")[1] || "");
  lv.fleet = fleet === true;
  lv.aux = lv.fleet ? 0 : Math.min(Math.max(Number(params.get("aux")) || 0, 0), 3);
  lv.prefs = lvLoadPrefs(lv.aux);
  lv.lastInput = Date.now();
  lv.idleWarnAt = 0;
  lv.active = true;
  lv.cells = [];
  lv.selected = null;
  lv.maximized = -1;
  lv.promoted = null;
  lv.zoomMode = false;
  lv.mergeMode = false;
  lv.search = "";
  lv.activeView = "";
  $("#page-title").textContent = lv.fleet ? "Flota en vivo" : lv.aux ? `Pantalla auxiliar ${lv.aux}` : "Vista en vivo";
  if (lv.aux) document.title = `Pantalla auxiliar ${lv.aux} — TrueCentral VMS`;
  document.body.classList.toggle("live-aux", !!lv.aux);
  $("#view").className = "view-live";

  if (typeof RTCPeerConnection === "undefined") {
    $("#view").innerHTML = `<div class="info-box" style="margin:24px">Este navegador no admite WebRTC: use Chrome, Edge o Firefox actualizados para ver video en vivo.</div>`;
    return;
  }

  const canPtz = Perms.can("live.ptz");
  $("#view").innerHTML = `
    <div class="lv-root ${lv.prefs.treeCollapsed ? "tree-collapsed" : ""} ${lv.fleet ? "lv-fleet" : ""}" id="lv-root">
      <aside class="lv-side">
        <div class="lv-side-head">
          <span class="lv-cap">${lv.fleet ? "VEHÍCULOS" : "CÁMARAS"}</span>
          <span class="lv-seg">
            ${lv.fleet
              ? `<button type="button" data-tree="device" title="Cámaras agrupadas por vehículo">Vehículo</button>
            <button type="button" data-tree="location" title="Vehículos agrupados por ubicación (base, región…), como en Recursos">Ubicación</button>`
              : `<button type="button" data-tree="device" title="Cámaras agrupadas por equipo (DVR, NVR, cámara)">Equipo</button>
            <button type="button" data-tree="location" title="Cámaras agrupadas por ubicación (sitio, edificio, sector…), como en Recursos">Ubicación</button>`}
          </span>
        </div>
        <input id="lv-search" class="lv-search" type="search" placeholder="${lv.fleet ? "Buscar vehículo, patente o cámara…" : "Buscar canal…"}" autocomplete="off">
        <div class="lv-tree" id="lv-tree"><div class="muted lv-small" style="padding:10px 14px">${lv.fleet ? "Cargando vehículos…" : "Cargando cámaras…"}</div></div>
        ${lv.aux || lv.fleet ? "" : `
        <section class="lv-panel" id="lv-spk" hidden></section>
        <section class="lv-panel ${lv.prefs.ptzOpen ? "open" : ""}" id="lv-ptz">
          <div class="lv-panel-head">
            <span class="lv-cap lv-ptz-title">PTZ — <b id="lv-ptz-name" class="off">sin cámara PTZ</b></span>
            <span class="lv-pill" id="lv-precision" hidden title="Modo precisión: movimiento PTZ a velocidad mínima (suelte Shift para salir)">PRECISIÓN</span>
            <span class="lv-pill" id="lv-joy-pill" hidden title="El joystick mueve la cámara PTZ seleccionada (Ajustes de este navegador)">JOYSTICK</span>
            <button type="button" class="lv-chev" id="lv-ptz-toggle" title="Minimizar / restaurar el control PTZ"></button>
          </div>
          <div class="lv-panel-body" id="lv-ptz-body">
            <div class="lv-ptz-pad">
              <button type="button" data-ptz="UpLeft">↖</button><button type="button" data-ptz="TiltUp">↑</button><button type="button" data-ptz="UpRight">↗</button>
              <button type="button" data-ptz="PanLeft">←</button><button type="button" disabled>·</button><button type="button" data-ptz="PanRight">→</button>
              <button type="button" data-ptz="DownLeft">↙</button><button type="button" data-ptz="TiltDown">↓</button><button type="button" data-ptz="DownRight">↘</button>
            </div>
            <div class="lv-ptz-rows">
              <span>Zoom</span><button type="button" data-ptz="ZoomOut">−</button><button type="button" data-ptz="ZoomIn">+</button>
              <span>Foco</span><button type="button" data-ptz="FocusNear">Cerca</button><button type="button" data-ptz="FocusFar">Lejos</button>
              <span>Iris</span><button type="button" data-ptz="IrisOpen">Abrir</button><button type="button" data-ptz="IrisClose">Cerrar</button>
            </div>
            <div class="lv-ptz-preset">
              <span>Preset</span>
              <input type="number" id="lv-preset" min="1" max="300" value="1">
              <button type="button" data-preset="Clear" title="Eliminar este preset en la cámara">Borrar</button>
              <button type="button" data-preset="Set" title="Guardar la posición actual como este preset">Guardar</button>
              <button type="button" data-preset="Goto" title="Mover la cámara a este preset">Ir</button>
            </div>
            <div class="lv-ptz-speed">
              <span>Velocidad</span>
              <input type="range" id="lv-speed" min="1" max="7" step="1" value="${lv.prefs.ptzSpeed}">
              <b id="lv-speed-val">${lv.prefs.ptzSpeed}</b>
            </div>
            ${canPtz ? "" : `<div class="muted lv-small" style="margin-top:6px">Sus roles no incluyen mover cámaras PTZ.</div>`}
          </div>
        </section>`}
      </aside>
      <button type="button" class="lv-handle" id="lv-handle" title="Mostrar u ocultar la lista de dispositivos"></button>
      <div class="lv-main">
        <div class="lv-toolbar">
          <div class="lv-tb-left">
            <span class="muted">División:</span>
            <span class="lv-pop-anchor">
              <button type="button" class="btn ghost lv-tb" id="lv-layout-btn"></button>
              <div class="lv-pop lv-layout-pop" id="lv-layout-pop" hidden></div>
            </span>
            <button type="button" class="btn ghost lv-tb lv-toggle" id="lv-zoom-btn" title="Zoom digital: arrastre sobre el video para marcar el área a acercar (clic derecho vuelve a 1×). La rueda del mouse acerca en cualquier momento.">${lvSvg(LV_ICO.zoom, 14)}<span>Zoom</span></button>
            <button type="button" class="btn ghost lv-tb lv-toggle" id="lv-merge-btn" title="Unir cuadros: arrastre de un cuadro a otro para unir el rectángulo en uno solo; un clic en un cuadro unido lo separa">${lvSvg(LV_ICO.views, 14)}<span>Unir</span></button>
            <button type="button" class="btn ghost lv-tb" id="lv-clear-all">Limpiar todo</button>
            <span class="lv-bulk" id="lv-bulk" hidden><i></i></span>
          </div>
          <div class="lv-tb-center">
            <span class="lv-pop-anchor">
              <button type="button" class="btn ghost lv-tb" id="lv-views-btn" title="Tableros: deje la grilla como la armó y vuelva a ella con un clic">${lvSvg(LV_ICO.views, 14)}<span>Tableros</span><b class="lv-active-view" id="lv-active-view"></b><span class="lv-caret-down">▾</span></button>
              <div class="lv-pop lv-views-pop" id="lv-views-pop" hidden></div>
            </span>
          </div>
          <div class="lv-tb-right">
            ${lv.aux || lv.fleet ? "" : `<button type="button" class="btn ghost lv-tb" id="lv-aux-btn" title="Abrir una pantalla auxiliar (hasta 3): otra ventana de vista en vivo para llevar a otro monitor">${lvSvg(LV_ICO.aux, 14)}<span>Pantalla auxiliar</span></button>`}
            <button type="button" class="btn ghost lv-tb" id="lv-full-btn" title="Pantalla completa de la grilla — presione Esc para salir">${lvSvg(LV_ICO.full, 14)}<span>Pantalla completa</span></button>
            <span class="lv-pop-anchor">
              <button type="button" class="btn ghost lv-tb lv-icon-only" id="lv-settings-btn" title="Ajustes de la vista en vivo de este navegador">${lvSvg(LV_ICO.gear, 15)}</button>
              <div class="lv-pop lv-settings-pop" id="lv-settings-pop" hidden></div>
            </span>
          </div>
        </div>
        <div class="lv-gridwrap ${lv.prefs.stretch ? "stretch" : ""}" id="lv-gridwrap"><div class="lv-grid" id="lv-grid"></div></div>
        ${lv.fleet ? `<div class="lv-idle" id="lv-idle" hidden>
          <span id="lv-idle-text"></span>
          <button type="button" class="btn small" id="lv-idle-keep">Seguir viendo</button>
        </div>` : ""}
        <div class="lv-statusbar" id="lv-status"></div>
      </div>
    </div>`;

  lvWirePage();
  lvApplyLayout(lvFindLayout(lv.prefs.layout) ?? LV_LAYOUTS[1]);
  lvRenderLayoutButton();
  lvUpdatePtz();
  lvStatus(lv.fleet
    ? "Doble clic en un vehículo abre todas sus cámaras; en una cámara, la abre en el cuadro seleccionado. El video de los vehículos usa sus datos móviles."
    : "Clic en la barra de un cuadro para seleccionarlo (borde azul); doble clic en un canal del árbol lo abre ahí.");

  await lvLoadTree();
  if (!lv.active) return;
  if (!lv.aux && !lv.fleet) lvLoadSpeakers();
  // Desde el mapa de flota ("Abrir en grilla"): las cámaras de ese vehículo.
  const fromMap = lv.fleet ? lv.devices.find((d) => d.id === Number(params.get("dev"))) : null;
  if (fromMap) await lvOpenAll(fromMap.name, fromMap.channels);
  else if (!lv.aux && lv.prefs.reopen) await lvReopenLast();
}

/** Suelta todo al salir de la página (video, grabaciones, teclado, hub). */
function liveViewLeave() {
  if (!lv.active) return;
  lv.active = false;
  lvSaveLastNow();
  lv.cells.forEach((c) => c.feed?.dispose());
  lv.cells = [];
  lv.timers.forEach((t) => clearInterval(t));
  lv.timers = [];
  clearTimeout(lv.treeReload);
  clearTimeout(lv.lastSaveTimer);
  lvStopTalk();
  lvStopPreview();
  if (lv.ptzHeld) lvPtzRelease();
  if (lv.keyHeld) { lvPtz(lv.keyHeld, true); lv.keyHeld = null; }
  lvJoyStop();
  document.removeEventListener("keydown", lvOnKeyDown, true);
  document.removeEventListener("keyup", lvOnKeyUp, true);
  document.removeEventListener("pointerdown", lvOnOutsidePointer, true);
  window.removeEventListener("blur", lvOnWindowBlur);
  VmsHub.off("DeviceStatusChanged", lvOnDeviceStatus);
  VmsHub.off("ConfigChanged", lvOnConfigChanged);
  lv.bc?.close();
  lv.bc = null;
  $("#lv-ctx")?.remove();
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  if (lv.aux) {
    document.body.classList.remove("live-aux");
    document.title = LV_DOC_TITLE;
  }
}

// La pestaña o la ventana se cierra: los streams se liberan en el servidor al
// instante (keepalive) y queda anotada la grilla para "volver a abrir".
window.addEventListener("pagehide", () => {
  if (!lv.active) return;
  lvSaveLastNow();
  lv.cells.forEach((c) => c.feed?.dispose());
});

function lvWirePage() {
  // Árbol: modo, buscador, colapsar.
  $$(".lv-seg [data-tree]").forEach((b) => {
    b.classList.toggle("on", (b.dataset.tree === "location") === lv.prefs.byLocation);
    b.addEventListener("click", () => {
      lv.prefs.byLocation = b.dataset.tree === "location";
      lvSavePrefs();
      $$(".lv-seg [data-tree]").forEach((x) => x.classList.toggle("on", x === b));
      lvRenderTree();
    });
  });
  $("#lv-search").addEventListener("input", (e) => { lv.search = e.target.value; lvRenderTree(); });
  $("#lv-handle").addEventListener("click", () => {
    lv.prefs.treeCollapsed = !lv.prefs.treeCollapsed;
    lvSavePrefs();
    $("#lv-root").classList.toggle("tree-collapsed", lv.prefs.treeCollapsed);
  });
  lvWireTree();
  lvWireGrid();

  // Barra de herramientas.
  $("#lv-layout-btn").addEventListener("click", () => lvTogglePop("lv-layout-pop", lvRenderLayoutPop));
  $("#lv-zoom-btn").addEventListener("click", () => {
    lv.zoomMode = !lv.zoomMode;
    if (lv.zoomMode && lv.mergeMode) lvSetMergeMode(false);
    $("#lv-zoom-btn").classList.toggle("on", lv.zoomMode);
    $("#lv-grid").classList.toggle("zoom-mode", lv.zoomMode);
    lvStatus(lv.zoomMode
      ? "Zoom digital: arrastre sobre el video para marcar el área a acercar; clic derecho vuelve a 1×."
      : "Zoom digital desactivado (la rueda del mouse sigue acercando; clic derecho vuelve a 1×).");
  });
  $("#lv-merge-btn").addEventListener("click", () => lvSetMergeMode(!lv.mergeMode));
  $("#lv-clear-all").addEventListener("click", () => {
    lvForgetPromotion();
    lv.maximized = -1;
    lv.cells.forEach((c) => c.clear());
    lv.activeView = "";
    lvRenderViewsButton();
    lvApplyGridGeometry();
    lvRefreshTreeLive();
    lvUpdatePtz();
    lvScheduleLastSave();
  });
  $("#lv-views-btn").addEventListener("click", () => lvTogglePop("lv-views-pop", lvRenderViewsPop));
  $("#lv-aux-btn")?.addEventListener("click", lvOpenAux);
  $("#lv-full-btn").addEventListener("click", () => {
    $("#lv-gridwrap").requestFullscreen?.().catch((err) => lvStatus(`No se pudo pasar a pantalla completa: ${err.message}`, true));
  });
  $("#lv-settings-btn").addEventListener("click", () => lvTogglePop("lv-settings-pop", lvRenderSettingsPop));

  // PTZ (panel, teclado y joystick). Los vehículos no tienen PTZ.
  if (!lv.aux && !lv.fleet) { lvWirePtz(); lvJoyStart(); }
  if (lv.fleet) lvWireIdle();

  // Teclado (PTZ) y cierre de los desplegables con clic afuera.
  document.addEventListener("keydown", lvOnKeyDown, true);
  document.addEventListener("keyup", lvOnKeyUp, true);
  document.addEventListener("pointerdown", lvOnOutsidePointer, true);
  window.addEventListener("blur", lvOnWindowBlur);

  // Tiempo real: estado de equipos y cambios de configuración.
  VmsHub.on("DeviceStatusChanged", lvOnDeviceStatus);
  VmsHub.on("ConfigChanged", lvOnConfigChanged);

  // Mover cámaras entre ventanas (principal ↔ auxiliares).
  if ("BroadcastChannel" in window) {
    lv.bc = new BroadcastChannel("tcvms-live");
    lv.bc.onmessage = (e) => {
      const m = e.data;
      if (m?.type === "moved" && m.window === lv.windowId) {
        const cell = lv.cells[m.index];
        if (cell?.feed && cell.feed.channel.id === m.channelId) {
          cell.clear();
          lvRefreshTreeLive();
          lvUpdatePtz();
          lvStatus(`La cámara del cuadro ${m.index + 1} se movió a otra ventana.`);
        }
      }
    };
  }

  // Reloj de grabaciones y vigía de señal.
  lv.timers.push(setInterval(() => {
    for (const c of lv.cells) if (c.feed?.rec) { const t = c.el.querySelector(".lv-rectime"); if (t) t.textContent = lvElapsed(c.feed.rec.startedAt); }
  }, 1000));
  lv.timers.push(setInterval(() => { for (const f of lvAllFeeds()) f.watch(); }, 4000));
}

// ---------------------------------------------------------------------------
// Flota: corte por inactividad. El video de un vehículo viaja por su 4G: si el operador no toca la página en
// lv.prefs.idleMinutes, se avisa y, sin respuesta en LV_IDLE_WARN_SECONDS, se cierran los cuadros (queda en la bitácora).
// ---------------------------------------------------------------------------
function lvWireIdle() {
  const touch = () => {
    lv.lastInput = Date.now();
    if (lv.idleWarnAt) lvIdleHide();
  };
  for (const type of ["pointerdown", "keydown", "wheel"]) $("#lv-root").addEventListener(type, touch, { passive: true });
  $("#lv-idle-keep").addEventListener("click", touch);
  lv.timers.push(setInterval(lvIdleTick, 1000));
}

function lvIdleHide() {
  lv.idleWarnAt = 0;
  const box = $("#lv-idle");
  if (box) box.hidden = true;
}

function lvIdleTick() {
  const minutes = Number(lv.prefs.idleMinutes) || 0;
  const feeds = lvAllFeeds();
  if (!minutes || !feeds.length) { if (lv.idleWarnAt) lvIdleHide(); return; }
  const now = Date.now();
  if (!lv.idleWarnAt) {
    if (now - lv.lastInput < minutes * 60000) return;
    lv.idleWarnAt = now;
    $("#lv-idle").hidden = false;
  }
  const left = LV_IDLE_WARN_SECONDS - Math.floor((now - lv.idleWarnAt) / 1000);
  if (left > 0) {
    $("#lv-idle-text").textContent = `Sin actividad hace ${minutes} min: el video de los vehículos se corta en ${left} s para no gastar sus datos móviles.`;
    return;
  }
  lvIdleHide();
  const names = [...new Set(feeds.map((f) => f.channel.device?.name).filter(Boolean))];
  lvForgetPromotion();
  lv.maximized = -1;
  lv.cells.forEach((c) => c.clear());
  lvApplyGridGeometry();
  lvRefreshTreeLive();
  lvStatus(`Se cerró el video de ${feeds.length} cámara(s) por inactividad (configurable en Ajustes de este navegador).`);
  Api.post("/api/audit/client-event", {
    action: "fleet-live-idle-stopped",
    detail: `Se cerraron ${feeds.length} cámara(s) de ${names.join(", ")} tras ${minutes} min sin actividad en el panel web.`,
  }).catch(() => { /* la auditoría nunca bloquea al operador */ });
}

function lvOnWindowBlur() {
  // Una tecla, un botón o el joystick sostenidos al cambiar de ventana no deben dejar la cámara moviéndose
  // (el joystick sigue si está configurado para operar en segundo plano).
  if (lv.keyHeld) { lvPtz(lv.keyHeld, true); lv.keyHeld = null; }
  if (lv.ptzHeld) lvPtzRelease();
  lvJoyOnBlur();
  lvSetPrecision(false);
}

// ---------------------------------------------------------------------------
// Desplegables de la barra
// ---------------------------------------------------------------------------
function lvTogglePop(id, render) {
  const pop = $("#" + id);
  const open = pop.hidden;
  $$(".lv-pop").forEach((p) => { p.hidden = true; });
  if (open) { render(); pop.hidden = false; }
}

function lvClosePops() { $$(".lv-pop").forEach((p) => { p.hidden = true; }); }

function lvOnOutsidePointer(e) {
  if (!e.target.closest?.(".lv-pop-anchor")) lvClosePops();
  if (!e.target.closest?.("#lv-ctx")) $("#lv-ctx")?.remove();
}

function lvRenderLayoutButton() {
  const l = lv.layout;
  const btn = $("#lv-layout-btn");
  if (!btn || !l) return;
  btn.innerHTML = `${lvLayoutPreview(l, 22, 15)}<span>${esc(l.name)}</span><span class="lv-caret-down">▾</span>`;
  btn.title = lvLayoutLabel(l);
}

function lvRenderLayoutPop() {
  $("#lv-layout-pop").innerHTML = `
    <div class="lv-pop-title">División de pantalla</div>
    ${LV_LAYOUT_GROUPS.map((g) => `
      <div class="lv-lgroup">
        <div class="lv-lgroup-title">${esc(g.title)}</div>
        <div class="lv-lgrid">${g.layouts.map((l) => `
          <button type="button" class="lv-lbtn ${lv.layout?.key === l.key ? "on" : ""}" data-layout="${esc(l.key)}" title="${esc(l.key)}">
            ${lvLayoutPreview(l, 36, 26)}<span>${esc(l.name)}</span>
          </button>`).join("")}</div>
      </div>`).join("")}`;
  $$("#lv-layout-pop [data-layout]").forEach((b) => b.addEventListener("click", () => {
    const layout = lvFindLayout(b.dataset.layout);
    lvClosePops();
    if (!layout) return;
    lvApplyLayout(layout);
    lv.prefs.layout = layout.key;
    lvSavePrefs();
    lvScheduleLastSave();
  }));
}

function lvRenderSettingsPop() {
  const p = lv.prefs;
  $("#lv-settings-pop").innerHTML = `
    <div class="lv-pop-title">Ajustes de este navegador</div>
    <label class="lv-set-row"><span>Stream al abrir una cámara</span>
      <select id="lv-set-profile">
        <option value="auto" ${p.profile === "auto" ? "selected" : ""}>Automático (principal hasta 4 cuadros)</option>
        <option value="main" ${p.profile === "main" ? "selected" : ""}>Siempre principal</option>
        <option value="sub" ${p.profile === "sub" ? "selected" : ""}>Siempre secundario</option>
      </select></label>
    <label class="lv-set-row"><span>Formato de las capturas</span>
      <select id="lv-set-snap">
        <option value="jpg" ${p.snapFormat !== "png" ? "selected" : ""}>JPG</option>
        <option value="png" ${p.snapFormat === "png" ? "selected" : ""}>PNG</option>
      </select></label>
    <label class="lv-set-check"><input type="checkbox" id="lv-set-fit" ${p.fitDevice ? "checked" : ""}>
      <span>Al abrir un equipo completo, grilla a la medida de sus canales</span></label>
    <label class="lv-set-check"><input type="checkbox" id="lv-set-stretch" ${p.stretch ? "checked" : ""}>
      <span>Estirar el video al tamaño del cuadro (sin mantener la proporción)</span></label>
    ${lv.aux ? "" : `<label class="lv-set-check"><input type="checkbox" id="lv-set-reopen" ${p.reopen ? "checked" : ""}>
      <span>Volver a abrir las cámaras de la última sesión al entrar</span></label>`}
    ${lv.fleet ? `<label class="lv-set-row"><span>Cortar el video sin actividad</span>
      <select id="lv-set-idle" title="El video de los vehículos usa sus datos móviles">
        ${[2, 5, 10, 15, 30].map((m) => `<option value="${m}" ${Number(p.idleMinutes) === m ? "selected" : ""}>A los ${m} minutos</option>`).join("")}
        <option value="0" ${Number(p.idleMinutes) === 0 ? "selected" : ""}>Nunca</option>
      </select></label>` : ""}
    <div class="muted lv-small" style="margin-top:8px">Las capturas y grabaciones se guardan en la carpeta de descargas del navegador.</div>
    <div class="muted lv-small" style="margin-top:4px">H.265 en este navegador: por WebRTC ${lvHevc.webrtc ? "sí" : "no"};
      por la entrega alternativa (sin audio) ${lvHevc.mse ? "sí" : "no"}.</div>
    ${lv.aux || lv.fleet ? "" : lvJoySettingsHtml()}`;
  $("#lv-set-idle")?.addEventListener("change", (e) => { p.idleMinutes = Number(e.target.value); lvSavePrefs(); lv.lastInput = Date.now(); });
  $("#lv-set-profile").addEventListener("change", (e) => { p.profile = e.target.value; lvSavePrefs(); });
  $("#lv-set-snap").addEventListener("change", (e) => { p.snapFormat = e.target.value; lvSavePrefs(); });
  $("#lv-set-fit").addEventListener("change", (e) => { p.fitDevice = e.target.checked; lvSavePrefs(); });
  $("#lv-set-stretch").addEventListener("change", (e) => {
    p.stretch = e.target.checked; lvSavePrefs();
    $("#lv-gridwrap").classList.toggle("stretch", p.stretch);
  });
  $("#lv-set-reopen")?.addEventListener("change", (e) => { p.reopen = e.target.checked; lvSavePrefs(); });
  if (!lv.aux && !lv.fleet) lvJoyWireSettings();
}

// ---------------------------------------------------------------------------
// Grilla
// ---------------------------------------------------------------------------
/** Cambia la división: los cuadros que siguen conservan su video; los que sobran se liberan. */
function lvApplyLayout(layout) {
  lvForgetPromotion();
  lv.maximized = -1;
  lv.layout = layout;
  const grid = $("#lv-grid");
  const count = layout.cells.length;
  while (lv.cells.length > count) {
    const cell = lv.cells.pop();
    cell.clear();
    cell.el.remove();
  }
  while (lv.cells.length < count) {
    const cell = new LvCell();
    lv.cells.push(cell);
    grid.append(cell.el);
  }
  lv.cells.forEach((c, i) => { c.index = i + 1; lvRenderCell(c); });
  if (lv.selected && !lv.cells.includes(lv.selected)) lv.selected = null;
  lvApplyGridGeometry();
  lvRenderLayoutButton();
  lvRefreshTreeLive();
  lvUpdatePtz();
}

function lvApplyGridGeometry() {
  const grid = $("#lv-grid");
  const l = lv.layout;
  if (!grid || !l) return;
  const maxed = lv.maximized >= 0;
  grid.classList.toggle("maxed", maxed);
  grid.style.gridTemplateColumns = maxed ? "1fr" : `repeat(${l.columns}, minmax(0, 1fr))`;
  grid.style.gridTemplateRows = maxed ? "1fr" : `repeat(${l.rows}, minmax(0, 1fr))`;
  lv.cells.forEach((c, i) => {
    const r = l.cells[i];
    if (maxed) {
      c.el.style.display = i === lv.maximized ? "" : "none";
      c.el.style.gridColumn = c.el.style.gridRow = i === lv.maximized ? "1 / -1" : "";
    } else {
      c.el.style.display = "";
      c.el.style.gridColumn = `${r.col + 1} / span ${r.colSpan}`;
      c.el.style.gridRow = `${r.row + 1} / span ${r.rowSpan}`;
    }
  });
}

// ---------------------------------------------------------------------------
// Ampliar la grilla arrastrando: una cámara del árbol llevada al borde de la
// grilla muestra la franja nueva (sus cuadros, el de destino resaltado) y la
// grilla se aleja un poco para dejarla ver; al soltar se agrega la franja con
// la cámara en el cuadro marcado. Los cuadros que ya estaban conservan su
// video (el <video> viaja al cuadro nuevo, no se reabre).
// ---------------------------------------------------------------------------

/** No más cuadros que cámaras en el árbol (las habilitadas por la licencia) ni que el tope web. */
function lvGrowLimit() { return Math.min(LV_MAX_CELLS, Math.max(lv.cells.length, lv.channels.size)); }

function lvIsChannelDrag(e) {
  const types = e.dataTransfer?.types ?? [];
  return types.includes("text/x-tcvms-channel") && !types.includes("text/x-tcvms-cell");
}

/** Borde bajo el puntero; con la franja abierta, todo su ancho la mantiene (no parpadea). */
function lvGrowEdgeAt(e) {
  const wrap = $("#lv-gridwrap");
  if (!wrap || lv.maximized >= 0) return null;
  const r = wrap.getBoundingClientRect();
  const x = e.clientX - r.left, y = e.clientY - r.top;
  if (x < 0 || y < 0 || x > r.width || y > r.height) return null;
  const strip = lv.grow?.el.getBoundingClientRect();
  if (strip && ({
    left: e.clientX <= strip.right, right: e.clientX >= strip.left,
    top: e.clientY <= strip.bottom, bottom: e.clientY >= strip.top,
  })[lv.grow.edge]) return lv.grow.edge;
  const bandX = Math.min(Math.max(r.width * 0.06, 14), 48), bandY = Math.min(Math.max(r.height * 0.06, 14), 48);
  let best = null, depth = 1;
  for (const [edge, dist, band] of [["left", x, bandX], ["right", r.width - x, bandX], ["top", y, bandY], ["bottom", r.height - y, bandY]])
    if (dist < band && dist / band < depth) { best = edge; depth = dist / band; }
  return best;
}

/** Arrastre sobre la grilla: true si quedó (o sigue) la franja a la vista; el arrastre es de ella. */
function lvGrowTrack(e) {
  if (!lvIsChannelDrag(e) || !lv.layout) return false;
  const edge = lvGrowEdgeAt(e);
  if (!edge) { lv.growPending = null; lvGrowEnd(); return false; }
  if (!lv.grow) {
    // Pasar por el borde camino a un cuadro no abre nada: la franja aparece
    // recién si el puntero se queda en el borde un momento (el navegador
    // repite dragover mientras el puntero sigue encima).
    if (lv.growPending?.edge !== edge) lv.growPending = { edge, since: performance.now(), told: false };
    if (performance.now() - lv.growPending.since < LV_GROW_DWELL_MS) return false;
  }
  if (lv.grow?.edge !== edge) {
    const blocked = lvGrowBlocked(lv.layout, edge, lvGrowLimit());
    if (blocked) {
      // El aviso de tope, una sola vez por espera en el borde.
      if (lv.growPending && !lv.growPending.told) { lvStatus(blocked, true); lv.growPending.told = true; }
      lvGrowEnd();
      return false;
    }
    lvGrowShow(edge);
  }
  const strip = lv.grow.el.getBoundingClientRect();
  const count = lv.grow.slots.length;
  const along = lvSideways(edge) ? (e.clientY - strip.top) / strip.height : (e.clientX - strip.left) / strip.width;
  const slot = Math.min(Math.max(Math.floor(along * count), 0), count - 1);
  if (slot !== lv.grow.slot) {
    lv.grow.slot = slot;
    lv.grow.slots.forEach((s, i) => s.classList.toggle("on", i === slot));
  }
  e.preventDefault();
  e.dataTransfer.dropEffect = "copy";
  if (lv.dropCell) { const prev = lv.dropCell; lv.dropCell = null; lvRenderCell(prev); }
  return true;
}

/** Dibuja la grilla como quedaría: los cuadros corridos y la franja en la fila o columna nueva. */
function lvGrowShow(edge) {
  lvGrowEnd();
  const grid = $("#lv-grid"), l = lv.layout;
  const sideways = lvSideways(edge);
  const columns = l.columns + (sideways ? 1 : 0), rows = l.rows + (sideways ? 0 : 1);
  const dx = edge === "left" ? 1 : 0, dy = edge === "top" ? 1 : 0;
  grid.style.gridTemplateColumns = `repeat(${columns}, minmax(0, 1fr))`;
  grid.style.gridTemplateRows = `repeat(${rows}, minmax(0, 1fr))`;
  lv.cells.forEach((c, i) => {
    const r = l.cells[i];
    c.el.style.gridColumn = `${r.col + 1 + dx} / span ${r.colSpan}`;
    c.el.style.gridRow = `${r.row + 1 + dy} / span ${r.rowSpan}`;
  });
  const count = sideways ? l.rows : l.columns;
  const el = document.createElement("div");
  el.className = `lv-strip ${sideways ? "col" : "row"}`;
  el.style.gridColumn = sideways ? `${edge === "left" ? 1 : columns} / span 1` : "1 / -1";
  el.style.gridRow = sideways ? "1 / -1" : `${edge === "top" ? 1 : rows} / span 1`;
  el.style.setProperty("--n", count);
  el.innerHTML = Array.from({ length: count }, () => `
    <i><span class="lv-strip-plus">+</span><b>${sideways ? "Columna nueva" : "Fila nueva"}</b><em>${l.columns}×${l.rows} → ${columns}×${rows}</em></i>`).join("");
  grid.append(el);
  grid.classList.add("growing");
  lv.grow = { edge, slot: -1, el, slots: [...el.children] };
}

/** La franja se recoge (el arrastre salió del borde, terminó o se soltó). */
function lvGrowEnd() {
  if (!lv.grow) return;
  lv.grow.el.remove();
  lv.grow = null;
  $("#lv-grid")?.classList.remove("growing");
  lvApplyGridGeometry();
}

/** Soltar con la franja abierta: se agrega la franja y la cámara abre en el cuadro marcado. */
function lvGrowDrop(e) {
  lv.growPending = null;
  if (!lv.grow) return false;
  e.preventDefault();
  const { edge } = lv.grow, slot = Math.max(lv.grow.slot, 0);
  lvGrowEnd();
  const ch = lv.channels.get(Number(e.dataTransfer.getData("text/x-tcvms-channel")));
  const growth = ch && lvGrow(lv.layout, edge, lvGrowLimit());
  if (!growth) return true;
  // El video viaja: se sueltan todas las cámaras y cada una vuelve en su
  // lugar nuevo, todo en el mismo turno (el <video> no alcanza a pausarse).
  const feeds = lv.cells.map((c) => c.detach());
  lvApplyLayout(growth.layout);
  feeds.forEach((f, i) => { if (f) lv.cells[growth.placement[i]].attach(f); });
  const cell = lv.cells[growth.strip[Math.min(slot, growth.strip.length - 1)]];
  lvSelectCell(cell);
  lvOpenInCell(ch, cell, lvDefaultProfile());
  if (lvFindLayout(growth.layout.key)) { lv.prefs.layout = growth.layout.key; lvSavePrefs(); }
  lvScheduleLastSave();
  lvStatus(`${({ left: "Columna nueva a la izquierda", right: "Columna nueva a la derecha", top: "Fila nueva arriba", bottom: "Fila nueva abajo" })[edge]}: ` +
    `la grilla pasa a ${growth.layout.columns}×${growth.layout.rows} (${growth.layout.cells.length} cuadros).`);
  return true;
}

// ---------------------------------------------------------------------------
// Modo «Unir» (botón de la barra; Esc termina): apretar en un cuadro y
// arrastrar hasta otro dibuja el recuadro de lo que quedaría unido (acento, o
// rojo si corta un cuadro); al soltar se une. Un clic en un cuadro unido lo
// separa. Queda la cámara del cuadro de inicio (o la primera); las demás de la
// selección se cierran.
// ---------------------------------------------------------------------------
function lvSetMergeMode(on) {
  lv.mergeMode = on;
  $("#lv-merge-btn")?.classList.toggle("on", on);
  $("#lv-grid")?.classList.toggle("merge-mode", on);
  if (on && lv.zoomMode) $("#lv-zoom-btn")?.click();
  lvStatus(on
    ? "Unir cuadros: arrastre de un cuadro a otro para unirlos en uno; clic en un cuadro unido lo separa. Esc termina."
    : "Clic en la barra de un cuadro para seleccionarlo (borde azul); doble clic en un canal del árbol lo abre ahí.");
}

function lvMergeStart(cell, e) {
  if (lv.maximized >= 0) { lvStatus("Restaure el cuadro maximizado para unir cuadros.", true); return; }
  const from = lv.cells.indexOf(cell);
  const band = document.createElement("div");
  band.className = "lv-mergeband";
  band.innerHTML = "<span></span>";
  $("#lv-grid").append(band);
  let to = from;
  const draw = () => {
    const { rect, inside, cut } = lvMergeSelection(lv.layout, from, to);
    band.style.gridColumn = `${rect.col + 1} / span ${rect.colSpan}`;
    band.style.gridRow = `${rect.row + 1} / span ${rect.rowSpan}`;
    band.classList.toggle("bad", cut.length > 0);
    band.firstChild.textContent = cut.length ? "Corta un cuadro: no se puede unir"
      : inside.length < 2 ? "Arrastre hasta otro cuadro" : `Unir ${inside.length} cuadros`;
    lv.cells.forEach((c, i) => {
      c.el.classList.toggle("merge-in", inside.includes(i) && !cut.length);
      c.el.classList.toggle("merge-cut", cut.includes(i) || (cut.length > 0 && inside.includes(i)));
    });
  };
  const move = (ev) => {
    const under = document.elementFromPoint(ev.clientX, ev.clientY)?.closest?.(".lv-cell")?._lvCell;
    const index = under ? lv.cells.indexOf(under) : -1;
    if (index >= 0 && index !== to) { to = index; draw(); }
  };
  const up = () => {
    window.removeEventListener("pointermove", move);
    window.removeEventListener("pointerup", up);
    band.remove();
    lv.cells.forEach((c) => c.el.classList.remove("merge-in", "merge-cut"));
    if (to === from) lvSplitCell(from);
    else lvMergeCells(from, to);
  };
  draw();
  window.addEventListener("pointermove", move);
  window.addEventListener("pointerup", up);
  e.preventDefault();
}

/** Reacomoda las cámaras en la división nueva sin reabrirlas (el <video> viaja de cuadro). */
function lvRelayout(layout, placement, keep = () => true) {
  const feeds = lv.cells.map((c) => c.detach());
  lvApplyLayout(layout);
  feeds.forEach((f, i) => { if (f && keep(f, i)) lv.cells[placement[i]].attach(f); });
  if (lvFindLayout(layout.key)) { lv.prefs.layout = layout.key; lvSavePrefs(); }
  lvRefreshTreeLive();
  lvScheduleLastSave();
}

function lvMergeCells(from, to) {
  const m = lvMerge(lv.layout, from, to);
  if (!m.layout) { lvStatus(m.reason, true); return; }
  const keep = lv.cells[from].feed ?? m.inside.map((i) => lv.cells[i].feed).find(Boolean) ?? null;
  const closed = [];
  for (const i of m.inside) {
    const f = lv.cells[i].feed;
    if (f && f !== keep) { closed.push(f.title); lv.cells[i].clear(); }
  }
  lvRelayout(m.layout, m.placement);
  lvSelectCell(lv.cells[m.merged]);
  lvStatus(`${m.inside.length} cuadros unidos en uno.` + (closed.length ? ` Se cerró: ${closed.map((t) => `"${t}"`).join(", ")}.` : ""));
}

function lvSplitCell(index) {
  const s = lvSplit(lv.layout, index);
  if (!s) { lvStatus("Ese cuadro no está unido: arrastre hasta otro cuadro para unirlos."); return; }
  lvRelayout(s.layout, s.placement);
  lvSelectCell(lv.cells[s.first]);
  lvStatus(`Cuadro separado: la grilla queda en ${s.layout.cells.length} cuadros.`);
}

function lvSelectCell(cell) {
  if (lv.selected === cell) return;
  const prev = lv.selected;
  lv.selected = cell;
  if (prev) lvRenderCell(prev);
  if (cell) lvRenderCell(cell);
  // Selección grilla → árbol: el canal del cuadro queda marcado.
  lv.selChannelId = cell?.feed?.channel.id ?? lv.selChannelId;
  lvMarkTreeSelection(true);
  lvUpdatePtz();
}

function lvCellFromEvent(e) { return e.target.closest?.(".lv-cell")?._lvCell ?? null; }

function lvWireGrid() {
  const grid = $("#lv-grid");

  grid.addEventListener("pointerdown", (e) => {
    const cell = lvCellFromEvent(e);
    if (!cell) return;
    if (lv.mergeMode && e.button === 0 && !e.target.closest(".lv-act")) { lvMergeStart(cell, e); return; }
    lvSelectCell(cell);
    if (e.button !== 0 || !e.target.closest(".lv-body") || !cell.feed) return;
    const f = cell.feed;
    const rect = cell.body.getBoundingClientRect();
    const fx = (e.clientX - rect.left) / rect.width, fy = (e.clientY - rect.top) / rect.height;
    if (lv.zoomMode) {
      // Marcar el rectángulo a acercar.
      const box = cell.body.querySelector(".lv-zoomrect");
      const move = (ev) => {
        const x = Math.min(Math.max((ev.clientX - rect.left) / rect.width, 0), 1);
        const y = Math.min(Math.max((ev.clientY - rect.top) / rect.height, 0), 1);
        Object.assign(box.style, {
          left: `${Math.min(fx, x) * 100}%`, top: `${Math.min(fy, y) * 100}%`,
          width: `${Math.abs(x - fx) * 100}%`, height: `${Math.abs(y - fy) * 100}%`,
        });
        box.hidden = false;
        box._end = [x, y];
      };
      const up = () => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        box.hidden = true;
        if (box._end) f.zoomToRect(fx, fy, box._end[0], box._end[1]);
        box._end = null;
      };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
      e.preventDefault();
    } else if (f.zoom.s > 1.001) {
      // Con zoom, arrastrar desplaza la imagen.
      let lastX = e.clientX, lastY = e.clientY;
      const move = (ev) => {
        f.zoom.x += (ev.clientX - lastX) / rect.width;
        f.zoom.y += (ev.clientY - lastY) / rect.height;
        lastX = ev.clientX; lastY = ev.clientY;
        f.clampZoom();
        f.applyZoom();
      };
      const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
      e.preventDefault();
    }
  });

  grid.addEventListener("click", (e) => {
    const btn = e.target.closest(".lv-act");
    const cell = lvCellFromEvent(e);
    if (!btn || !cell?.feed) return;
    const f = cell.feed;
    switch (btn.dataset.act) {
      case "audio": lvToggleAudio(f); break;
      case "profile": f.switchProfile((f.pendingTarget ?? f.profile) === "Main" ? "Sub" : "Main"); break;
      case "snap": f.snapshot(); break;
      case "rec": f.toggleRecording(); break;
      case "close":
        if (lv.maximized === lv.cells.indexOf(cell)) { lv.maximized = -1; lvApplyGridGeometry(); }
        cell.clear();
        lvRefreshTreeLive();
        lvUpdatePtz();
        lvScheduleLastSave();
        break;
    }
  });

  grid.addEventListener("dblclick", (e) => {
    if (e.target.closest(".lv-bar")) return;
    const cell = lvCellFromEvent(e);
    if (cell) lvToggleMaximize(cell);
  });

  grid.addEventListener("wheel", (e) => {
    const cell = lvCellFromEvent(e);
    if (!cell?.feed || !e.target.closest(".lv-body")) return;
    e.preventDefault();
    const rect = cell.body.getBoundingClientRect();
    cell.feed.zoomAt((e.clientX - rect.left) / rect.width, (e.clientY - rect.top) / rect.height, e.deltaY < 0 ? 1.2 : 1 / 1.2);
  }, { passive: false });

  grid.addEventListener("contextmenu", (e) => {
    const cell = lvCellFromEvent(e);
    if (!cell?.feed || !e.target.closest(".lv-body")) return;
    e.preventDefault();
    cell.feed.resetZoom();
  });

  // Ampliar la grilla: el borde se mira ANTES que el cuadro de abajo (captura
  // en el contenedor, que también recibe el margen que deja el alejamiento).
  const wrap = $("#lv-gridwrap");
  wrap.addEventListener("dragover", (e) => { if (lvGrowTrack(e)) e.stopPropagation(); }, true);
  wrap.addEventListener("drop", (e) => { if (lvGrowDrop(e)) e.stopPropagation(); }, true);
  wrap.addEventListener("dragleave", (e) => { if (!wrap.contains(e.relatedTarget)) { lv.growPending = null; lvGrowEnd(); } });
  // Arrastre terminado en cualquier lado (soltado fuera, Esc): la franja se recoge.
  if (!lv.growWired) { document.addEventListener("dragend", () => { lv.growPending = null; lvGrowEnd(); }); lv.growWired = true; }

  // Arrastre de un cuadro sobre otro.
  grid.addEventListener("dragstart", (e) => {
    const bar = e.target.closest?.(".lv-bar");
    const cell = lvCellFromEvent(e);
    if (!bar || !cell?.feed) { if (e.target.closest?.(".lv-cell")) e.preventDefault(); return; }
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/x-tcvms-cell", JSON.stringify({
      window: lv.windowId, index: lv.cells.indexOf(cell), channelId: cell.feed.channel.id, profile: cell.feed.profile,
    }));
  });
  grid.addEventListener("dragover", (e) => {
    const types = e.dataTransfer?.types ?? [];
    if (!types.includes("text/x-tcvms-cell") && !types.includes("text/x-tcvms-channel")) return;
    const cell = lvCellFromEvent(e);
    if (!cell) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = types.includes("text/x-tcvms-cell") ? "move" : "copy";
    if (lv.dropCell !== cell) {
      const prev = lv.dropCell;
      lv.dropCell = cell;
      if (prev) lvRenderCell(prev);
      lvRenderCell(cell);
    }
  });
  grid.addEventListener("dragleave", (e) => {
    const cell = lvCellFromEvent(e);
    if (cell && cell === lv.dropCell && !cell.el.contains(e.relatedTarget)) {
      lv.dropCell = null;
      lvRenderCell(cell);
    }
  });
  grid.addEventListener("drop", (e) => {
    const target = lvCellFromEvent(e);
    const prev = lv.dropCell;
    lv.dropCell = null;
    if (prev) lvRenderCell(prev);
    if (!target) return;
    e.preventDefault();
    const channelId = Number(e.dataTransfer.getData("text/x-tcvms-channel"));
    if (channelId) {
      const ch = lv.channels.get(channelId);
      if (ch) lvOpenInCell(ch, target, lvDefaultProfile());
      return;
    }
    let moved = null;
    try { moved = JSON.parse(e.dataTransfer.getData("text/x-tcvms-cell") || "null"); } catch { /* otro contenido */ }
    if (!moved) return;
    if (moved.window === lv.windowId) {
      const source = lv.cells[moved.index];
      if (source) lvSwapCells(source, target);
    } else {
      // Desde otra ventana: se abre aquí y la otra suelta su cuadro.
      const ch = lv.channels.get(moved.channelId);
      if (!ch) { lvStatus("Esa cámara no está en el árbol de esta ventana.", true); return; }
      lvOpenInCell(ch, target, moved.profile || lvDefaultProfile());
      lv.bc?.postMessage({ type: "moved", window: moved.window, index: moved.index, channelId: moved.channelId });
    }
  });
}

/** Arrastrar un cuadro sobre otro: si el destino tiene video se intercambian; si no, la cámara se muda. */
function lvSwapCells(source, target) {
  if (source === target || (!source.feed && !target.feed)) return;
  const exchange = !!target.feed;
  const moved = source.feed?.title ?? "", displaced = target.feed?.title ?? "";
  const a = source.detach(), b = target.detach();
  target.attach(a);
  source.attach(b);
  lvSelectCell(target);
  lvRefreshTreeLive();
  lvScheduleLastSave();
  lvStatus(exchange
    ? `Cuadros ${source.index} y ${target.index} intercambiados: "${moved}" ↔ "${displaced}".`
    : `"${moved}" movida del cuadro ${source.index} al ${target.index}.`);
}

/** Doble clic: ver ese cuadro solo (sube a principal) o volver a la grilla (vuelve a secundario). */
function lvToggleMaximize(cell) {
  const index = lv.cells.indexOf(cell);
  if (index < 0) return;
  if (lv.maximized === index) {
    lv.maximized = -1;
    const f = cell.feed;
    if (f && lv.promoted === f && f.channel === lv.promotedChannel) {
      if (f.profile === "Main") { if (!f.restoreParked()) f.switchProfile("Sub", { hardFallback: true }); }
      else f.cancelSwitch();
    }
    lv.promoted = null;
    lv.promotedChannel = null;
    lvApplyGridGeometry();
    return;
  }
  if (!cell.feed) return;
  lvForgetPromotion();
  lv.maximized = index;
  lvSelectCell(cell);
  lvApplyGridGeometry();
  const f = cell.feed;
  if (f.profile === "Sub") {
    lv.promoted = f;
    lv.promotedChannel = f.channel;
    f.switchProfile("Main", { keepForRestore: true });
  }
}

/** Cambiar de división deshace el maximizado sin volver a secundario: el estacionado se libera. */
function lvForgetPromotion() {
  lv.promoted?.releaseParked();
  lv.promoted = null;
  lv.promotedChannel = null;
}

/** Audio exclusivo: encender uno silencia los demás. */
function lvToggleAudio(feed) {
  if (feed.audioOn) {
    feed.audioOn = false;
  } else {
    if (feed.session && !feed.connecting && !feed.session.hasAudio) { feed.flash("Esta cámara no envía audio."); return; }
    for (const f of lvAllFeeds()) if (f !== feed && f.audioOn) { f.audioOn = false; f.applyFront(); f.render(); }
    feed.audioOn = true;
  }
  feed.applyFront();
  feed.render();
}

/** Abre un canal: en el cuadro seleccionado, el primero libre o el primero; la selección avanza. */
function lvOpenChannel(ch) {
  const cell = lv.selected ?? lv.cells.find((c) => c.isEmpty) ?? lv.cells[0];
  if (!cell) return;
  const next = lv.cells.indexOf(cell) + 1;
  lvOpenInCell(ch, cell, lvDefaultProfile());
  lvSelectCell(next < lv.cells.length ? lv.cells[next] : null);
}

function lvOpenInCell(ch, cell, profile) {
  if (lv.maximized >= 0 && lv.cells[lv.maximized] !== cell) { lv.maximized = -1; lvApplyGridGeometry(); }
  if (lv.promoted === cell.feed) lvForgetPromotion();
  const feed = new LvFeed(ch, profile);
  cell.attach(feed);
  const opened = feed.open();
  lvRefreshTreeLive();
  if (lv.selected === cell) lvUpdatePtz();
  lvScheduleLastSave();
  return opened;
}

/** Apertura masiva (equipo, ubicación, vista guardada): de a 4 negociaciones a la vez. */
async function lvOpenMany(label, items) {
  const bulk = $("#lv-bulk");
  const total = items.length;
  let done = 0;
  lv.bulk = label;
  // El avance va en la barra de estado; en la de herramientas, solo la barra animada.
  const show = () => {
    const text = `Abriendo canales de "${label}"… ${done}/${total}`;
    if (bulk) { bulk.hidden = false; bulk.title = text; }
    lvStatus(text);
  };
  show();
  let next = 0;
  const worker = async () => {
    while (next < items.length && lv.active) {
      const { ch, cell, profile } = items[next++];
      try { await lvOpenInCell(ch, cell, profile); } catch { /* el cuadro muestra su error */ }
      done++;
      show();
    }
  };
  await Promise.all([worker(), worker(), worker(), worker()]);
  lv.bulk = null;
  if (bulk) bulk.hidden = true;
  lvStatus(`${total} canal(es) de "${label}" en pantalla.`);
}

/** Doble clic en un equipo o "Abrir sus cámaras" de una ubicación. */
async function lvOpenAll(label, channels) {
  if (!channels.length) { lvStatus(`"${label}" no tiene canales habilitados.`); return; }
  const total = channels.length;
  if (total > LV_MAX_CELLS) channels = channels.slice(0, LV_MAX_CELLS);
  if (lv.prefs.fitDevice) {
    lvApplyLayout(lvFitFor(channels.length)); // a medida: no queda como división recordada
  } else {
    const layout = lvSmallestFor(channels.length);
    if (lv.layout !== layout) { lvApplyLayout(layout); lv.prefs.layout = layout.key; lvSavePrefs(); }
  }
  lv.cells.forEach((c) => c.clear());
  lvSelectCell(null);
  const profile = lvDefaultProfile();
  const n = Math.min(channels.length, lv.cells.length);
  await lvOpenMany(label, channels.slice(0, n).map((ch, i) => ({ ch, cell: lv.cells[i], profile })));
  if (total > LV_MAX_CELLS)
    lvStatus(`"${label}" tiene ${total} canales: el panel web abre hasta ${LV_MAX_CELLS} (para verlos todos, use el cliente de escritorio).`, true);
}

// ---------------------------------------------------------------------------
// Árbol de cámaras
// ---------------------------------------------------------------------------
async function lvLoadTree() {
  if (lv.fleet) return lvLoadFleetTree();
  let devices;
  try { devices = await Api.get("/api/devices"); }
  catch (err) { $("#lv-tree").innerHTML = `<div class="error-box" style="margin:10px">${esc(err.error)}</div>`; return; }
  const lists = await Promise.all(devices.map((d) => Api.get(`/api/devices/${d.id}/channels`).catch(() => [])));
  let locations = [];
  try { locations = await Api.get("/api/locations"); } catch { /* sin ubicaciones: todo "Sin ubicación" */ }
  if (!lv.active) return;

  lv.channels = new Map();
  lv.devices = devices.map((d, i) => {
    const dev = { id: d.id, name: d.name, status: d.status, channels: [] };
    dev.channels = lists[i].filter((c) => c.enabled).map((c) => {
      const ch = { ...c, device: dev };
      lv.channels.set(ch.id, ch);
      return ch;
    });
    return dev;
  });
  lvBuildLocationTree(locations);

  // Los cuadros abiertos toman los datos nuevos del canal (PTZ marcado, nombre…).
  for (const f of lvAllFeeds()) {
    const fresh = lv.channels.get(f.channel.id);
    if (fresh) { f.channel = fresh; f.render(); }
  }
  lvRenderTree();
  lvUpdatePtz();
}

/**
 * Árbol de Flota: cada vehículo hace de "equipo" (su grabador móvil) con sus cámaras habilitadas. El vivo va por las mismas
 * concesiones que cualquier cámara (equipo + canal RTSP); el servidor exige el permiso de Flota para estas.
 */
async function lvLoadFleetTree() {
  let vehicles;
  try { vehicles = await Api.get("/api/fleet/vehicles"); }
  catch (err) { $("#lv-tree").innerHTML = `<div class="error-box" style="margin:10px">${esc(err.error)}</div>`; return; }
  let locations = [];
  try { locations = await Api.get("/api/locations"); } catch { /* sin ubicaciones: todo "Sin ubicación" */ }
  if (!lv.active) return;

  lv.channels = new Map();
  lv.devices = vehicles.filter((v) => v.enabled).map((v) => {
    const dev = { id: v.deviceId, name: v.code, status: v.status, vehicle: v, channels: [] };
    dev.channels = v.channels.filter((c) => c.enabled).map((c) => {
      const ch = { ...c, deviceId: v.deviceId, supportsPtz: false, locationId: v.locationId, device: dev };
      lv.channels.set(ch.id, ch);
      return ch;
    });
    return dev;
  });
  lvBuildLocationTree(locations);
  for (const f of lvAllFeeds()) {
    const fresh = lv.channels.get(f.channel.id);
    if (fresh) { f.channel = fresh; f.render(); }
  }
  lvRenderTree();
  if (!vehicles.length)
    lvStatus(Perms.can("fleet.manage")
      ? "Todavía no hay vehículos: agréguelos en Configuración → Dispositivos → Vehículos."
      : "Todavía no hay vehículos (o su alcance no incluye ninguno).", true);
}

/** Datos del vehículo junto a su nombre en el árbol: patente y velocidad de su última posición. */
function lvVehicleExtra(d) {
  const v = d.vehicle;
  if (!v) return "";
  const parts = [];
  if (v.plate) parts.push(`<span class="lv-plate">${esc(v.plate)}</span>`);
  if (v.position && d.status === "Online") parts.push(`<span class="lv-speed" title="Última posición GPS">${Math.round(v.position.speedKmh)} km/h</span>`);
  return parts.join("");
}

const lvCollator = new Intl.Collator("es", { numeric: true, sensitivity: "base" });

function lvBuildLocationTree(locations) {
  const nodes = new Map(locations.map((l) => [l.id, { loc: l, children: [], channels: [] }]));
  const roots = [];
  for (const l of [...locations].sort((a, b) => lvCollator.compare(a.name, b.name))) {
    const node = nodes.get(l.id);
    const parent = l.parentId != null ? nodes.get(l.parentId) : null;
    (parent ? parent.children : roots).push(node);
  }
  const unassigned = { loc: null, children: [], channels: [] };
  const all = [...lv.channels.values()].sort((a, b) => lvCollator.compare(a.name, b.name));
  for (const ch of all) (nodes.get(ch.locationId)?.channels ?? unassigned.channels).push(ch);
  if (unassigned.channels.length) roots.push(unassigned);
  // Solo las ubicaciones con alguna cámara en su subárbol (este árbol es para ver video).
  const count = (n) => (n.total = n.channels.length + n.children.reduce((s, c) => s + count(c), 0));
  roots.forEach(count);
  const prune = (list) => list.filter((n) => n.total > 0).map((n) => { n.children = prune(n.children); return n; });
  lv.locRoots = prune(roots);
}

function lvChannelOnline(ch) { return ch.isOnline && ch.device?.status === "Online"; }

function lvIsCollapsed(key) { return lv.prefs.collapsedNodes.includes(key); }

function lvRenderTree() {
  const host = $("#lv-tree");
  if (!host) return;
  const q = lvNorm(lv.search.trim());
  const live = lvLiveMap();
  const chHtml = (ch, depth) => {
    const where = live.get(ch.id);
    const tip = where ? `En pantalla: cuadro ${where.join(", ")}` : `${ch.device?.name ?? ""} · canal ${ch.channelNumber}`;
    return `<div class="lv-node lv-ch ${where ? "live" : ""} ${lv.selChannelId === ch.id ? "sel" : ""}" draggable="true"
      data-ch="${ch.id}" style="--d:${depth}" title="${esc(tip)} — doble clic para abrirla">
      <span class="lv-ico ${lvChannelOnline(ch) ? "ok" : ""}">${where ? "▶" : "◉"}</span><span class="lv-label">${esc(ch.name)}</span></div>`;
  };
  let html = "";
  if (!lv.prefs.byLocation) {
    for (const d of lv.devices) {
      const devMatch = !q || lvNorm(d.name).includes(q) || (d.vehicle?.plate && lvNorm(d.vehicle.plate).includes(q));
      const chans = devMatch ? d.channels : d.channels.filter((c) => lvNorm(c.name).includes(q));
      if (q && !devMatch && !chans.length) continue;
      const key = `d${d.id}`;
      const collapsed = !q && lvIsCollapsed(key);
      const devTip = d.vehicle
        ? `${d.vehicle.kind ? `${d.vehicle.kind} · ` : ""}${d.status === "Online" ? "conectado" : "sin conexión"} — doble clic para abrir sus cámaras`
        : "Doble clic para abrir todos sus canales";
      html += `<div class="lv-node lv-dev" data-dev="${d.id}" data-key="${key}" style="--d:0" title="${esc(devTip)}">
        <span class="lv-caret ${collapsed ? "" : "open"}" data-caret></span>
        <span class="dot ${d.status === "Online" ? "ok" : "bad"}"></span><b class="lv-label">${esc(d.name)}</b>${lvVehicleExtra(d)}</div>`;
      if (!collapsed) html += chans.map((c) => chHtml(c, 1)).join("");
    }
    if (!lv.devices.length) html = `<div class="muted lv-small" style="padding:10px 14px">${lv.fleet ? "No hay vehículos disponibles." : "No hay cámaras disponibles."}</div>`;
  } else {
    const walk = (node, depth, ancestorMatch) => {
      const name = node.loc ? node.loc.name : "Sin ubicación";
      const selfMatch = ancestorMatch || !q || lvNorm(name).includes(q);
      const chans = selfMatch ? node.channels : node.channels.filter((c) => lvNorm(c.name).includes(q) || lvNorm(c.device?.name).includes(q));
      const kids = node.children.map((c) => walk(c, depth + 1, selfMatch)).filter(Boolean).join("");
      if (q && !selfMatch && !chans.length && !kids) return "";
      const key = node.loc ? `l${node.loc.id}` : "l0";
      const collapsed = !q && lvIsCollapsed(key);
      const kind = node.loc ? (typeof LOC_KINDS !== "undefined" ? LOC_KINDS[node.loc.kind] : null) : null;
      const icon = typeof resIcon === "function" ? resIcon(node.loc ? (kind?.icon ?? "pin") : "inbox", 13) : "";
      return `<div class="lv-node lv-loc" data-loc="${node.loc?.id ?? 0}" data-key="${key}" style="--d:${depth}"
          title="${esc(kind?.label ?? "Cámaras sin ubicación")} — doble clic o clic derecho para abrir sus cámaras">
          <span class="lv-caret ${collapsed ? "" : "open"}" data-caret></span>
          <span class="lv-locico">${icon}</span><b class="lv-label">${esc(name)}</b><span class="lv-count">${node.total}</span></div>` +
        (collapsed ? "" : kids + chans.map((c) => chHtml(c, depth + 1)).join(""));
    };
    html = lv.locRoots.map((n) => walk(n, 0, false)).join("") ||
      `<div class="muted lv-small" style="padding:10px 14px">No hay cámaras disponibles.</div>`;
  }
  if (q && !html.includes("lv-ch")) html = `<div class="muted lv-small" style="padding:10px 14px">Ningún canal coincide con la búsqueda.</div>`;
  host.innerHTML = html;
}

/** Canal → números de cuadro donde está en pantalla. */
function lvLiveMap() {
  const map = new Map();
  for (const c of lv.cells) if (c.feed) {
    const list = map.get(c.feed.channel.id) ?? [];
    list.push(c.index);
    map.set(c.feed.channel.id, list);
  }
  return map;
}

function lvRefreshTreeLive() { lvRenderTree(); }

function lvMarkTreeSelection(scroll) {
  $$("#lv-tree .lv-ch.sel").forEach((n) => n.classList.remove("sel"));
  if (lv.selChannelId == null) return;
  const node = $(`#lv-tree .lv-ch[data-ch="${lv.selChannelId}"]`);
  if (!node) return;
  node.classList.add("sel");
  if (scroll) node.scrollIntoView({ block: "nearest" });
}

function lvLocationChannels(id) {
  const out = [];
  const find = (list) => {
    for (const n of list) {
      if ((n.loc?.id ?? 0) === id) { collect(n); return true; }
      if (find(n.children)) return true;
    }
    return false;
  };
  const collect = (n) => { out.push(...n.channels); n.children.forEach(collect); };
  find(lv.locRoots);
  return out;
}

function lvWireTree() {
  const tree = $("#lv-tree");
  tree.addEventListener("click", (e) => {
    const node = e.target.closest(".lv-node");
    if (!node) return;
    if (e.target.closest("[data-caret]") && node.dataset.key) {
      const key = node.dataset.key, list = lv.prefs.collapsedNodes;
      const i = list.indexOf(key);
      if (i >= 0) list.splice(i, 1); else list.push(key);
      lvSavePrefs();
      lvRenderTree();
      return;
    }
    if (node.dataset.ch) { lv.selChannelId = Number(node.dataset.ch); lvMarkTreeSelection(false); }
  });
  tree.addEventListener("dblclick", (e) => {
    const node = e.target.closest(".lv-node");
    if (!node || e.target.closest("[data-caret]")) return;
    if (node.dataset.ch) {
      const ch = lv.channels.get(Number(node.dataset.ch));
      if (ch) lvOpenChannel(ch);
    } else if (node.dataset.dev) {
      const d = lv.devices.find((x) => x.id === Number(node.dataset.dev));
      if (d) lvOpenAll(d.name, d.channels);
    } else if (node.dataset.loc != null) {
      const id = Number(node.dataset.loc);
      lvOpenAll(node.querySelector(".lv-label").textContent, lvLocationChannels(id));
    }
  });
  tree.addEventListener("dragstart", (e) => {
    const node = e.target.closest?.(".lv-ch");
    if (!node) { e.preventDefault(); return; }
    e.dataTransfer.effectAllowed = "copyMove";
    e.dataTransfer.setData("text/x-tcvms-channel", node.dataset.ch);
  });
  tree.addEventListener("contextmenu", (e) => {
    const node = e.target.closest(".lv-loc");
    if (!node) return;
    e.preventDefault();
    lvLocationMenu(node, e.clientX, e.clientY);
  });
}

/** Menú de una ubicación: abrir sus cámaras y órdenes a sus áreas de alarma. */
function lvLocationMenu(node, x, y) {
  $("#lv-ctx")?.remove();
  const id = Number(node.dataset.loc);
  const name = node.querySelector(".lv-label").textContent;
  const canCmd = id > 0 && Perms.can("locations.command") && Operable.can("location", id);
  const menu = document.createElement("div");
  menu.id = "lv-ctx";
  menu.className = "lv-ctx";
  menu.innerHTML = `
    <button type="button" data-cmd="open">Abrir sus cámaras</button>
    ${id > 0 ? `<hr>
    <button type="button" data-cmd="arm-away" ${canCmd ? "" : "disabled"}>Armar total sus áreas de alarma…</button>
    <button type="button" data-cmd="arm-stay" ${canCmd ? "" : "disabled"}>Armar parcial sus áreas de alarma…</button>
    <button type="button" data-cmd="disarm" ${canCmd ? "" : "disabled"}>Desarmar sus áreas de alarma…</button>
    ${canCmd ? "" : `<div class="muted lv-small" style="padding:4px 12px 6px">Sus roles o su alcance no permiten ordenar en esta ubicación.</div>`}` : ""}`;
  document.body.append(menu);
  const r = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(x, innerWidth - r.width - 8)}px`;
  menu.style.top = `${Math.min(y, innerHeight - r.height - 8)}px`;
  menu.addEventListener("click", (e) => {
    const b = e.target.closest("[data-cmd]");
    if (!b || b.disabled) return;
    menu.remove();
    if (b.dataset.cmd === "open") lvOpenAll(name, lvLocationChannels(id));
    else lvLocationCommand(id, name, b.dataset.cmd);
  });
}

async function lvLocationCommand(locationId, name, command) {
  const verb = { "arm-away": "Armar total", "arm-stay": "Armar parcial" }[command] ?? "Desarmar";
  const url = `/api/locations/${locationId}/command`;
  let preview;
  try { preview = await Api.post(url, { command, dryRun: true }); }
  catch (err) { lvStatus(err.error, true); return; }
  if (!preview.items.length) { toast(preview.message); return; }
  const list = preview.items.slice(0, 15).map((i) => `• ${i.area} (${i.panel})`).join("\n") +
    (preview.items.length > 15 ? `\n… y ${preview.items.length - 15} más` : "");
  if (!confirm(`${verb} · ${name}\n\n${preview.message}\n\n${list}\n\n¿Continuar?`)) return;
  lvStatus(`${verb}: enviando la orden a ${preview.items.length} área(s)…`);
  try {
    const result = await Api.post(url, { command, dryRun: false });
    lvStatus(result.message);
    const failed = result.items.filter((i) => !i.success);
    if (failed.length) toast(`${result.message} Fallaron: ${failed.map((f) => `${f.area} (${f.error || "sin detalle"})`).join("; ")}`, true);
    else toast(result.message);
  } catch (err) {
    lvStatus(err.error, true);
    toast(err.error, true);
  }
}

function lvOnDeviceStatus(dto) {
  const d = lv.devices.find((x) => x.id === dto.id);
  if (!d) return;
  // El hub manda los enums como números (la API, como texto): DeviceStatus { Unknown, Online, Offline, AuthFailed }.
  d.status = typeof dto.status === "number" ? ["Unknown", "Online", "Offline", "AuthFailed"][dto.status] ?? "Unknown" : dto.status;
  lvRenderTree();
}

function lvOnConfigChanged(topic) {
  if (["devices", "channels", "locations", "fleet"].includes(topic)) {
    clearTimeout(lv.treeReload);
    lv.treeReload = setTimeout(() => { if (lv.active) lvLoadTree(); }, 800);
  } else if (topic === "speakers" && !lv.aux && !lv.fleet) {
    lvLoadSpeakers();
  }
  // Lo operable (alcance) se relee en app.js: el PTZ se re-evalúa después.
  setTimeout(() => { if (lv.active) lvUpdatePtz(); }, 1500);
}

// ---------------------------------------------------------------------------
// Tableros (los mismos del cliente de escritorio: viven en el servidor). El
// panel web muestra sus cámaras; los recursos, tarjetas y páginas web de un
// tablero se ven en el cliente de escritorio.
// ---------------------------------------------------------------------------
function lvRenderViewsButton() {
  const el = $("#lv-active-view");
  if (el) el.textContent = lv.activeView || "";
}

async function lvRenderViewsPop() {
  const pop = $("#lv-views-pop");
  const canSave = Perms.can("live.views");
  const canShare = Perms.can("dashboards.manage");
  pop.innerHTML = `
    <div class="lv-pop-title">TABLEROS</div>
    <div class="lv-views-list" id="lv-views-list"><div class="muted lv-small">Cargando…</div></div>
    ${canSave ? `
    <hr>
    <div class="lv-views-save">
      <input type="text" id="lv-view-name" maxlength="80" placeholder="Nombre del tablero…">
      <button type="button" class="btn small" id="lv-view-save" title="Guarda la división y las cámaras que hay ahora en la grilla">Guardar</button>
    </div>
    ${canShare ? `<label class="lv-set-check"><input type="checkbox" id="lv-view-shared"><span>Compartir con todos (los roles se eligen en Configuración → Tableros)</span></label>` : ""}` : ""}
    <div class="muted lv-small" id="lv-views-msg"></div>`;
  $("#lv-view-save")?.addEventListener("click", lvSaveView);
  $("#lv-view-name")?.addEventListener("keydown", (e) => { if (e.key === "Enter") lvSaveView(); });
  try { lv.views = await Api.get("/api/live-views"); }
  catch (err) { $("#lv-views-list").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  lvRenderViewsList();
}

/** Cuadros de cámara del tablero (los demás solo los muestra el cliente de escritorio). */
const lvViewCameras = (v) => v.items.filter((i) => !i.kind || i.kind === "camera");

function lvViewDetail(v) {
  const found = lvFindLayout(v.layoutName);
  const layout = found ? lvLayoutLabel(found) : v.layoutName;
  const cameras = lvViewCameras(v).length, others = v.items.length - cameras;
  const owner = !v.shared ? "" : v.roles?.length ? ` · para ${v.roles.join(", ")}`
    : ` · compartido${v.owner && v.owner !== Api.username ? ` por ${v.owner}` : ""}`;
  return `${cameras} cámara(s)${others ? ` + ${others} cuadro(s) del cliente` : ""} · división ${layout}${owner}`;
}

/** Tableros de este árbol: los de Flota tienen cámaras de vehículos y los de la vista en vivo, de equipos fijos. */
function lvViewsHere() { return lv.views.filter((v) => lvViewCameras(v).some((i) => lv.channels.has(i.channelId))); }

function lvRenderViewsList() {
  const list = $("#lv-views-list");
  if (!list) return;
  const views = lvViewsHere();
  if (!views.length) {
    list.innerHTML = `<div class="muted lv-small">Todavía no hay tableros con cámaras. Arme la grilla y guárdela con un nombre.</div>`;
    return;
  }
  list.innerHTML = views.map((v) => `
    <div class="lv-view-row" data-view="${v.id}" title="Cargar este tablero en la grilla">
      <div class="lv-view-text"><b>${esc(v.name)}</b><span class="muted">${esc(lvViewDetail(v))}</span></div>
      ${v.canEdit ? `<span class="lv-view-actions">
        <button type="button" class="lv-icon-btn" data-vact="update" title="Actualizar este tablero con la grilla actual">${lvSvg(LV_ICO.edit, 13)}</button>
        <button type="button" class="lv-icon-btn danger" data-vact="delete" title="Eliminar este tablero">${lvSvg(LV_ICO.trash, 13)}</button>
      </span>` : ""}
    </div>`).join("");
  $$("#lv-views-list .lv-view-row").forEach((row) => row.addEventListener("click", (e) => {
    const v = lv.views.find((x) => x.id === Number(row.dataset.view));
    if (!v) return;
    const act = e.target.closest("[data-vact]")?.dataset.vact;
    if (act === "update") lvUpdateView(v);
    else if (act === "delete") lvDeleteView(v);
    else lvApplyView(v);
  }));
}

function lvViewRequest(name, shared) {
  return {
    name, layoutName: lv.layout.key, columns: lv.layout.columns, rows: lv.layout.rows, shared,
    items: lv.cells.map((c, i) => (c.feed ? { cellIndex: i, channelId: c.feed.channel.id, streamType: c.feed.profile === "Main" ? 0 : 1, kind: "camera" } : null)).filter(Boolean),
    roleIds: lv.views.find((v) => v.name === name && v.shared === shared)?.roleIds ?? null,
  };
}

function lvViewsMsg(text, isError) {
  const el = $("#lv-views-msg");
  if (el) { el.textContent = text; el.classList.toggle("error", !!isError); }
}

async function lvSaveView() {
  const name = $("#lv-view-name").value.trim();
  if (!name) { lvViewsMsg("Escriba un nombre para el tablero.", true); return; }
  const req = lvViewRequest(name, !!$("#lv-view-shared")?.checked);
  if (!req.items.length) { lvViewsMsg("La grilla está vacía: abra al menos una cámara antes de guardar.", true); return; }
  try {
    const saved = await Api.post("/api/live-views", req);
    lv.views = [saved, ...lv.views.filter((v) => v.id !== saved.id)].sort((a, b) => lvCollator.compare(a.name, b.name));
    lv.activeView = saved.name;
    lvRenderViewsButton();
    $("#lv-view-name").value = "";
    lvRenderViewsList();
    lvViewsMsg(`Tablero "${saved.name}" guardado.`);
  } catch (err) { lvViewsMsg(err.error, true); }
}

async function lvUpdateView(v) {
  // Los recursos, tarjetas y páginas web del tablero no se ven aquí: reemplazarlo los borraría.
  if (v.items.length > lvViewCameras(v).length) {
    lvViewsMsg(`"${v.name}" tiene cuadros que el panel web no muestra (recursos, tarjetas o páginas web): actualícelo desde el cliente de escritorio${v.shared ? " o en Configuración → Tableros" : ""}.`, true);
    return;
  }
  const req = lvViewRequest(v.name, v.shared);
  if (!req.items.length) { lvViewsMsg("La grilla está vacía: no hay nada que guardar en el tablero.", true); return; }
  if (!confirm(`¿Reemplazar el tablero "${v.name}" con la grilla actual (${req.items.length} cámara(s), división ${lv.layout.key})?`)) return;
  try {
    const saved = await Api.put(`/api/live-views/${v.id}`, req);
    lv.views = lv.views.map((x) => (x.id === saved.id ? saved : x));
    lvRenderViewsList();
    lvViewsMsg(`Tablero "${saved.name}" actualizado.`);
  } catch (err) { lvViewsMsg(err.error, true); }
}

async function lvDeleteView(v) {
  if (!confirm(`¿Eliminar el tablero "${v.name}"?${v.shared ? " Está compartido: desaparece para todos los puestos." : ""}`)) return;
  try {
    await Api.delete(`/api/live-views/${v.id}`);
    lv.views = lv.views.filter((x) => x.id !== v.id);
    if (lv.activeView === v.name) { lv.activeView = ""; lvRenderViewsButton(); }
    lvRenderViewsList();
    lvViewsMsg(`Tablero "${v.name}" eliminado.`);
  } catch (err) { lvViewsMsg(err.error, true); }
}

async function lvApplyView(v) {
  lvClosePops();
  let view = v;
  try { view = await Api.post(`/api/live-views/${v.id}/apply`); } // la versión vigente + bitácora
  catch (err) { lvStatus(err.error, true); return; }
  const cameras = lvViewCameras(view), others = view.items.length - cameras.length;
  const fit = lvFitSavedItems(lvRestoreLayout(view.layoutName, view.columns, view.rows), cameras);
  lvApplyLayout(fit.layout);
  if (lvFindLayout(fit.layout.key)) { lv.prefs.layout = fit.layout.key; lvSavePrefs(); }
  lv.cells.forEach((c) => c.clear());
  lvSelectCell(null);
  lv.activeView = view.name;
  lvRenderViewsButton();
  const items = [];
  let missing = 0;
  for (const item of fit.items) {
    const ch = lv.channels.get(item.channelId);
    const cell = lv.cells[item.cellIndex];
    if (!ch || !cell) { missing++; continue; }
    items.push({ ch, cell, profile: item.streamType === 0 ? "Main" : "Sub" });
  }
  await lvOpenMany(view.name, items);
  const notes = [];
  if (fit.compacted)
    notes.push(`es de ${cameras.length} cámara(s) en división ${view.layoutName}; el panel web muestra hasta ${LV_MAX_CELLS} cuadros ` +
      `(el tablero completo, en el cliente de escritorio)`);
  if (missing) notes.push(`${missing} cámara(s) ya no están disponibles y se omitieron`);
  if (others) notes.push(`${others} cuadro(s) con recursos, tarjetas o páginas web se ven solo en el cliente de escritorio`);
  if (notes.length) lvStatus(`Tablero "${view.name}": ${notes.join("; ")}.`, true);
}

// ---------------------------------------------------------------------------
// "Volver a abrir las cámaras de la última sesión" (solo la ventana principal)
// ---------------------------------------------------------------------------
function lvScheduleLastSave() {
  clearTimeout(lv.lastSaveTimer);
  lv.lastSaveTimer = setTimeout(lvSaveLastNow, 800);
}

function lvSaveLastNow() {
  if (lv.aux || !lv.layout) return;
  try {
    localStorage.setItem(lvLastKey(), JSON.stringify({
      layout: lv.layout.key, columns: lv.layout.columns, rows: lv.layout.rows,
      items: lv.cells.map((c, i) => (c.feed ? { i, ch: c.feed.channel.id, p: c.feed.profile } : null)).filter(Boolean),
    }));
  } catch { /* modo privado */ }
}

async function lvReopenLast() {
  let last = null;
  try { last = JSON.parse(localStorage.getItem(lvLastKey()) || "null"); } catch { /* ilegible */ }
  if (!last?.items?.length) return;
  const fit = lvFitSavedItems(lvRestoreLayout(last.layout, last.columns, last.rows),
    last.items.map((x) => ({ cellIndex: x.i, channelId: x.ch, profile: x.p })));
  lvApplyLayout(fit.layout);
  const items = fit.items
    .map((x) => ({ ch: lv.channels.get(x.channelId), cell: lv.cells[x.cellIndex], profile: x.profile === "Main" ? "Main" : "Sub" }))
    .filter((x) => x.ch && x.cell);
  if (items.length) await lvOpenMany("la última sesión", items);
}

// ---------------------------------------------------------------------------
// Pantallas auxiliares: otra ventana del navegador con su propia grilla.
// ---------------------------------------------------------------------------
async function lvOpenAux() {
  lv.auxWindows = lv.auxWindows.filter((w) => w.win && !w.win.closed);
  if (lv.auxWindows.length >= 3) {
    lvStatus("Ya hay 3 pantallas auxiliares abiertas: cierre una para abrir otra.", true);
    lv.auxWindows.at(-1).win.focus();
    return;
  }
  const n = [1, 2, 3].find((k) => !lv.auxWindows.some((w) => w.n === k));
  const w = Math.round(screen.availWidth * 0.85), h = Math.round(screen.availHeight * 0.85);
  const win = window.open(`${location.pathname}#/live?aux=${n}`, `tcvms-aux-${n}`, `popup=yes,width=${w},height=${h}`);
  if (!win) { lvStatus("El navegador bloqueó la ventana emergente: permita las ventanas emergentes de este sitio.", true); return; }
  lv.auxWindows.push({ n, win });
  lvStatus(`Pantalla auxiliar ${n} abierta. Arrástrela al monitor que quiera y use "Pantalla completa".`);
  // Con el permiso de administración de ventanas, se lleva sola a un monitor libre.
  try {
    if (!("getScreenDetails" in window)) return;
    const perm = await navigator.permissions.query({ name: "window-management" }).catch(() => null);
    if (perm?.state === "denied") return;
    const details = await window.getScreenDetails();
    const busy = new Set([details.currentScreen]);
    const target = details.screens.find((s) => !busy.has(s)) ?? null;
    if (target && !win.closed) {
      win.moveTo(target.availLeft, target.availTop);
      win.resizeTo(target.availWidth, target.availHeight);
    }
  } catch { /* sin permiso o un solo monitor: queda donde la abrió el navegador */ }
}

// ---------------------------------------------------------------------------
// PTZ (panel y teclado)
// ---------------------------------------------------------------------------
function lvPtzTarget() {
  const f = lv.selected?.feed;
  if (!f) return null;
  const ch = lv.channels.get(f.channel.id) ?? f.channel;
  return ch.supportsPtz ? ch : null;
}

function lvPtzAvailable() {
  const ch = lvPtzTarget();
  return !!ch && !lv.aux && Perms.can("live.ptz") && Operable.can("channel", ch.id);
}

function lvUpdatePtz() {
  const panel = $("#lv-ptz");
  if (!panel) return;
  const ch = lvPtzTarget();
  const operable = !!ch && Operable.can("channel", ch.id);
  const ok = lvPtzAvailable();
  const name = $("#lv-ptz-name");
  name.textContent = !ch ? "sin cámara PTZ" : operable ? ch.name : `${ch.name} (fuera de su alcance)`;
  name.classList.toggle("off", !ok);
  panel.classList.toggle("disabled", !ok);
  $$("#lv-ptz-body button, #lv-ptz-body input").forEach((el) => {
    if (el.id === "lv-speed") return;
    if (el.textContent === "·") return;
    el.disabled = !ok;
  });
  if (!ok && lv.ptzHeld) lvPtzRelease();
  lvJoyUpdatePill();
}

function lvPtz(command, stop, speed) {
  const ch = lvPtzTarget();
  if (!ch || !lvPtzAvailable()) return;
  lvPtzSend(ch, command, stop, speed ?? (lv.shift ? 1 : lv.prefs.ptzSpeed));
}

/** Las órdenes van en fila: un "detener" nunca debe adelantarse a su "mover". Resuelve al terminar la orden. */
function lvPtzSend(ch, command, stop, speed) {
  return lv.ptzChain = lv.ptzChain.then(() =>
    Api.post(`/api/devices/${ch.deviceId}/channels/${ch.channelNumber}/ptz`, { command, speed, stop })
      .catch((err) => lvStatus(`PTZ: ${err.error}`, true)));
}

function lvPtzRelease() {
  const held = lv.ptzHeld;
  lv.ptzHeld = null;
  if (held) lvPtz(held, true);
}

function lvWirePtz() {
  const panel = $("#lv-ptz");
  const body = $("#lv-ptz-body");
  $("#lv-ptz-toggle").addEventListener("click", () => {
    lv.prefs.ptzOpen = !lv.prefs.ptzOpen;
    lvSavePrefs();
    panel.classList.toggle("open", lv.prefs.ptzOpen);
  });
  // Presionar mantiene el movimiento; soltar (o salir del botón) lo detiene.
  body.addEventListener("pointerdown", (e) => {
    const b = e.target.closest("[data-ptz]");
    if (!b || b.disabled || e.button !== 0) return;
    b.setPointerCapture(e.pointerId);
    if (lv.ptzHeld) lvPtzRelease();
    lv.ptzHeld = b.dataset.ptz;
    b.classList.add("held");
    lvPtz(b.dataset.ptz, false);
  });
  const release = (e) => {
    const b = e.target.closest?.("[data-ptz]");
    b?.classList.remove("held");
    if (lv.ptzHeld) lvPtzRelease();
  };
  body.addEventListener("pointerup", release);
  body.addEventListener("pointercancel", release);
  body.addEventListener("lostpointercapture", release);
  body.addEventListener("click", async (e) => {
    const b = e.target.closest("[data-preset]");
    if (!b || b.disabled) return;
    const ch = lvPtzTarget();
    if (!ch) return;
    const input = $("#lv-preset");
    const index = Math.min(Math.max(Number(input.value) || 1, 1), 300);
    input.value = index;
    const action = b.dataset.preset;
    try {
      await Api.post(`/api/devices/${ch.deviceId}/channels/${ch.channelNumber}/ptz-preset`, { action, index });
      lvStatus(action === "Goto" ? `PTZ: moviéndose al preset ${index}.`
        : action === "Set" ? `PTZ: posición actual guardada como preset ${index}.`
          : `PTZ: preset ${index} eliminado.`);
    } catch (err) { lvStatus(`PTZ: ${err.error}`, true); }
  });
  $("#lv-speed").addEventListener("input", (e) => {
    lv.prefs.ptzSpeed = Number(e.target.value);
    $("#lv-speed-val").textContent = lv.prefs.ptzSpeed;
    lvSavePrefs();
  });
}

const LV_KEY_PTZ = {
  ArrowUp: "TiltUp", ArrowDown: "TiltDown", ArrowLeft: "PanLeft", ArrowRight: "PanRight",
  "+": "ZoomIn", "=": "ZoomIn", Add: "ZoomIn", "-": "ZoomOut", Subtract: "ZoomOut",
};

function lvKeyTargetIsInput(e) {
  const t = e.target;
  return t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
}

function lvSetPrecision(on) {
  lv.shift = on;
  const pill = $("#lv-precision");
  if (pill) pill.hidden = !on || !lvPtzAvailable();
}

/** Teclado: flechas = pan/tilt, +/− = zoom, Shift sostenido = modo precisión (velocidad 1). */
function lvOnKeyDown(e) {
  if (!lv.active) return;
  if (e.key === "Escape") { lvClosePops(); $("#lv-ctx")?.remove(); if (lv.mergeMode) lvSetMergeMode(false); return; }
  if (lv.aux) return;
  if (e.key === "Shift") { lvSetPrecision(true); return; }
  if (lvKeyTargetIsInput(e) || !$("#modal-backdrop").classList.contains("hidden")) return;
  const command = LV_KEY_PTZ[e.key] ?? (e.code === "NumpadAdd" ? "ZoomIn" : e.code === "NumpadSubtract" ? "ZoomOut" : null);
  if (!command || !lvPtzAvailable()) return;
  e.preventDefault();
  if (e.repeat || lv.keyHeld === command) return;
  if (lv.keyHeld) lvPtz(lv.keyHeld, true);
  lv.keyHeld = command;
  lvPtz(command, false);
}

function lvOnKeyUp(e) {
  if (!lv.active || lv.aux) return;
  if (e.key === "Shift") { lvSetPrecision(false); return; }
  const command = LV_KEY_PTZ[e.key] ?? (e.code === "NumpadAdd" ? "ZoomIn" : e.code === "NumpadSubtract" ? "ZoomOut" : null);
  if (command && lv.keyHeld === command) {
    lv.keyHeld = null;
    lvPtz(command, true);
  }
}

// ---------------------------------------------------------------------------
// Parlantes IP (hablar, sonidos del servidor, biblioteca del equipo, texto a voz)
// ---------------------------------------------------------------------------
async function lvLoadSpeakers() {
  const panel = $("#lv-spk");
  if (!panel) return;
  if (!Perms.can("speakers.play")) { panel.hidden = true; return; }
  let speakers;
  try { speakers = await Api.get("/api/speakers"); } catch { panel.hidden = true; return; }
  lv.speakers = speakers.filter((s) => s.enabled);
  if (!lv.speakers.length) { panel.hidden = true; return; }
  try { lv.sounds = await Api.get("/api/speakers/sounds"); } catch { lv.sounds = []; }
  const ids = new Set(lv.speakers.map((s) => s.id));
  lv.prefs.speakers = (lv.prefs.speakers || []).filter((id) => ids.has(id));
  panel.hidden = false;
  lvRenderSpeakers();
  lvLoadLibrary();
}

function lvSelectedSpeakers() { return lv.speakers.filter((s) => lv.prefs.speakers.includes(s.id)); }

/** Se puede marcar si está en su alcance (uno sin conexión se marca igual: el servidor dirá qué pasó). */
function lvSpeakerOk(s) { return Operable.can("speaker", s.id); }

function lvRenderSpeakers() {
  const panel = $("#lv-spk");
  if (!panel) return;
  const chosen = lvSelectedSpeakers();
  const summary = chosen.length === 0 ? "ninguno" : chosen.length === 1 ? chosen[0].name : `${chosen.length} parlantes`;
  const usable = lv.speakers.filter(lvSpeakerOk);
  const all = usable.length > 0 && usable.every((s) => lv.prefs.speakers.includes(s.id));
  const volume = chosen.find((s) => s.volume != null)?.volume ?? 50;
  const secure = window.isSecureContext && !!navigator.mediaDevices?.getUserMedia;
  const hasTts = chosen.some((s) => s.supportsTts);
  panel.className = `lv-panel ${lv.prefs.spkOpen ? "open" : ""}`;
  panel.innerHTML = `
    <div class="lv-panel-head">
      <span class="lv-cap">PARLANTES — <b class="${chosen.length ? "" : "off"}">${esc(summary)}</b></span>
      <button type="button" class="lv-icon-btn lv-only-open" id="lv-spk-all" title="${all ? "Desmarcar" : "Marcar"} todos los parlantes">${lvSvg(all ? LV_ICO.checkAll : LV_ICO.checkNone, 14)}</button>
      <button type="button" class="lv-chev" id="lv-spk-toggle" title="Minimizar / restaurar el panel de parlantes"></button>
    </div>
    <div class="lv-panel-body">
      <div class="lv-spk-list">${lv.speakers.map((s) => `
        <label class="lv-spk ${lvSpeakerOk(s) ? "" : "off"}" title="${esc([s.location, s.model, s.volume != null ? `Volumen ${s.volume}` : null, s.lastError].filter(Boolean).join(" · "))}">
          <input type="checkbox" data-spk="${s.id}" ${lv.prefs.speakers.includes(s.id) ? "checked" : ""} ${lvSpeakerOk(s) ? "" : "disabled"}>
          <span class="dot ${s.status === "Online" ? "ok" : "bad"}"></span><span>${esc(s.name)}</span>
          ${s.busyWith ? `<em>${esc(s.busyWith)}</em>` : ""}
        </label>`).join("")}</div>
      <div class="lv-spk-row" title="Volumen de salida del parlante (0-100); con varios marcados se aplica a todos">
        <span>Volumen</span><input type="range" id="lv-spk-vol" min="0" max="100" step="5" value="${volume}" ${chosen.length ? "" : "disabled"}><b id="lv-spk-vol-val">${volume}</b>
      </div>
      ${secure ? `
      <div class="lv-spk-row">
        <select id="lv-spk-mic" title="Micrófono con el que se habla por los parlantes (se recuerda)"><option value="">Micrófono predeterminado</option></select>
        <button type="button" class="btn ghost small" id="lv-spk-test" title="Captura el micrófono elegido sin transmitir: el nivel de abajo debe moverse al hablar">Probar</button>
      </div>
      <div class="lv-level"><i id="lv-spk-level"></i></div>` : ""}
      <div class="lv-spk-row">
        <button type="button" class="btn lv-talk" id="lv-spk-talk" ${chosen.length && secure ? "" : "disabled"}
          title="${secure ? "Mantenga presionado para hablar por los parlantes marcados (micrófono de este equipo)" : "Para hablar desde el navegador, el panel debe abrirse por HTTPS o desde el propio servidor (http://localhost)."}">Mantener para hablar</button>
        <button type="button" class="btn ghost small" id="lv-spk-stop" ${chosen.length ? "" : "disabled"} title="Detener lo que esté sonando en los parlantes marcados">Detener</button>
      </div>
      ${secure ? `<label class="lv-set-check"><input type="checkbox" id="lv-spk-tone" ${lv.prefs.tone ? "checked" : ""}><span>Tono al abrir el canal (dos pitidos antes de la voz)</span></label>` : ""}
      ${lv.sounds.length ? `
      <div class="lv-spk-row">
        <select id="lv-spk-sound" title="Sonidos subidos al servidor (Automatizaciones → Sonidos)">${lv.sounds.map((s) => `<option value="${esc(s.displayName)}">${esc(s.displayName)}</option>`).join("")}</select>
        <button type="button" class="lv-icon-btn" id="lv-spk-sound-preview" title="Escuchar en ESTE equipo (no suena en los parlantes); otro clic detiene">${lvSvg(LV_ICO.ear, 14)}</button>
        <button type="button" class="lv-icon-btn" id="lv-spk-sound-play" ${chosen.length ? "" : "disabled"} title="Reproducir el sonido del servidor en los PARLANTES marcados (sincronizado)">${lvSvg(LV_ICO.play, 13)}</button>
      </div>` : ""}
      <div class="lv-spk-row" id="lv-spk-lib-row" hidden>
        <select id="lv-spk-lib"></select>
        <button type="button" class="lv-icon-btn" id="lv-spk-lib-dl" title="Descargar el archivo desde el parlante a este equipo">${lvSvg(LV_ICO.download, 14)}</button>
        <button type="button" class="lv-icon-btn" id="lv-spk-lib-preview" title="Escuchar en ESTE equipo (se descarga del parlante, no suena afuera); otro clic detiene">${lvSvg(LV_ICO.ear, 14)}</button>
        <button type="button" class="lv-icon-btn" id="lv-spk-lib-play" title="Reproducir el audio guardado en el PARLANTE (se busca por nombre en cada uno)">${lvSvg(LV_ICO.play, 13)}</button>
      </div>
      ${hasTts ? `
      <div class="lv-spk-row">
        <input type="text" id="lv-spk-tts" maxlength="100" placeholder="Texto a leer en voz alta…" title="Texto a leer (máximo 100 caracteres)">
        <button type="button" class="lv-icon-btn" id="lv-spk-tts-play" title="El parlante lee el texto en voz alta">${lvSvg(LV_ICO.play, 13)}</button>
      </div>` : ""}
      <div class="muted lv-small" id="lv-spk-msg"></div>
    </div>`;
  lvWireSpeakers();
  lvRenderLibrary();
}

function lvSpkMsg(text, isError) {
  const el = $("#lv-spk-msg");
  if (el) { el.textContent = text; el.classList.toggle("error", !!isError); }
}

function lvWireSpeakers() {
  $("#lv-spk-toggle").addEventListener("click", () => {
    lv.prefs.spkOpen = !lv.prefs.spkOpen;
    lvSavePrefs();
    $("#lv-spk").classList.toggle("open", lv.prefs.spkOpen);
  });
  $("#lv-spk-all").addEventListener("click", () => {
    const usable = lv.speakers.filter(lvSpeakerOk).map((s) => s.id);
    lv.prefs.speakers = usable.every((id) => lv.prefs.speakers.includes(id)) ? [] : usable;
    lvSavePrefs();
    lvRenderSpeakers();
    lvLoadLibrary();
  });
  $$("#lv-spk [data-spk]").forEach((cb) => cb.addEventListener("change", () => {
    const id = Number(cb.dataset.spk);
    lv.prefs.speakers = cb.checked ? [...new Set([...lv.prefs.speakers, id])] : lv.prefs.speakers.filter((x) => x !== id);
    lvSavePrefs();
    lvRenderSpeakers();
    lvLoadLibrary();
  }));
  // Volumen: se aplica al soltar, a todos los marcados.
  const vol = $("#lv-spk-vol");
  vol.addEventListener("input", () => { $("#lv-spk-vol-val").textContent = vol.value; });
  vol.addEventListener("change", async () => {
    const targets = lvSelectedSpeakers();
    const volume = Number(vol.value);
    let ok = 0;
    for (const s of targets) {
      try {
        const updated = await Api.put(`/api/speakers/${s.id}/volume`, { volume });
        Object.assign(s, updated);
        ok++;
      } catch (err) { lvSpkMsg(`${s.name}: ${err.error}`, true); }
    }
    if (ok) lvSpkMsg(`Volumen ${volume} aplicado a ${ok} parlante${ok === 1 ? "" : "s"}.`);
  });
  $("#lv-spk-stop").addEventListener("click", async () => {
    try {
      const r = await Api.post("/api/speakers/stop", { speakerIds: lvSelectedSpeakers().map((s) => s.id) });
      lvSpkMsg(r.message);
    } catch (err) { lvSpkMsg(err.error, true); }
  });
  $("#lv-spk-tone")?.addEventListener("change", (e) => { lv.prefs.tone = e.target.checked; lvSavePrefs(); });

  // Micrófono: lista (los nombres aparecen tras el primer permiso) y prueba.
  const mic = $("#lv-spk-mic");
  if (mic) {
    navigator.mediaDevices.enumerateDevices().then((devices) => {
      const inputs = devices.filter((d) => d.kind === "audioinput" && d.deviceId && d.deviceId !== "default");
      mic.innerHTML = `<option value="">Micrófono predeterminado</option>` +
        inputs.map((d, i) => `<option value="${esc(d.deviceId)}" ${d.deviceId === lv.prefs.mic ? "selected" : ""}>${esc(d.label || `Micrófono ${i + 1}`)}</option>`).join("");
    }).catch(() => { /* sin acceso a dispositivos */ });
    mic.addEventListener("change", () => { lv.prefs.mic = mic.value; lvSavePrefs(); });
    $("#lv-spk-test").addEventListener("click", () => {
      if (lv.talk?.testing) { lvStopTalk(); return; }
      lvStartTalk(true);
    });
  }

  // Hablar: mantener presionado (como el PTZ).
  const talk = $("#lv-spk-talk");
  talk.addEventListener("pointerdown", (e) => {
    if (talk.disabled || e.button !== 0) return;
    talk.setPointerCapture(e.pointerId);
    lvStartTalk(false);
  });
  const stopTalk = () => { if (lv.talk && !lv.talk.testing) lvStopTalk(); };
  talk.addEventListener("pointerup", stopTalk);
  talk.addEventListener("pointercancel", stopTalk);
  talk.addEventListener("lostpointercapture", stopTalk);

  $("#lv-spk-sound-play")?.addEventListener("click", () =>
    lvSpeakerPlay({ source: "server", sound: $("#lv-spk-sound").value }));
  $("#lv-spk-sound-preview")?.addEventListener("click", () =>
    lvPreview(`/api/workflows/audio/${encodeURIComponent($("#lv-spk-sound").value)}/file`, $("#lv-spk-sound").value));
  $("#lv-spk-tts-play")?.addEventListener("click", () => {
    const text = $("#lv-spk-tts").value.trim();
    if (!text) { lvSpkMsg("Escriba el texto a leer.", true); return; }
    lvSpeakerPlay({ source: "tts", text, language: "spanish", voice: "female" });
  });
  $("#lv-spk-lib-play").addEventListener("click", () => {
    const item = lv.library.find((x) => String(x.id) === $("#lv-spk-lib").value);
    if (item) lvSpeakerPlay({ source: "library", libraryName: item.name });
  });
  $("#lv-spk-lib-preview").addEventListener("click", () => {
    const item = lv.library.find((x) => String(x.id) === $("#lv-spk-lib").value);
    if (item) lvPreview(`/api/speakers/${lv.library.owner}/library/${item.id}/file`, item.name);
  });
  $("#lv-spk-lib-dl").addEventListener("click", async () => {
    const item = lv.library.find((x) => String(x.id) === $("#lv-spk-lib").value);
    if (!item) return;
    try {
      const blob = await lvFetchBlob(`/api/speakers/${lv.library.owner}/library/${item.id}/file`);
      lvDownload(blob, `${lvSafeName(item.name)}.${(item.format || "wav").toLowerCase()}`);
    } catch (err) { lvSpkMsg(err.message, true); }
  });
}

async function lvSpeakerPlay(request) {
  const ids = lvSelectedSpeakers().map((s) => s.id);
  if (!ids.length) { lvSpkMsg("Marque al menos un parlante.", true); return; }
  try {
    const r = await Api.post("/api/speakers/play", { speakerIds: ids, ...request });
    lvSpkMsg(r.message, !r.success);
  } catch (err) { lvSpkMsg(err.error, true); }
}

/** Biblioteca: la del primer parlante marcado que tenga (en los demás se busca por nombre). */
async function lvLoadLibrary() {
  const owner = lvSelectedSpeakers().find((s) => s.supportsLibrary);
  lv.library = [];
  if (owner) {
    try { lv.library = await Api.get(`/api/speakers/${owner.id}/library`); } catch { lv.library = []; }
    lv.library.owner = owner.id;
    lv.library.ownerName = owner.name;
  }
  lvRenderLibrary();
}

function lvRenderLibrary() {
  const row = $("#lv-spk-lib-row");
  if (!row) return;
  row.hidden = !lv.library.length;
  if (!lv.library.length) return;
  const sel = $("#lv-spk-lib");
  sel.title = `Biblioteca de ${lv.library.ownerName}`;
  sel.innerHTML = lv.library.map((x) => `<option value="${x.id}">${esc(x.name)}</option>`).join("");
}

async function lvFetchBlob(url) {
  const res = await fetch(url, { headers: Api.token ? { Authorization: "Bearer " + Api.token } : {} });
  if (!res.ok) throw new Error(res.status === 404 ? "El archivo no está disponible." : `Error ${res.status} al descargar.`);
  return res.blob();
}

/** Escucha local (no suena en los parlantes); otro clic detiene. */
async function lvPreview(url, label) {
  if (lv.preview) { lvStopPreview(); return; }
  try {
    const blob = await lvFetchBlob(url);
    const src = URL.createObjectURL(blob);
    const audio = new Audio(src);
    lv.preview = { audio, src };
    audio.onended = lvStopPreview;
    await audio.play();
    lvSpkMsg(`Escuchando "${label}" en este equipo (no suena en los parlantes).`);
  } catch (err) {
    lvStopPreview();
    lvSpkMsg(err.message || "No se pudo reproducir el audio.", true);
  }
}

function lvStopPreview() {
  const p = lv.preview;
  lv.preview = null;
  if (!p) return;
  try { p.audio.pause(); } catch { /* noop */ }
  URL.revokeObjectURL(p.src);
}

/** Dos pitidos de 1 kHz (PCM 16 bits a 8 kHz) que avisan que el canal ya está abierto. */
function lvToneFrames() {
  const rate = 8000, beep = 0.12, gap = 0.08;
  const total = Math.round(rate * (beep * 2 + gap * 2));
  const pcm = new Int16Array(total);
  for (let i = 0; i < total; i++) {
    const t = i / rate;
    const inBeep = t < beep || (t >= beep + gap && t < beep * 2 + gap);
    pcm[i] = inBeep ? Math.round(Math.sin(2 * Math.PI * 1000 * t) * 9000) : 0;
  }
  return pcm.buffer;
}

/**
 * Voz en vivo: micrófono → PCM 16 bits mono a 8 kHz → WebSocket
 * /api/speakers/talk (el servidor convierte a G.711 y escribe en todos los
 * marcados). testing = solo medir el nivel, sin transmitir.
 */
async function lvStartTalk(testing) {
  if (lv.talk) lvStopTalk();
  const ids = lvSelectedSpeakers().map((s) => s.id);
  if (!testing && !ids.length) { lvSpkMsg("Marque al menos un parlante.", true); return; }
  const state = { testing, ws: null, ready: false, stream: null, ctx: null, node: null, rest: new Float32Array(0) };
  lv.talk = state;
  const btn = testing ? $("#lv-spk-test") : $("#lv-spk-talk");
  btn?.classList.add("on");
  if (!testing) btn.textContent = "● Hablando… (suelte para terminar)";
  try {
    state.stream = await navigator.mediaDevices.getUserMedia({
      audio: { deviceId: lv.prefs.mic ? { exact: lv.prefs.mic } : undefined, echoCancellation: true, noiseSuppression: true, channelCount: 1 },
    });
  } catch (err) {
    lvStopTalk();
    lvSpkMsg(`No se pudo usar el micrófono: ${err.message}`, true);
    return;
  }
  if (lv.talk !== state) { state.stream.getTracks().forEach((t) => t.stop()); return; }
  // El contexto va a la tasa del equipo (no todos los navegadores conectan un
  // micrófono a un contexto de 8 kHz): se baja a 8 kHz aquí, promediando cada
  // ventana (filtro simple contra el aliasing, suficiente para voz).
  state.ctx = new AudioContext();
  const ratio = state.ctx.sampleRate / 8000;
  const source = state.ctx.createMediaStreamSource(state.stream);
  state.node = state.ctx.createScriptProcessor(2048, 1, 1);
  state.node.onaudioprocess = (e) => {
    const input = e.inputBuffer.getChannelData(0);
    const buf = new Float32Array(state.rest.length + input.length);
    buf.set(state.rest);
    buf.set(input, state.rest.length);
    const n = Math.floor(buf.length / ratio);
    const pcm = new Int16Array(n);
    let sum = 0;
    for (let k = 0; k < n; k++) {
      const a = Math.floor(k * ratio), b = Math.max(a + 1, Math.floor((k + 1) * ratio));
      let acc = 0;
      for (let i = a; i < b; i++) acc += buf[i];
      const v = Math.max(-1, Math.min(1, acc / (b - a)));
      sum += v * v;
      pcm[k] = v < 0 ? v * 0x8000 : v * 0x7fff;
    }
    state.rest = buf.slice(Math.floor(n * ratio));
    const level = n ? Math.min(100, Math.sqrt(sum / n) * 300) : 0;
    const bar = $("#lv-spk-level");
    if (bar) { bar.style.width = `${level}%`; bar.classList.toggle("hot", level > 85); }
    if (state.ready && state.ws?.readyState === WebSocket.OPEN) state.ws.send(pcm.buffer);
  };
  source.connect(state.node);
  state.node.connect(state.ctx.destination);
  if (testing) { lvSpkMsg("Prueba de micrófono: hable y mire el nivel (no se transmite)."); return; }

  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  const ws = new WebSocket(`${proto}//${location.host}/api/speakers/talk?ids=${ids.join(",")}&access_token=${encodeURIComponent(Api.token || "")}`);
  ws.binaryType = "arraybuffer";
  state.ws = ws;
  ws.onmessage = (e) => {
    if (typeof e.data !== "string") return;
    let msg = null;
    try { msg = JSON.parse(e.data); } catch { return; }
    if (msg?.type !== "ready") return;
    const names = (msg.speakers || []).map((s) => s.name).join(", ");
    const rejected = (msg.rejected || []).map((r) => `${r.speakerName} (${r.message})`).join("; ");
    lvSpkMsg(`Hablando por ${names || "los parlantes"}${rejected ? `. No aceptaron: ${rejected}` : ""}.`, !!rejected && !names);
    if (lv.prefs.tone) ws.send(lvToneFrames());
    state.ready = true;
  };
  ws.onerror = () => lvSpkMsg("No se pudo abrir la voz en vivo con el servidor.", true);
  ws.onclose = (e) => { if (lv.talk === state) { lvStopTalk(); if (e.code !== 1000 && e.reason) lvSpkMsg(e.reason, true); } };
}

function lvStopTalk() {
  const state = lv.talk;
  lv.talk = null;
  if (!state) return;
  try { if (state.ws?.readyState === WebSocket.OPEN) { state.ws.send("stop"); state.ws.close(1000); } } catch { /* noop */ }
  try { state.node?.disconnect(); } catch { /* noop */ }
  state.ctx?.close().catch(() => {});
  state.stream?.getTracks().forEach((t) => t.stop());
  const talk = $("#lv-spk-talk");
  if (talk) { talk.classList.remove("on"); talk.textContent = "Mantener para hablar"; }
  $("#lv-spk-test")?.classList.remove("on");
  const bar = $("#lv-spk-level");
  if (bar) bar.style.width = "0";
  if (!state.testing) lvSpkMsg("Voz en vivo terminada.");
}

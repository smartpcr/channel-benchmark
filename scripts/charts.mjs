// Draws the README charts from a run.ps1 results CSV as static SVGs, one
// light and one dark file per metric, and prints the winner table as
// Markdown.
//
//   node scripts/charts.mjs [results.csv] [output dir]
//
// Defaults: docs/results.csv and docs/charts.

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [input = 'docs/results.csv', outDir = 'docs/charts'] = process.argv.slice(2);

const LANGS = [
  { key: 'go', name: 'Go' },
  { key: 'rust', name: 'Rust' },
  { key: 'csharp', name: 'C#' },
];

const METRICS = [
  {
    key: 'throughput', title: 'Throughput', unit: 'million messages per second', higherIsBetter: true,
    value: (r) => r.msg_per_second / 1e6,
  },
  {
    key: 'cpu', title: 'CPU time per message', unit: 'microseconds of process CPU', higherIsBetter: false,
    value: (r) => r.cpu_ns_per_msg / 1e3,
  },
  {
    key: 'alloc', title: 'Heap allocation per message', unit: 'bytes', higherIsBetter: false,
    value: (r) => r.alloc_bytes_per_msg,
  },
];

// Validated categorical slots 1-3 (blue, orange, aqua) and chart chrome.
const THEMES = {
  light: {
    surface: '#fcfcfb', primary: '#0b0b0b', secondary: '#52514e', muted: '#898781',
    grid: '#e1e0d9', axis: '#c3c2b7',
    series: { go: '#2a78d6', rust: '#eb6834', csharp: '#1baf7a' },
  },
  dark: {
    surface: '#1a1a19', primary: '#f0efec', secondary: '#c3c2b7', muted: '#898781',
    grid: '#2c2c2a', axis: '#383835',
    series: { go: '#3987e5', rust: '#d95926', csharp: '#199e70' },
  },
};

const FONT = 'system-ui, -apple-system, &quot;Segoe UI&quot;, sans-serif';
const BAR = 16; // bar thickness, under the 24px cap
const GAP = 2; // surface gap between adjacent bars
const RADIUS = 4; // rounded data end

// ---- data -----------------------------------------------------------------

const [header, ...lines] = readFileSync(input, 'utf8').trim().split(/\r?\n/);
const cols = header.split(',');
const rows = lines.map((line) => {
  const cells = line.split(',');
  const row = Object.fromEntries(cols.map((c, i) => [c, cells[i]]));
  for (const c of cols) if (c !== 'language') row[c] = Number(row[c]);
  return row;
});

// One case per payload/topics/consumers/work combination, holding each
// metric's value per language.
const caseMap = new Map();
for (const r of rows) {
  const id = [r.payload_bytes, r.topics, r.consumers_per_topic, r.work_rounds].join('/');
  if (!caseMap.has(id)) {
    caseMap.set(id, {
      payload: r.payload_bytes, topics: r.topics, consumers: r.consumers_per_topic,
      work: r.work_rounds, values: Object.fromEntries(METRICS.map((m) => [m.key, {}])),
    });
  }
  for (const m of METRICS) caseMap.get(id).values[m.key][r.language] = m.value(r);
}
const all = [...caseMap.values()];
const payloads = [...new Set(all.map((c) => c.payload))].sort((a, b) => a - b);
const workLevels = [...new Set(all.map((c) => c.work))].sort((a, b) => a - b);
const fmtBytes = (b) => (b >= 1024 ? `${b / 1024} KB` : `${b} B`);
const fmtWork = (w) => (w === 0 ? 'no extra work' : `${w.toLocaleString('en-US')} work rounds`);

function best(metric, c) {
  const vals = LANGS.map((l) => c.values[metric.key][l.key]);
  return metric.higherIsBetter ? Math.max(...vals) : Math.min(...vals);
}

// How close a value is to the best in its case: 1 is best, 0.5 is half as
// good (half the throughput, or twice the CPU or allocation).
function ratio(metric, c, lang) {
  const v = Math.max(c.values[metric.key][lang], 1e-9);
  const b = Math.max(best(metric, c), 1e-9);
  return metric.higherIsBetter ? v / b : b / v;
}

// Per language: geometric mean of the ratios, and the number of cases won.
function standings(metric, subset) {
  return LANGS.map((l) => {
    const logs = subset.map((c) => Math.log(ratio(metric, c, l.key)));
    return {
      ...l,
      score: Math.exp(logs.reduce((a, b) => a + b, 0) / logs.length),
      wins: subset.filter((c) => ratio(metric, c, l.key) === 1).length,
    };
  }).sort((a, b) => b.score - a.score);
}

// ---- svg helpers ------------------------------------------------------------

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function text(x, y, s, { size = 12, fill, anchor = 'start', weight = 400 } = {}) {
  return `<text x="${x}" y="${y}" font-size="${size}" font-weight="${weight}" fill="${fill}" `
    + `text-anchor="${anchor}" dominant-baseline="middle">${esc(s)}</text>`;
}

// Vertical column from baseline yb up to yt, rounded at the data end only.
function vbar(x, w, yb, yt, fill) {
  if (yb - yt < 0.5) return '';
  const r = Math.min(RADIUS, (yb - yt) / 2, w / 2);
  return `<path d="M${x} ${yb}V${yt + r}Q${x} ${yt} ${x + r} ${yt}H${x + w - r}`
    + `Q${x + w} ${yt} ${x + w} ${yt + r}V${yb}Z" fill="${fill}"/>`;
}

function legend(x, y, t) {
  let out = '';
  for (const l of LANGS) {
    out += `<rect x="${x}" y="${y - 5}" width="10" height="10" rx="2" fill="${t.series[l.key]}"/>`;
    out += text(x + 16, y, l.name, { fill: t.secondary });
    x += 16 + l.name.length * 8 + 22;
  }
  return out;
}

// Rounds an axis maximum up to 1, 2, 2.5 or 5 times a power of ten.
function niceTicks(max) {
  const raw = max / 4;
  const pow = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * pow).find((s) => s >= raw);
  const ticks = [];
  for (let v = 0; v <= max + step * 0.999; v += step) ticks.push(Number(v.toPrecision(6)));
  return ticks;
}

const fmtNum = (v) => {
  if (v === 0) return '0';
  if (v >= 100) return v.toFixed(0);
  if (v >= 10) return v.toFixed(1);
  if (v >= 1) return v.toFixed(2);
  return v.toPrecision(2);
};

// ---- chart: one metric, small multiples by payload and work ------------------

function metricChart(metric, t) {
  const panels = payloads.flatMap((p) => workLevels.map((w) => ({
    title: `${fmtBytes(p)} messages, ${fmtWork(w)}`,
    cases: all.filter((c) => c.payload === p && c.work === w)
      .sort((a, b) => a.topics - b.topics || a.consumers - b.consumers),
  })));
  const width = 760;
  const panelW = 356;
  const panelH = 220;
  const colGap = 24;
  const rowGap = 28;
  const top = 74;
  const rowsOfPanels = Math.ceil(panels.length / 2);
  const height = top + rowsOfPanels * panelH + (rowsOfPanels - 1) * rowGap + 12;
  const direction = metric.higherIsBetter ? 'higher is better' : 'lower is better';

  let body = text(24, 26, `${metric.title} (${metric.unit}, ${direction})`, {
    size: 14, weight: 600, fill: t.primary,
  });
  body += legend(24, 48, t);
  body += text(width - 24, 48, 'Groups: topics × consumers per topic. Best value labelled.', {
    fill: t.muted, anchor: 'end', size: 11,
  });

  panels.forEach((panel, i) => {
    const px = 24 + (i % 2) * (panelW + colGap);
    const py = top + Math.floor(i / 2) * (panelH + rowGap);
    const left = px + 40;
    const plotTop = py + 42;
    const plotBottom = py + panelH - 26;
    const plotW = px + panelW - left;
    const peak = Math.max(...panel.cases.flatMap((c) => LANGS.map((l) => c.values[metric.key][l.key])));
    const ticks = niceTicks(peak);
    const max = ticks[ticks.length - 1];
    const y = (v) => plotBottom - (v / max) * (plotBottom - plotTop);

    body += text(px, py + 10, panel.title, { fill: t.primary, weight: 600, size: 12 });
    for (const v of ticks) {
      body += `<line x1="${left}" y1="${y(v)}" x2="${left + plotW}" y2="${y(v)}" `
        + `stroke="${v === 0 ? t.axis : t.grid}" stroke-width="1"/>`;
      body += text(left - 6, y(v), String(v), { fill: t.muted, anchor: 'end', size: 11 });
    }
    const slot = plotW / panel.cases.length;
    const groupW = LANGS.length * BAR + (LANGS.length - 1) * GAP;
    panel.cases.forEach((c, ci) => {
      const gx = left + ci * slot + (slot - groupW) / 2;
      LANGS.forEach((l, li) => {
        body += vbar(gx + li * (BAR + GAP), BAR, y(0), y(c.values[metric.key][l.key]), t.series[l.key]);
      });
      // Label only the best value, above the group's tallest column.
      const b = best(metric, c);
      const winner = LANGS.find((l) => c.values[metric.key][l.key] === b);
      const tallest = Math.max(...LANGS.map((l) => c.values[metric.key][l.key]));
      body += text(gx + groupW / 2, y(tallest) - 10, `${winner.name} ${fmtNum(b)}`, {
        fill: t.primary, anchor: 'middle', size: 11, weight: 600,
      });
      body += text(gx + groupW / 2, plotBottom + 14, `${c.topics} × ${c.consumers}`, {
        fill: t.secondary, anchor: 'middle', size: 11,
      });
    });
  });

  const label = `${metric.title} per case for Go, Rust and C#`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" `
    + `viewBox="0 0 ${width} ${height}" role="img" aria-label="${esc(label)}" font-family="${FONT}">\n`
    + `<title>${esc(label)}</title>\n`
    + `<rect width="${width}" height="${height}" rx="8" fill="${t.surface}"/>\n${body}\n</svg>\n`;
}

// ---- output -----------------------------------------------------------------

mkdirSync(outDir, { recursive: true });
for (const metric of METRICS) {
  for (const [mode, t] of Object.entries(THEMES)) {
    writeFileSync(join(outDir, `${metric.key}-${mode}.svg`), metricChart(metric, t));
  }
}

// Winner table: one row per metric, one column per slice of the cases.
const slices = [
  { label: 'All cases', subset: all },
  ...payloads.map((p) => ({ label: `${fmtBytes(p)} messages`, subset: all.filter((c) => c.payload === p) })),
  ...workLevels.map((w) => ({
    label: w === 0 ? 'No extra work' : `${w.toLocaleString('en-US')} work rounds`,
    subset: all.filter((c) => c.work === w),
  })),
];
const table = [
  `| Category | ${slices.map((s) => s.label).join(' | ')} |`,
  `|---|${slices.map(() => '---').join('|')}|`,
  ...METRICS.map((m) => {
    const cells = slices.map((s) => {
      const [first, second] = standings(m, s.subset);
      const ratio = first.score / second.score;
      const lead = ratio >= 2 ? `${ratio.toFixed(0)}×` : `${Math.round((ratio - 1) * 100)}%`;
      const close = ratio < 1.05 ? ' (close)' : '';
      return `**${first.name}**${close}, ${lead} ahead of ${second.name}; won ${first.wins} of ${s.subset.length}`;
    });
    return `| ${m.title} | ${cells.join(' | ')} |`;
  }),
];
console.log(table.join('\n'));
console.log();
for (const m of METRICS) {
  console.log(`${m.title}: ${standings(m, all).map((s) => `${s.name} ${(s.score * 100).toFixed(0)}% (${s.wins} wins)`).join(', ')}`);
}

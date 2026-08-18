/**
 * Inline SVG icon set.
 *
 * Emoji were the wrong call: they render differently on every machine, ignore
 * the current text colour, and cannot be sized to sit on a text baseline. These
 * are 24x24 stroke icons that inherit `currentColor`, so one icon works in both
 * themes and on coloured buttons.
 */

const PATHS = {
  /* --- files and structure --- */
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  folderOpen: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2"/><path d="m3 9 2.5 9a1 1 0 0 0 1 .8h12a1 1 0 0 0 1-.8L22 9z"/>',
  archive: '<rect x="3" y="4" width="18" height="5" rx="1"/><path d="M5 9v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9"/><path d="M10 13h4"/>',
  file: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>',
  image: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="8.5" cy="9.5" r="1.5"/><path d="m21 16-5-5-9 9"/>',
  layers: '<path d="m12 3 9 5-9 5-9-5z"/><path d="m3 13 9 5 9-5"/>',
  hash: '<path d="M4 9h16M4 15h16M10 3 8 21M16 3l-2 18"/>',
  type: '<path d="M4 7V5h16v2M12 5v14M9 19h6"/>',

  /* --- actions --- */
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  trash: '<path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2"/><path d="M19 6v14a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6"/><path d="M10 11v6M14 11v6"/>',
  copy: '<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1"/>',
  edit: '<path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>',
  save: '<path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/><path d="M17 21v-8H7v8M7 3v5h8"/>',
  download: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5M12 15V3"/>',
  upload: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m17 8-5-5-5 5M12 3v12"/>',
  undo: '<path d="M9 14 4 9l5-5"/><path d="M4 9h10a6 6 0 0 1 0 12h-4"/>',
  redo: '<path d="m15 14 5-5-5-5"/><path d="M20 9H10a6 6 0 0 0 0 12h4"/>',
  replace: '<path d="M14 4h5v5"/><path d="M19 4 4 19"/><path d="M10 20H5v-5"/>',
  close: '<path d="M18 6 6 18M6 6l12 12"/>',
  check: '<path d="m20 6-11 11-5-5"/>',

  /* --- navigation --- */
  chevronRight: '<path d="m9 18 6-6-6-6"/>',
  chevronDown: '<path d="m6 9 6 6 6-6"/>',
  arrowLeft: '<path d="M19 12H5M12 19l-7-7 7-7"/>',
  arrowRight: '<path d="M5 12h14M12 5l7 7-7 7"/>',
  arrowUp: '<path d="M12 19V5M5 12l7-7 7 7"/>',
  arrowDown: '<path d="M12 5v14M19 12l-7 7-7-7"/>',
  externalLink: '<path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/><path d="M15 3h6v6M10 14 21 3"/>',

  /* --- state --- */
  star: '<path d="m12 3 2.9 5.9 6.5.9-4.7 4.6 1.1 6.5-5.8-3-5.8 3 1.1-6.5L3.4 9.8l6.5-.9z"/>',
  pin: '<path d="m9 3 6 6"/><path d="m7 8 9 9"/><path d="M13 4 8 9l-1 5 3 3 5-1 5-5z"/><path d="m9 15-6 6"/>',
  dot: '<circle cx="12" cy="12" r="5" fill="currentColor" stroke="none"/>',
  alert: '<path d="M12 3 2 20h20z"/><path d="M12 10v4M12 17.5v.01"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8.5v.01"/>',
  help: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.2 2.4c-.5.2-.7.6-.7 1.1v.5M12 17v.01"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  lock: '<rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',

  /* --- view --- */
  grid: '<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
  list: '<path d="M8 6h13M8 12h13M8 18h13M3.5 6h.01M3.5 12h.01M3.5 18h.01"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
  moon: '<path d="M20 14.5A8.5 8.5 0 1 1 9.5 4a7 7 0 0 0 10.5 10.5z"/>',
  play: '<path d="M6 4l14 8-14 8z"/>',
  pause: '<path d="M8 4v16M16 4v16"/>',
  cart: '<circle cx="9" cy="20" r="1.5"/><circle cx="18" cy="20" r="1.5"/><path d="M2 3h3l2.5 12h11L21 7H6"/>',
  sliders: '<path d="M4 8h10M18 8h2M4 16h4M12 16h8"/><circle cx="16" cy="8" r="2"/><circle cx="10" cy="16" r="2"/>',
  brush: '<path d="M17 3a3 3 0 0 1 4 4L9 19l-5 2 2-5z"/><path d="m14 6 4 4"/>',
  eraser: '<path d="M4 16 14 6a2 2 0 0 1 3 0l4 4a2 2 0 0 1 0 3l-8 8H7z"/><path d="M9 21h12M11 9l7 7"/>',
  bucket: '<path d="m5 12 7-7 7 7-7 7z"/><path d="M19 15c1.3 1.7 2 2.9 2 3.6a2 2 0 1 1-4 0c0-.7.7-1.9 2-3.6z"/>',
  pipette: '<path d="m14 4 6 6"/><path d="m17 7-9 9-3 5 5-3 9-9"/><path d="M12 9 5 16"/>',
  line: '<path d="M5 19 19 5"/><circle cx="19" cy="5" r="2"/><circle cx="5" cy="19" r="2"/>',
  square: '<rect x="4" y="4" width="16" height="16" rx="1"/>',
  grid4: '<path d="M3 9h18M3 15h18M9 3v18M15 3v18"/>',
  zoomIn: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5M11 8v6M8 11h6"/>',
  zoomOut: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5M8 11h6"/>',
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/>',
  filter: '<path d="M3 5h18l-7 8v6l-4 2v-8z"/>',
  refresh: '<path d="M3 12a9 9 0 0 1 15.5-6.2L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-15.5 6.2L3 16"/><path d="M3 21v-5h5"/>',
  more: '<circle cx="5" cy="12" r="1.7" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="1.7" fill="currentColor" stroke="none"/><circle cx="19" cy="12" r="1.7" fill="currentColor" stroke="none"/>',

  /* --- brand --- */
  // The MapleBench mark: a bench edge with three tools standing on it. Same
  // 24-unit grid as everything above, but seven FILLED axis-aligned rectangles
  // rather than a stroked outline -- render it with brandMark(), never icon().
  // Canonical source: branding/maplebench-mark.svg.
  brandMark: '<path d="M3.7 7.4H6.7V14.2H3.7ZM9 4.1H15V6.9H9ZM10.8 6.5H13.2V14.2H10.8ZM17.3 10.6H20.5V14.2H17.3ZM2.4 13.8H21.6V16.6H2.4ZM4.4 16.2H7.2V19.4H4.4ZM16.8 16.2H19.6V19.4H16.8Z"/>',
};

/**
 * Returns an <svg> element for the named icon.
 * `size` is in px; stroke width is scaled so small icons stay crisp.
 */
export function icon(name, { size = 16, strokeWidth } = {}) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', String(strokeWidth ?? (size <= 14 ? 2.2 : 1.9)));
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  svg.style.flex = 'none';
  // Icon names arrive dynamically (emptyState, showMenu), so an unguarded
  // property read would let 'constructor' or 'toString' stringify a function
  // straight into the markup instead of falling back.
  svg.innerHTML = Object.hasOwn(PATHS, name) ? PATHS[name] : PATHS.file;
  return svg;
}

/**
 * The brand mark, as its own <svg>.
 *
 * icon() puts fill="none" stroke="currentColor" on the root element, which is
 * correct for the stroke icons above and wrong for this one: the mark is seven
 * solid rectangles, so stroking it draws seven hollow outlines instead of the
 * shape in branding/maplebench-mark.svg. The shared builder cannot express
 * fill-only without growing a second rendering mode for a single path, so the
 * mark builds its own element. It still takes its colour from `currentColor`,
 * which is what lets .brand-mark's flat fill drive it from CSS.
 */
export function brandMark(size = 20) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('fill', 'currentColor');
  svg.setAttribute('stroke', 'none');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  svg.style.flex = 'none';
  svg.innerHTML = PATHS.brandMark;
  return svg;
}

/** Icon for a tree node, chosen from its kind and property type. */
export function nodeIcon(node, size = 14) {
  if (node.kind === 'File') return icon('archive', { size });
  if (node.kind === 'Directory') return icon('folder', { size });
  if (node.kind === 'Image') return icon('file', { size });

  switch (node.type) {
    case 'Canvas': return icon('image', { size });
    case 'Sound': return icon('play', { size });
    case 'SubProperty': return icon('layers', { size });
    case 'Convex': return icon('layers', { size });
    case 'UOL': return icon('externalLink', { size });
    case 'Vector': return icon('sliders', { size });
    case 'String': return icon('type', { size });
    case 'Int': case 'Short': case 'Long': case 'Float': case 'Double':
      return icon('hash', { size });
    default: return icon('dot', { size });
  }
}

export const ICON_NAMES = Object.keys(PATHS);

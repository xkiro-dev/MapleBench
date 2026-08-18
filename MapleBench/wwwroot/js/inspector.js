/**
 * The child table (main area) and the node inspector (right panel).
 *
 * Editing happens in always-visible inputs rather than click-to-edit: a WZ node
 * is a value, and hiding the affordance costs a click on every single field.
 */

import { api, ApiError, q } from './api.js';
import { el, clear, toast, toastError, fmt, confirmDialog, sortNodes, SORT_MODES, debounce, scalePixelArt, withoutFileId } from './ui.js';
import { AnimationPlayer } from './animation.js';
import { thumbUrl, canvasUrl, lazySprite, releaseSprite } from './media.js';
import { icon, nodeIcon } from './icons.js';

/** Types whose value can be typed as text. */
const TEXT_EDITABLE = new Set(['Int', 'Short', 'Long', 'Float', 'Double', 'String', 'UOL', 'Vector', 'Lua']);
const NUMERIC = new Set(['Int', 'Short', 'Long', 'Float', 'Double']);

const RANGES = {
  Short: [-32768, 32767],
  Int: [-2147483648, 2147483647],
};

/**
 * Child-table row height, in px, and fixed.
 *
 * The virtualiser derives every index from `scrollTop / ROW_H`, so nothing in a
 * row may grow past it -- .child-row in app.css pins it, and the two things
 * that could push it (a canvas thumbnail, an audio player) are capped there.
 */
const ROW_H = 34;
const OVERSCAN = 6;
/**
 * Hard ceiling on live rows, whatever the viewport says.
 *
 * The whole scheme rests on .child-vlist being the element that scrolls. If a
 * layout change ever let it grow to its content instead, clientHeight would be
 * the full 9,697 rows and this would quietly go back to building all of them.
 * 300 is ~7 screens; past that something is wrong and a short list is the safe
 * way to be wrong.
 */
const MAX_LIVE_ROWS = 300;

/**
 * Shortest gap between two inspect loads, in ms.
 *
 * Auto-repeat on an arrow key fires selection at ~30/s. Each selection used to
 * start three JSON reads plus up to 400 image requests, all queued on the
 * server's single session gate, so the tail of a two-second key press was still
 * arriving seconds after the user stopped moving.
 */
const SHOW_INTERVAL = 120;

/**
 * Names that are an item id and nothing else: "01002316.img", "0100100".
 *
 * These get set in the mono face with tabular figures, so 3,331 Caps read as a
 * column of aligned digits instead of 3,331 unrelated words. Deliberately not
 * applied to every name -- "info" and "reqLevel" are prose and look wrong in
 * it, and the point is to separate the two kinds of row, not to typeset both
 * the same way.
 */
const ID_NAME = /^\d{3,}(\.img)?$/i;

/**
 * How long the selection has to stand still before the animation player and the
 * contact sheet are asked for. Both are expensive -- one parses every frame
 * under the node, the other decodes up to 60 canvases -- and neither is worth
 * starting for a node the user is passing through.
 */
const EXTRAS_DELAY = 150;

/**
 * One parsed SVG per icon, cloned per row.
 *
 * icon() builds its element with innerHTML, which is an SVG parse. The child
 * table puts two on every row -- the type glyph and the row-actions dots -- so
 * opening Commodity.img used to pay 19,394 of them. Cloning a template is a
 * node copy instead. The key set is closed (icon names, and kind/type pairs),
 * so this cannot grow with the session.
 */
const iconTemplates = new Map();

function templateIcon(name, size) {
  const key = `i:${name}:${size}`;
  let built = iconTemplates.get(key);
  if (!built) { built = icon(name, { size }); iconTemplates.set(key, built); }
  return built.cloneNode(true);
}

function templateNodeIcon(node, size) {
  const key = `n:${node.kind}:${node.type ?? ''}:${size}`;
  let built = iconTemplates.get(key);
  if (!built) { built = nodeIcon(node, size); iconTemplates.set(key, built); }
  return built.cloneNode(true);
}

/**
 * A GET that can be cancelled.
 *
 * The two reads made per selection are the expensive ones -- /api/inspect on
 * Commodity.img is 9,697 children in 1,248 ms, /api/preview walks up to 60
 * canvases -- and both are served under the session gate, so abandoning one
 * without cancelling it makes the *next* selection wait for work nobody wants
 * any more. Same ApiError shape as api.js, so callers still show the backend's
 * message verbatim.
 */
async function getJson(url, signal) {
  const response = await fetch(url, { signal });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    let detail;
    try {
      const payload = await response.json();
      message = payload.message || message;
      detail = payload.detail;
    } catch {
      /* non-JSON error body; keep the status line */
    }
    throw new ApiError(message, detail, response.status);
  }
  return response.json();
}

const isAbort = (error) => error?.name === 'AbortError';

export class Inspector {
  constructor({ childHost, inspectorHost, app }) {
    this.childHost = childHost;
    this.inspectorHost = inspectorHost;
    this.app = app;
    this.path = null;
    this.node = null;
    this.children = [];
    /** `children` filtered and sorted: what the virtualiser indexes. */
    this.rows = [];
    this.childFilter = '';
    /**
     * Child path -> the facts /api/node/facts found for it, or null while the
     * folder has not been asked about yet.
     *
     * Null and empty mean different things and the UI shows different things
     * for them, which is the whole reason this is not just a Set: an empty map
     * means "asked, and none of these are cash", and null means "not asked, or
     * this folder does not carry the flag at all". A marker that appears before
     * the answer is in would be a lie for as long as the scan takes, and a Cap
     * folder takes 5.7s.
     */
    this.facts = null;
    /** 'all' | 'cash' | 'free'. Only offered once `facts` is in. */
    this.cashFilter = 'all';
    this.sortMode = localStorage.getItem('mb.sort') || 'name';
    /** The live child table, or null while there is not one. */
    this.table = null;
    this.lastShowAt = 0;
    this.showTimer = null;
    this.extrasTimer = null;
    /** Aborts the JSON reads belonging to the node being left. */
    this.reads = null;
    /** Aborts every sprite this node's table and panel still have queued. */
    this.sprites = null;
    this.sizeObserver = null;
  }

  /**
   * Opens a node, at most once every SHOW_INTERVAL ms.
   *
   * An isolated selection is served immediately -- the interval has always
   * elapsed -- and only a burst is thinned, which is exactly the case that used
   * to bury the server. Everything the previous node had in flight is cancelled
   * here rather than merely ignored: the point is to give the gate back.
   */
  show(path) {
    // A new node starts with a clean filter; carrying one over hides children
    // for no visible reason. The cash filter and the facts behind it go with
    // it -- they are about this folder's children and mean nothing for the
    // next folder's.
    if (path !== this.path) {
      this.childFilter = '';
      this.cashFilter = 'all';
      this.facts = null;
    }
    this.path = path;
    this.cancelPending();
    this.table = null;
    clear(this.childHost);
    this.childHost.append(skeletonTable());

    const wait = Math.max(0, SHOW_INTERVAL - (performance.now() - this.lastShowAt));
    return new Promise((resolve) => {
      // Handed to cancelPending() as well as to the timer. A show that is
      // superseded before it runs still has to settle: navigate() and setMode()
      // both await this one, and a promise that is quietly dropped instead
      // leaves them -- and anything holding a runOnce key behind them --
      // waiting for ever. Selecting from the tree supersedes a show on every
      // single keypress.
      this.pendingShow = resolve;
      this.showTimer = setTimeout(() => {
        this.showTimer = null;
        this.pendingShow = null;
        this.lastShowAt = performance.now();
        this.load(path).then(resolve, resolve);
      }, wait);
    });
  }

  async load(path) {
    const reads = new AbortController();
    this.reads = reads;
    try {
      const data = await getJson(`/api/inspect?path=${q(path)}`, reads.signal);
      if (this.path !== path) return;      // a newer navigation won the race
      this.node = data.node;
      this.children = data.children;
      this.renderChildren();
      this.renderInspector();
    } catch (error) {
      if (isAbort(error) || this.path !== path) return;
      clear(this.childHost);
      this.table = null;
      this.childHost.append(emptyState('alert', 'Could not open this node', error.message));
      toastError(error);
    }
  }

  /** Drops every request and timer belonging to the node being left. */
  cancelPending() {
    clearTimeout(this.showTimer);
    this.showTimer = null;
    if (this.pendingShow) { const settle = this.pendingShow; this.pendingShow = null; settle(); }
    clearTimeout(this.extrasTimer);
    this.extrasTimer = null;
    this.reads?.abort();
    this.reads = null;
    this.sprites?.abort();
    this.sprites = null;
    this.sizeObserver?.disconnect();
    this.sizeObserver = null;
    if (this.rowFrame) { cancelAnimationFrame(this.rowFrame); this.rowFrame = null; }
  }

  /**
   * Re-reads the current node without disturbing the rest of the app.
   *
   * The facts go with it. A refresh follows a structural edit -- a child added,
   * deleted, renamed -- and the answer is a map keyed by child path, so after
   * one of those it describes children that may no longer be the children.
   * Re-asking costs a rescan (5.7s for a 3,331-child folder, and the server's
   * own cache has been invalidated by the same edit), which is the right way
   * round: a marker on the wrong row is worse than a marker that is late.
   */
  refresh() {
    this.facts = null;
    if (this.path) return this.show(this.path);
  }

  /* ============================================================
     CHILD TABLE
     ============================================================ */

  /**
   * Builds the child table once per node; updateRows() does everything after.
   *
   * Only the rows in view exist in the DOM. Commodity.img has 9,697 children
   * and a row is ~14 elements, so building them all made ~136,000 -- then threw
   * them away and did it again on every filter keystroke and every sort change.
   * The approach is tree.js's, for the same reasons and under the same
   * constraint: fixed row height, a pool of recycled row elements, scroll
   * coalesced into a frame, passive listener.
   */
  renderChildren() {
    this.sizeObserver?.disconnect();
    this.sizeObserver = null;
    // Everything the previous table had queued belongs to a node that is no
    // longer on screen.
    this.sprites?.abort();
    this.sprites = new AbortController();
    this.table = null;
    clear(this.childHost);

    if (!this.children.length) {
      this.childHost.append(emptyState(
        'layers',
        'This node is empty',
        this.node?.kind === 'Directory'
          ? 'Add an image or a folder to get started.'
          : 'Add a property to get started.',
        el('button', {
          class: 'btn btn-primary', text: 'Add child',
          onclick: () => this.app.promptAddChild(this.path),
        }),
      ));
      return;
    }

    const countLabel = el('span', { class: 'muted num', style: 'font-size:var(--fs-12)' });
    const filterInput = el('input', {
      class: 'field-input', type: 'search', value: this.childFilter,
      placeholder: `Filter this folder (${fmt.format(this.children.length)})\u2026`,
      'aria-label': 'Filter this folder',
    });
    // The toolbar outlives a filter change now, so the caret stays where the
    // user left it rather than being restored by hand afterwards.
    filterInput.addEventListener('input', debounce(() => this.setFilter(filterInput.value), 140));

    const scroller = el('div', { class: 'child-vlist', role: 'rowgroup' });
    const sizer = el('div', { class: 'child-sizer' });
    const rowsHost = el('div', { class: 'child-rows' });
    scroller.append(sizer, rowsHost);
    const nomatch = el('div', { class: 'child-nomatch' });
    const head = this.buildHeaderRow();
    // Empty until the facts land, and hidden by CSS while it is.
    const factBar = el('div', { class: 'cash-tabs' });

    // Drives the column widths: see .child-card[data-kind="Directory"].
    const card = el('div', {
      class: 'card child-card', role: 'table', 'aria-label': 'Children',
      'data-kind': this.node?.kind || '',
    },
      this.buildTableToolbar(countLabel, filterInput, factBar),
      head, scroller, nomatch);

    this.table = {
      card, head, countLabel, filterInput, factBar, scroller, sizer, rowsHost, nomatch,
      pool: [], first: -1, pinned: null, dirty: true,
    };

    scroller.addEventListener('scroll', () => this.scheduleRows(), { passive: true });
    this.sizeObserver = new ResizeObserver(() => this.scheduleRows());
    this.sizeObserver.observe(scroller);

    this.childHost.append(card);
    this.updateRows();

    // Started after the table is on screen and never awaited: reading which of
    // these children are cash items means parsing every one of them, which is
    // 5.7s for Character.wz/Cap's 3,331 on a v232 client. Same deal the
    // thumbnails have -- the list is usable immediately and the markers arrive
    // when they arrive.
    this.loadFacts(this.path);
  }

  /**
   * Asks what the children of this folder are, and marks them when the answer
   * comes back.
   *
   * Uses `this.reads`, so navigating away cancels it: the scan is served under
   * the server's single session gate, and leaving one running for a folder
   * nobody is looking at makes the *next* folder wait for it.
   */
  async loadFacts(path) {
    // Properties do not carry these flags and a leaf has nothing to mark. The
    // server's own sample would reject both, but that costs a round trip and a
    // few image parses to say what the node kind already says.
    if (this.node && this.node.kind !== 'Directory' && this.node.kind !== 'Image') return;
    if (this.children.length < 2) return;

    // Already answered for this node. renderChildren() runs again whenever the
    // card is rebuilt -- updateRows() does it when #child-list has been emptied
    // under us -- and the answer is about the node, not about the card, so the
    // pills are simply put back rather than asked for a second time.
    if (this.facts) { this.buildFactBar(); return; }

    let data;
    try {
      data = await getJson(`/api/node/facts?path=${q(path)}`, this.reads?.signal);
    } catch {
      // A folder with no markers is exactly what the user had before this
      // existed, so a failure here is not worth a toast on top of whatever
      // else has gone wrong.
      return;
    }
    if (this.path !== path || !this.table) return;

    // Not applicable means the sample found no cash flag anywhere in this
    // folder -- a Mob.wz, a Map.wz/Back. Leaving `facts` null keeps both the
    // markers and the filter off, rather than offering a filter that would
    // always come back empty.
    if (!data.applicable) return;

    this.facts = new Map(Object.entries(data.facts || {}));
    this.buildFactBar();
    // The rows on screen were painted before the answer existed, so they are
    // marked in place. Everything painted after this reads `facts` in
    // paintChildRow, including the pooled rows recycled by the next scroll.
    for (const row of this.table.pool) this.markRow(row, row.dataset.path);
  }

  /** Writes one row's cash marker from `facts`. */
  markRow(row, path) {
    const chip = row.querySelector('.cn-cash');
    if (!chip) return;
    chip.textContent = this.facts?.get(path)?.cash ? '$' : '';
  }

  /**
   * The All / Cash / Not cash pills, built once the counts are known.
   *
   * A folder of 3,331 Caps is 2,346 cash and 985 not, and until now there was
   * no way to see either group on its own -- the flag is four levels down
   * inside each image, so not even the filter box could reach it.
   */
  buildFactBar() {
    const table = this.table;
    if (!table) return;

    const cash = this.children.filter((child) => this.facts?.get(child.path)?.cash).length;
    const counts = { all: this.children.length, cash, free: this.children.length - cash };

    clear(table.factBar);
    table.factBar.append(...[
      ['all', 'All'], ['cash', 'Cash'], ['free', 'Not cash'],
    ].map(([value, label]) => el('button', {
      class: 'cat-tab cash-tab',
      'aria-pressed': this.cashFilter === value ? 'true' : 'false',
      'data-tip': value === 'cash'
        ? 'Only children whose info/cash is set'
        : value === 'free' ? 'Only children whose info/cash is not set' : 'Every child',
      onclick: () => {
        this.cashFilter = value;
        // Rebuilt rather than toggled: the counts move with the text filter
        // too, so there is one place that decides what these pills say.
        this.buildFactBar();
        this.updateRows();
      },
    }, label, el('span', { class: 'cat-count', text: fmt.format(counts[value]) }))));
  }

  /** Clicking a header cycles that column between ascending and descending. */
  buildHeaderRow() {
    const header = (label, mode, altMode) => {
      const active = this.sortMode === mode || this.sortMode === altMode;
      return el('div', {
        class: 'child-cell child-th', role: 'columnheader',
        'data-sorted': active ? 'true' : null,
        'data-tip': `Sort by ${label.toLowerCase()}`,
        onclick: () => this.setSort(this.sortMode === mode && altMode ? altMode : mode),
      },
        el('span', { text: label }),
        active
          ? el('span', {
              style: 'margin-left:5px;font-size:9px',
              text: this.sortMode === altMode ? '\u25bc' : '\u25b2',
            })
          : null);
    };

    return el('div', { class: 'child-row child-head', role: 'row' },
      header('Name', 'name', 'name-desc'),
      header('Type', 'type', null),
      el('div', { class: 'child-cell child-th', role: 'columnheader', text: 'Value' }),
      el('div', { class: 'child-cell child-th', role: 'columnheader', 'aria-label': 'Actions' }));
  }

  /** Filter box, result count, cash pills and sort picker above the child table. */
  buildTableToolbar(countLabel, filterInput, factBar) {
    const sort = el('select', {
      class: 'field-input', style: 'width:auto;padding-left:10px',
      'data-tip': 'Change the order of this list',
      onchange: (event) => this.setSort(event.target.value),
    }, SORT_MODES.map(([value, label]) =>
      el('option', { value, text: label, selected: value === this.sortMode })));

    return el('div', { class: 'table-toolbar' },
      el('div', { class: 'input-wrap', style: 'flex:1;max-width:340px' },
        el('span', { class: 'in-icon' }, icon('filter', { size: 14 })),
        filterInput),
      countLabel,
      el('span', { style: 'margin-left:auto' }),
      factBar,
      sort);
  }

  /**
   * Re-filters, re-sorts and repaints, without rebuilding anything.
   *
   * This is what a filter keystroke costs now: one pass over the children, a
   * height on the sizer, and a repaint of the rows in view.
   */
  updateRows() {
    const table = this.table;
    if (!table) return;
    // #child-list is shared furniture: setMode, the welcome screen and the
    // section stages all empty it. A card that is no longer on the page has to
    // be rebuilt rather than painted into -- tree.js hit the same thing with
    // its borrowed row host, and the pool made it invisible until a repaint
    // landed on nothing.
    if (!table.card.isConnected) { this.renderChildren(); return; }

    const needle = this.childFilter.trim().toLowerCase();
    const wanted = this.facts ? this.cashFilter : 'all';
    const matched = (needle || wanted !== 'all')
      ? this.children.filter((child) => {
          if (wanted !== 'all') {
            const cash = !!this.facts?.get(child.path)?.cash;
            if (wanted === 'cash' ? !cash : cash) return false;
          }
          if (!needle) return true;
          return (child.name ?? '').toLowerCase().includes(needle) ||
            // The resolved name too. In a folder of ids it is the only part of
            // the row a person can actually spell, and leaving it out meant
            // typing "ice cream" into a list showing "Ice Cream Cap" matched
            // nothing at all.
            (child.displayName ?? '').toLowerCase().includes(needle) ||
            (child.value ?? '').toLowerCase().includes(needle) ||
            (child.type ?? '').toLowerCase().includes(needle);
        })
      : this.children;

    this.rows = sortNodes(matched, this.sortMode);

    table.countLabel.textContent = matched.length === this.children.length
      ? `${fmt.format(matched.length)} item${matched.length === 1 ? '' : 's'}`
      : `${fmt.format(matched.length)} of ${fmt.format(this.children.length)}`;

    const height = this.rows.length * ROW_H;
    table.sizer.style.height = `${height}px`;
    // Asked for exactly, capped by CSS at the height of the stage: four
    // children must not leave a screenful of empty scroller under them.
    table.scroller.style.height = `${height}px`;
    table.card.setAttribute('aria-rowcount', String(this.rows.length));

    clear(table.nomatch);
    if (!this.rows.length) {
      // Two different ways to end up here, and telling the user to clear a
      // filter box they never typed in is the kind of dead end that makes a
      // list feel broken.
      const byPill = wanted !== 'all' && !needle;
      table.nomatch.append(emptyState(
        'search',
        byPill
          ? (wanted === 'cash' ? 'Nothing here is a cash item' : 'Everything here is a cash item')
          : `Nothing here matches "${this.childFilter}"`,
        byPill
          ? 'The marker comes from each child’s info/cash property.'
          : 'The filter looks at names, resolved names, types and values.',
        el('button', {
          class: 'btn',
          text: byPill ? 'Show all children' : 'Clear filter',
          onclick: () => {
            if (byPill) { this.cashFilter = 'all'; this.buildFactBar(); this.updateRows(); }
            else this.setFilter('');
          },
        })));
    }

    // The row set is about to change under the pool, so the row pinned for its
    // caret is no longer the row at that index and cannot be left unpainted.
    // Blurring is how every edit in this table is committed anyway, so letting
    // go of it here costs the user nothing. (Filtering from the toolbar does
    // not reach this: the caret is in the toolbar, not in a row.)
    const active = document.activeElement;
    if (active && table.rowsHost.contains(active)) active.blur();

    table.dirty = true;
    this.paintRows();
  }

  scheduleRows() {
    if (this.rowFrame) return;
    this.rowFrame = requestAnimationFrame(() => {
      this.rowFrame = null;
      this.paintRows();
    });
  }

  paintRows() {
    const table = this.table;
    if (!table || !table.card.isConnected) return;

    const { scrollTop, clientHeight } = table.scroller;
    const first = Math.max(0, Math.floor(scrollTop / ROW_H) - OVERSCAN);
    const span = Math.min(Math.ceil((clientHeight || ROW_H) / ROW_H) + OVERSCAN * 2, MAX_LIVE_ROWS);
    const last = Math.min(this.rows.length, first + span);

    // The row being edited is never recycled: it keeps its element, and with it
    // the caret, the uncommitted text and the focus, even once it has scrolled
    // out of the window -- which is what a list that was not virtualised would
    // have done.
    const active = document.activeElement;
    const pinned = active && table.rowsHost.contains(active) ? active.closest('.child-row') : null;

    // A scroll event fires many times per row boundary crossed.
    if (!table.dirty && first === table.first && pinned === table.pinned) return;
    table.dirty = false;
    table.first = first;
    table.pinned = pinned;

    const needed = Math.max(0, last - first);
    const usable = pinned ? table.pool.filter((row) => row !== pinned) : [...table.pool];

    while (usable.length < needed) {
      const row = this.buildChildRow();
      table.pool.push(row);
      table.rowsHost.append(row);
      usable.push(row);
    }
    while (usable.length > needed) {
      const row = usable.pop();
      table.pool.splice(table.pool.indexOf(row), 1);
      releaseRow(row);
      row.remove();
    }

    let next = 0;
    for (let index = first; index < last; index++) {
      if (pinned && Number(pinned.dataset.index) === index) continue;
      this.paintChildRow(usable[next++], this.rows[index], index);
    }
    // The spare that the pinned row displaced; parked rather than removed,
    // because focus is about to move and it will be wanted straight back.
    for (; next < usable.length; next++) usable[next].style.display = 'none';
  }

  setFilter(value) {
    this.childFilter = value;
    if (this.table && this.table.filterInput.value !== value) this.table.filterInput.value = value;
    this.updateRows();
  }

  /** Sort is shared with the tree so both panes agree on the order. */
  setSort(mode) {
    this.sortMode = mode;
    localStorage.setItem('mb.sort', mode);
    this.app.tree?.setSortMode(mode);
    if (this.table) {
      const head = this.buildHeaderRow();
      this.table.head.replaceWith(head);
      this.table.head = head;
    }
    this.updateRows();
  }

  /**
   * An empty row shell.
   *
   * Built once per pool slot and repainted for ever after, so nothing here may
   * close over a child: the listeners read the row's current path out of the
   * dataset instead.
   */
  buildChildRow() {
    const row = el('div', { class: 'child-row', role: 'row', 'data-art': 'none' });

    const thumb = el('span', { class: 'row-thumb' },
      el('img', { alt: '', decoding: 'async' }),
      el('span', { class: 'rt-glyph' }));

    const image = thumb.firstElementChild;
    // Both listeners live on the pooled element, so they are attached long
    // before any src is assigned -- which is the invariant the card grids keep
    // tripping over, since a cached sprite fires `load` synchronously.
    image.addEventListener('load', () => {
      if (!image.naturalWidth) { row.dataset.art = 'none'; return; }
      row.dataset.art = 'ready';
      scalePixelArt(image, 26, 26);
      // One request, two pictures. The value cell of a Canvas row shows the
      // bitmap this thumbnail just loaded; it used to ask /api/canvas for it
      // separately, which is a second decode+encode of identical bytes on the
      // far side of the session gate.
      const twin = row.querySelector('.cv-thumb');
      if (twin) twin.src = image.src;
    });
    image.addEventListener('error', () => { row.dataset.art = 'none'; });

    const name = el('button', { class: 'child-name' },
      thumb,
      el('span', { class: 'cn-name' }),
      // Next to the id rather than in a column of its own: it belongs to the
      // name the way a currency symbol belongs to a number, and a fifth column
      // would cost 3,331 rows of width to be blank on most of them. Empty
      // until the facts arrive, and hidden by :empty while it is.
      el('span', { class: 'cn-cash', 'data-tip': 'Cash item — its info/cash is set' }),
      el('span', { class: 'cn-alias' }));
    name.addEventListener('click', () => {
      if (row.dataset.path) this.app.navigate(row.dataset.path);
    });

    const more = el('button', {
      class: 'btn btn-sm btn-icon btn-ghost', 'data-tip': 'Row actions',
    }, templateIcon('more', 15));
    more.addEventListener('click', (event) => {
      const path = row.dataset.path;
      if (!path) return;
      const rect = event.currentTarget.getBoundingClientRect();
      this.app.showMenu([
        { icon: 'copy', label: 'Duplicate', run: () => this.app.duplicate([path]) },
        { icon: 'copy', label: 'Copy path', run: () => this.app.copyPath(path) },
        { icon: 'trash', label: 'Delete', danger: true, run: () => this.app.deleteNodes([path]) },
      ], rect.left, rect.bottom + 4);
    });

    row.append(
      el('div', { class: 'child-cell c-name', role: 'cell' }, name),
      el('div', { class: 'child-cell c-type', role: 'cell' },
        el('span', { class: 'type-badge' })),
      el('div', { class: 'child-cell c-value', role: 'cell' }),
      el('div', { class: 'child-cell c-actions', role: 'cell' },
        el('div', { class: 'row-actions' }, more)));

    return row;
  }

  paintChildRow(row, child, index) {
    row.style.display = '';
    row.style.transform = `translateY(${index * ROW_H}px)`;
    row.setAttribute('aria-rowindex', String(index + 1));
    row.dataset.index = String(index);
    // Same child, same slot: everything below would write what is already there.
    if (row.dataset.path === child.path) return;
    row.dataset.path = child.path;
    row.dataset.kind = child.kind || '';
    row.dataset.type = child.type || '';
    row.dataset.previewLabel = child.name || '';

    const [nameCell, typeCell, valueCell] = row.children;
    const nameButton = nameCell.firstElementChild;
    const [thumb, label, cash, alias] = nameButton.children;
    const [image, glyph] = thumb.children;

    nameButton.dataset.tip = child.hasChildren ? 'Open this node' : 'Select this node';
    label.textContent = child.name || '(unnamed)';
    // Mono with tabular figures for an id, prose for a property name; see
    // ID_NAME. A data attribute rather than a class so the two are exclusive
    // and a recycled row cannot end up wearing both.
    label.dataset.id = ID_NAME.test(child.name || '') ? 'true' : 'false';
    cash.textContent = this.facts?.get(child.path)?.cash ? '$' : '';

    // The resolved human name, when the backend could find one: "Snail" after
    // 0100100.img. Secondary and dimmed on purpose -- it is decoration, the row
    // is still addressed by child.path, and it is missing far more often than
    // it is present, so nothing here may depend on it.
    alias.textContent = child.displayName && child.displayName !== child.name ? child.displayName : '';

    // The type glyph sits under the thumbnail and shows through whenever there
    // is no art, so a node without a sprite leaves no gap and nothing has to be
    // built to say so. Swapped only when the kind behind this slot changed;
    // rebuilding an SVG on every scroll frame is pure waste.
    const signature = `${child.kind}:${child.type ?? ''}`;
    if (glyph.dataset.sig !== signature) {
      glyph.dataset.sig = signature;
      glyph.replaceChildren(templateNodeIcon(child, 14));
    }

    // A thumbnail beats an icon whenever the node actually contains art:
    // "0100000.img" means nothing, the mob standing in it means everything.
    const showsArt = child.type === 'Canvas' || child.kind === 'Image'
      || child.kind === 'Directory' || child.type === 'SubProperty';

    row.dataset.art = 'none';
    releaseSprite(image);
    image.removeAttribute('src');
    // scalePixelArt() writes both, and a recycled row must not wear the size of
    // the sprite that used to be in it.
    image.style.width = '';
    image.style.height = '';
    if (showsArt) lazySprite(image, thumbUrl(child.path), { signal: this.sprites?.signal });

    const badge = typeCell.firstElementChild;
    badge.dataset.type = child.type || child.kind;
    badge.textContent = badgeText(child);

    valueCell.replaceChildren(this.buildValueCell(child));
  }

  buildValueCell(child) {
    if (child.kind !== 'Property') {
      // Blank, not an em dash, when the count is not known. An .img has no
      // count until it is opened and Mob.wz holds 2,606 of them, so this column
      // was a stack of 2,606 dashes with the occasional real number buried in
      // it -- an em dash repeated down a whole screen stops reading as "nothing
      // here" and starts reading as a column of content. Left blank, the only
      // marks in the column are the folder counts, which is what it is for.
      const count = child.childCount < 0 ? ''
        : `${fmt.format(child.childCount)} item${child.childCount === 1 ? '' : 's'}`;
      return el('span', { style: 'color:var(--text-3);font-size:var(--fs-12)', text: count });
    }

    if (child.type === 'Canvas') {
      // No src of its own: the row thumbnail is already loading these exact
      // bytes and hands them over when it has them (see buildChildRow).
      return el('div', { style: 'display:flex;align-items:center;gap:8px;min-width:0' },
        el('img', {
          class: 'cv-thumb', alt: '',
          style: 'max-height:24px;image-rendering:pixelated',
          onerror: (e) => { e.target.style.display = 'none'; },
        }),
        el('span', { style: 'color:var(--text-3);font-size:var(--fs-12)', text: child.value || '' }));
    }

    if (child.type === 'Sound') {
      return el('audio', { controls: true, src: api.audioUrl(child.path), style: 'height:26px;max-width:220px' });
    }

    if (!child.editable) {
      return el('span', { style: 'color:var(--text-3);font-size:var(--fs-12)', text: child.value ?? '—' });
    }

    return this.buildEditor(child);
  }

  /** An always-editable input that commits on blur or Enter and reverts on Esc. */
  buildEditor(child) {
    const input = el('input', {
      class: 'cell-input',
      value: child.value ?? '',
      spellcheck: 'false',
      'data-path': child.path,
      'aria-label': `${child.name} value`,
    });
    let committed = child.value ?? '';

    // Numeric fields select-all on focus so retyping does not need a manual wipe.
    if (NUMERIC.has(child.type)) {
      input.addEventListener('focus', () => input.select());
    }

    const commit = async () => {
      const next = input.value;
      if (next === committed) return;

      const problem = validate(child.type, next);
      if (problem) {
        input.dataset.invalid = 'true';
        input.title = problem;
        return;
      }
      delete input.dataset.invalid;
      input.title = '';

      try {
        const updated = await api.setValue(child.path, next);
        committed = updated.value ?? next;
        input.value = committed;
        input.dataset.dirty = 'true';
        child.value = committed;
        // A green pulse confirms the write without a toast per keystroke.
        replay(input, 'flash-ok');
        this.app.markDirty();
      } catch (error) {
        input.value = committed;             // optimistic edit rolled back
        input.dataset.invalid = 'true';
        replay(input, 'flash-bad');
        toastError(error, 'Could not set that value');
      }
    };

    input.addEventListener('blur', commit);
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); commit().then(() => input.select()); }
      else if (event.key === 'Escape') { event.preventDefault(); input.value = committed; input.blur(); }
      else if (NUMERIC.has(child.type) && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
        event.preventDefault();
        const step = (event.shiftKey ? 10 : 1) * (event.key === 'ArrowUp' ? 1 : -1);
        const current = parseFloat(input.value) || 0;
        input.value = String(current + step);
      }
    });

    return input;
  }

  /* ============================================================
     INSPECTOR PANEL
     ============================================================ */

  /**
   * The distinguishing folder for an archive root, or '' for everything else.
   *
   * Only roots need it -- a node deeper in has a breadcrumb saying which
   * archive it came out of -- and only when a second open archive shares the
   * name, which app.js decides once per file list.
   */
  archiveQualifier(node) {
    if (!node?.path || node.path.includes('/')) return '';
    return this.app.qualifiers?.get(node.path) ?? '';
  }

  renderInspector() {
    clear(this.inspectorHost);
    const node = this.node;
    if (!node) return;
    // Async sections (animation, contact sheet) may resolve after a newer
    // render already cleared the panel; the token lets them notice and bail.
    const token = this._token = (this._token || 0) + 1;
    // Per-render, not per-instance: as a sticky flag this permanently
    // suppressed the contact sheet after the first animation was viewed.
    this.animationShown = false;

    this.inspectorHost.append(el('div', { class: 'insp-hero anim-rise' },
      el('div', { style: 'display:flex;align-items:center;gap:8px' },
        nodeIcon(node, 16),
        el('span', { class: 'type-badge', 'data-type': node.type || node.kind, text: badgeText(node) }),
        node.dirty
          ? el('span', { class: 'dirty-chip', 'data-visible': 'true', style: 'height:22px;font-size:10px', text: 'unsaved' })
          : null),
      el('div', { class: 'insp-name', text: node.name || '(unnamed)' }),
      // Same rule as the child table: decoration under the real name, never a
      // replacement for it. For an archive root the decoration is the folder
      // that tells two same-named archives apart -- this panel is where the
      // rename field and the delete menu are, so "which String.wz is this"
      // has to be answerable without leaving it.
      node.displayName && node.displayName !== node.name
        ? el('div', { style: 'color:var(--text-3);font-size:var(--fs-12)', text: node.displayName })
        : this.archiveQualifier(node)
          ? el('div', { class: 'mono', style: 'color:var(--text-3);font-size:var(--fs-11)' },
              icon('folder', { size: 12 }), ' ', this.archiveQualifier(node))
          : null));

    // --- name ---
    const nameField = el('div', { class: 'field' },
      el('label', { text: 'Name' }),
      el('input', {
        value: node.name || '',
        onchange: async (event) => {
          try {
            const updated = await api.rename(node.path, event.target.value);
            toast(`Renamed to ${updated.name}`, 'success');
            // Refresh the PARENT and follow the node to its new path. Refreshing
            // the old path 404s, turning a rename that worked into an error.
            const parent = node.path.slice(0, node.path.lastIndexOf('/'));
            this.app.afterStructureChange(parent || node.path);
            this.app.navigate(updated.path);
          } catch (error) {
            event.target.value = node.name || '';
            toastError(error, 'Could not rename');
          }
        },
      }));
    this.inspectorHost.append(nameField);

    // --- value ---
    if (node.editable) {
      this.inspectorHost.append(this.buildInspectorValue(node));
    }

    // --- canvas preview ---
    if (node.type === 'Canvas') this.inspectorHost.append(this.buildCanvasSection(node));
    if (node.type === 'Sound') this.inspectorHost.append(this.buildSoundSection(node));
    if (node.type === 'UOL') this.inspectorHost.append(this.buildUolSection(node));

    // --- animation and contact sheet, when there is anything below ---
    //
    // Both are deferred until the selection has been still for EXTRAS_DELAY.
    // /api/animation parses every frame under the node and /api/preview decodes
    // up to 60 canvases, each on the same single gate the tree and the child
    // table are queued behind -- so a node the user is merely passing through
    // on the way down a directory must not start either.
    this.player?.destroy();
    this.player = null;
    clearTimeout(this.extrasTimer);
    if (node.hasChildren) {
      const requested = this.path;
      this.extrasTimer = setTimeout(() => {
        this.extrasTimer = null;
        if (this.path !== requested || this._token !== token) return;

        const player = new AnimationPlayer({ app: this.app });
        player.build(node.path).then((section) => {
          // The user may have navigated away while the frames were loading.
          if (!section || this.path !== requested || this._token !== token) { player.destroy(); return; }
          this.player = player;
          this.inspectorHost.append(section);
          this.animationShown = true;
        }).catch(() => player.destroy());

        // Contact sheet of everything drawable underneath, so you can see what
        // a branch holds without walking down to each canvas.
        this.appendContactSheet(node, requested, token);
      }, EXTRAS_DELAY);
    }

    // --- metadata ---
    const meta = el('dl', { class: 'meta-grid' });
    meta.append(el('dt', { text: 'Kind' }), el('dd', { text: node.kind }));
    if (node.type) meta.append(el('dt', { text: 'Type' }), el('dd', { text: node.type }));
    if (node.childCount >= 0) meta.append(el('dt', { text: 'Children' }), el('dd', { text: fmt.format(node.childCount) }));
    for (const [key, value] of Object.entries(node.extra || {})) {
      if (value === null || value === undefined || value === '') continue;
      meta.append(el('dt', { text: key }), el('dd', { text: String(value) }));
    }
    // The archive's name, not the session handle it happens to have been given.
    //
    // `f1` is assigned in the order files were opened. It is the right string to
    // send to the API and the wrong one to read: the inspector's Path row said
    // "f1/Achievement" while the status bar two inches below said
    // "Etc.wz/Achievement" for the same node, and with two clients open the same
    // archive is f1 in one session and f3 in the next. The handle stays
    // reachable on hover, because it is what an API call or a bug report wants.
    const archive = this.app.files?.find((file) => file.id === String(node.path).split('/')[0]);
    const readable = archive
      ? [archive.name, withoutFileId(node.path)].filter(Boolean).join('/')
      : decodeURIComponent(node.path);
    meta.append(
      el('dt', { text: 'Path' }),
      el('dd', { style: 'word-break:break-all', title: node.path, text: readable }));

    this.inspectorHost.append(
      el('details', { class: 'insp-section', open: true },
        el('summary', { text: 'Details' }), meta));

    // --- actions ---
    // One primary, one secondary, everything else behind the overflow --
    // and never a resting red button.
    this.inspectorHost.append(
      el('div', { style: 'display:flex;gap:8px;margin-top:16px' },
        el('button', {
          class: 'btn btn-primary', style: 'flex:1',
          onclick: () => this.app.promptAddChild(node.path),
        }, icon('plus', { size: 14 }), 'Add child'),
        el('button', { class: 'btn', onclick: () => this.app.duplicate([node.path]) },
          icon('copy', { size: 14 }), 'Duplicate'),
        el('button', {
          class: 'btn btn-icon', 'data-tip': 'More actions',
          onclick: (event) => {
            const rect = event.currentTarget.getBoundingClientRect();
            this.app.showMenu([
              {
                icon: 'star',
                label: this.app.pins?.has(node.path) ? 'Unpin' : 'Pin',
                shortcut: 'Ctrl D',
                run: () => this.app.togglePin(node.path),
              },
              { icon: 'copy', label: 'Copy path', shortcut: 'Alt C', run: () => this.app.copyPath(node.path) },
              node.hasChildren
                ? { icon: 'download', label: 'Export images (ZIP)', run: () => this.app.download(api.exportImagesUrl(node.path)) }
                : null,
              node.hasChildren
                ? { icon: 'download', label: 'Export as JSON', run: () => this.app.download(api.exportJsonUrl(node.path)) }
                : null,
              node.hasChildren
                ? { icon: 'replace', label: 'Find and replace here…', run: () => this.app.openFindReplace(node.path) }
                : null,
              { icon: 'trash', label: 'Delete', shortcut: 'Del', danger: true, run: () => this.app.deleteNodes([node.path]) },
            ], rect.right - 210, rect.bottom + 4);
          },
        }, icon('more', { size: 16 }))));
  }

  buildInspectorValue(node) {
    const wrap = el('div', { class: 'field' });
    const label = el('label', { text: 'Value' });

    const isLongText = node.type === 'String' && (node.value?.length ?? 0) > 60;
    const input = isLongText
      ? el('textarea', { value: node.value ?? '' })
      : el('input', { value: node.value ?? '', spellcheck: 'false' });

    const error = el('div', { class: 'field-error', style: 'display:none' });

    const commit = async () => {
      const problem = validate(node.type, input.value);
      if (problem) {
        error.textContent = problem;
        error.style.display = '';
        return;
      }
      error.style.display = 'none';
      try {
        const updated = await api.setValue(node.path, input.value);
        node.value = updated.value;
        this.app.markDirty();
        this.refreshChildValue(node.path, updated.value);
      } catch (ex) {
        input.value = node.value ?? '';
        toastError(ex, 'Could not set that value');
      }
    };

    input.addEventListener('change', commit);

    // Drag the label left/right to scrub numeric values.
    if (NUMERIC.has(node.type)) {
      label.dataset.scrub = 'true';
      label.title = 'Drag to change  ·  Shift for ×10';
      attachScrub(label, input, node.type, commit);
    }

    wrap.append(label, input, error);
    if (node.type === 'Vector') wrap.append(el('div', { class: 'field-hint', text: 'Format: x, y' }));
    return wrap;
  }

  refreshChildValue(path, value) {
    const input = this.childHost.querySelector(`.cell-input[data-path="${CSS.escape(path)}"]`);
    if (input) { input.value = value ?? ''; input.dataset.dirty = 'true'; }
  }

  /**
   * Shows every canvas found beneath the selected node. Selecting a branch is
   * far more common than selecting the leaf canvas itself, so this is what makes
   * browsing sprites bearable.
   */
  async appendContactSheet(node, requested, token) {
    let data;
    const signal = this.reads?.signal;
    try {
      data = await getJson(`/api/preview?path=${q(node.path)}&limit=60`, signal);
    } catch {
      return;
    }
    if (this.path !== requested || this._token !== token || !data.canvases.length) return;

    // A node whose children are the animation frames already renders them in
    // the player above; repeating them here is noise.
    if (this.animationShown && data.canvases.every((c) => /^\d+$/.test(c.name))) return;

    const grid = el('div', {
      style: 'display:grid;grid-template-columns:repeat(auto-fill,minmax(64px,1fr));gap:6px',
    });

    for (const canvas of data.canvases) {
      const shot = el('img', {
        alt: canvas.name, decoding: 'async', style: 'image-rendering:pixelated',
      });
      // Grow small sprites to fill the tile instead of floating in it.
      shot.addEventListener('load', () => scalePixelArt(shot, 56, 44));
      shot.addEventListener('error', () => shot.remove());
      // Sixty tiles used to be sixty simultaneous requests on the session gate,
      // for a panel showing about eight of them. Through the shared loader they
      // arrive a few at a time, only the visible ones are asked for at all, and
      // navigating away cancels the rest.
      lazySprite(shot, canvasUrl(canvas.path), { signal: this.sprites?.signal });

      grid.append(el('button', {
        class: 'btn thumb',
        'data-path': canvas.path,
        'data-preview-label': canvas.name,
        title: `${decodeURIComponent(canvas.path.split('/').slice(-2).join('/'))}\n${canvas.width}×${canvas.height}`,
        style: 'height:64px;padding:2px;display:grid;place-items:center;position:relative',
        onclick: () => this.app.navigate(canvas.path),
      },
        shot,
        el('span', {
          style: 'position:absolute;bottom:1px;left:0;right:0;font-size:9px;color:var(--text-3);' +
                 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;padding:0 2px',
          text: canvas.name,
        })));
    }

    this.inspectorHost.append(
      el('details', { class: 'insp-section', open: true },
        el('summary', { text: `Images below this node · ${data.count}${data.count >= 60 ? '+' : ''}` }),
        grid));
  }

  buildCanvasSection(node) {
    const size = node.extra?.width
      ? `${node.extra.width} x ${node.extra.height}${node.extra.format ? ' \u00b7 ' + node.extra.format : ''}`
      : '';

    const preview = el('img', { alt: node.name });
    const scaleNote = el('span', { class: 'num' });
    preview.addEventListener('load', () => {
      // The stage is a fixed frame; the sprite grows to fill it.
      const scale = scalePixelArt(preview, 320, 260);
      scaleNote.textContent = scale > 1 ? `  \u00b7  shown at ${scale}x` : '';
    });
    // Revision-stamped rather than `&t=Date.now()`: this used to defeat the
    // cache on every single render, re-downloading a sprite that had not moved.
    preview.src = canvasUrl(node.path);

    const stage = el('div', {
      class: 'preview-stage',
      'data-tip': 'Drop a picture here to replace it',
    }, preview);

    // Dropping an image straight onto the preview is the fastest path there is.
    stage.addEventListener('dragover', (event) => {
      if (!event.dataTransfer?.types.includes('Files')) return;
      event.preventDefault();
      stage.style.outline = '2px dashed var(--coral)';
    });
    stage.addEventListener('dragleave', () => { stage.style.outline = ''; });
    stage.addEventListener('drop', (event) => {
      event.preventDefault();
      stage.style.outline = '';
      const dropped = event.dataTransfer?.files?.[0];
      if (dropped) this.app.replaceCanvasFile(node.path, dropped);
    });

    return el('details', { class: 'insp-section', open: true },
      el('summary', { text: 'Image' }),
      stage,
      size
        ? el('div', { class: 'field-hint num', style: 'text-align:center' },
            el('span', { text: size }), scaleNote)
        : null,
      el('div', { style: 'display:flex;gap:8px;margin-top:10px' },
        el('button', {
          class: 'btn btn-primary btn-sm', style: 'flex:1',
          'data-tip': 'Replace these pixels with a picture from your computer',
          onclick: () => this.app.replaceCanvasFile(node.path),
        }, icon('upload', { size: 14 }), 'Replace canvas image'),
        el('button', {
          class: 'btn btn-sm', 'data-tip': 'Draw on this sprite',
          onclick: () => this.app.openPixelEditor(node),
        }, icon('brush', { size: 14 }), 'Edit'),
        el('a', {
          class: 'btn btn-sm btn-icon', 'data-tip': 'Download as PNG',
          href: canvasUrl(node.path), download: `${node.name}.png`,
        }, icon('download', { size: 14 }))));
  }

  buildSoundSection(node) {
    return el('details', { class: 'insp-section', open: true },
      el('summary', { text: 'Audio' }),
      el('audio', { controls: true, src: api.audioUrl(node.path), style: 'width:100%' }),
      el('a', {
        class: 'btn btn-sm', style: 'margin-top:10px',
        href: api.rawUrl(node.path),
        download: `${node.name}${node.extra?.extension || '.mp3'}`,
        text: 'Export audio',
      }));
  }

  buildUolSection(node) {
    const target = node.extra?.link;
    return el('details', { class: 'insp-section', open: true },
      el('summary', { text: 'Link' }),
      el('div', { class: 'field-hint', style: 'margin-bottom:8px;word-break:break-all', text: target || '(empty)' }),
      el('button', {
        class: 'btn btn-sm', text: 'Follow link',
        onclick: () => this.app.followUol(node),
      }));
  }
}

/* ============================================================
   HELPERS
   ============================================================ */

/** Drops a pooled row's claim on the sprite it was waiting for. */
function releaseRow(row) {
  const image = row.querySelector('.row-thumb img');
  if (image) releaseSprite(image);
}

function badgeText(node) {
  if (node.kind === 'Directory') return 'folder';
  if (node.kind === 'Image') return 'img';
  if (node.kind === 'File') return 'file';
  return (node.type || 'node').replace('SubProperty', 'sub');
}

/** Client-side pre-check; the server validates again and is the authority. */
function validate(type, value) {
  if (!TEXT_EDITABLE.has(type)) return null;

  if (NUMERIC.has(type)) {
    const text = value.trim();
    if (text === '') return null;
    const number = Number(text);
    if (!Number.isFinite(number)) return `"${value}" is not a number.`;
    if (type !== 'Float' && type !== 'Double' && !Number.isInteger(number)) {
      return `${type} values must be whole numbers.`;
    }
    const range = RANGES[type];
    if (range && (number < range[0] || number > range[1])) {
      return `${type} must be between ${fmt.format(range[0])} and ${fmt.format(range[1])}.`;
    }
  }

  if (type === 'Vector') {
    const parts = value.split(/[ ,;]+/).filter(Boolean);
    if (parts.length !== 2 || parts.some((p) => !Number.isInteger(Number(p)))) {
      return 'A vector needs two whole numbers, like "12, -4".';
    }
  }

  // Guards a long-standing WZ writer bug at the compressed-length prefix
  // boundary, where a 127-character unicode string corrupts the image.
  if (type === 'String' && value.length === 127 && /[^\x00-\x7F]/.test(value)) {
    return 'A non-ASCII string of exactly 127 characters can corrupt the image. Add or remove one character.';
  }
  return null;
}

/** Pointer-lock drag on a label to scrub the adjacent numeric input. */
function attachScrub(label, input, type, commit) {
  let dragging = false;
  let accumulated = 0;
  let start = 0;

  label.addEventListener('pointerdown', (event) => {
    dragging = true;
    accumulated = 0;
    start = parseFloat(input.value) || 0;
    label.setPointerCapture(event.pointerId);
    event.preventDefault();
  });

  label.addEventListener('pointermove', (event) => {
    if (!dragging) return;
    const multiplier = event.shiftKey ? 10 : event.altKey ? 0.1 : 1;
    accumulated += event.movementX * multiplier;
    const stepped = start + Math.round(accumulated / 2);   // 2px per step
    const value = (type === 'Float' || type === 'Double') ? stepped : Math.round(stepped);
    input.value = String(value);
  });

  const stop = (event) => {
    if (!dragging) return;
    dragging = false;
    label.releasePointerCapture?.(event.pointerId);
    commit();
  };
  label.addEventListener('pointerup', stop);
  label.addEventListener('pointercancel', stop);
}

/** `iconName` is a key from icons.js, not a glyph. */
export function emptyState(iconName, title, body, action) {
  return el('div', { class: 'empty anim-rise' },
    el('div', { class: 'empty-icon' }, icon(iconName || 'info', { size: 26 })),
    el('div', { class: 'empty-title', text: title }),
    body ? el('div', { class: 'empty-body', text: body }) : null,
    action || null);
}

/** Restarts a CSS animation that may already be on the element. */
export function replay(node, className) {
  node.classList.remove(className);
  void node.offsetWidth;          // forces a reflow so the animation restarts
  node.classList.add(className);
  node.addEventListener('animationend', () => node.classList.remove(className), { once: true });
}

function skeletonTable() {
  const card = el('div', { class: 'card', style: 'padding:8px 0' });
  for (let i = 0; i < 8; i++) {
    card.append(el('div', { class: 'skeleton skeleton-row', style: `width:${45 + ((i * 37) % 40)}%` }));
  }
  return card;
}

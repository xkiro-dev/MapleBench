/**
 * Database mode: search everything the open client names.
 *
 * Built in the same shape as Cash Shop and Mobs mode -- one class, guard()
 * around anything that runs before a menu or dialog, a head / stats / toolbar /
 * grid / pager stack, and honest empty states -- because three modes that do the
 * same kind of work should not feel like three different applications.
 *
 * This is the only read-only mode: nothing here writes, so there is no dirty
 * state, no undo entry and no preview. What it does instead is answer "what is
 * 1302000" and "where does the Blue Sword live", which otherwise means knowing
 * which archive to open and guessing a path.
 */

import { api } from './api.js';
import { el, clear, toastError, fmt, debounce, runOnce, scalePixelArt } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';
import { lazySprite } from './media.js';

const PAGE_SIZE = 60;

/**
 * How many hits to ask the server for. It ranks and interleaves the whole match
 * set before cutting, so this is a ceiling on what is shown, not on what is
 * searched -- and the response says when it bit.
 */
const LIMIT = 200;

const KINDS = [
  ['all', 'All'],
  ['item', 'Items'],
  ['mob', 'Mobs'],
  ['skill', 'Skills'],
  ['npc', 'NPCs'],
  ['map', 'Maps'],
];

/**
 * A glyph per kind. Only items have a reliable inventory icon -- the server
 * sends `icon` for those alone -- and a mob or a map with a broken image where
 * its picture should be reads as a failure rather than as "there is no art for
 * this here".
 */
const KIND_GLYPH = { item: 'image', mob: 'layers', skill: 'star', npc: 'info', map: 'grid4' };
const KIND_LABEL = { item: 'Item', mob: 'Mob', skill: 'Skill', npc: 'NPC', map: 'Map' };

/** The archive each kind is named out of, for the "not in any open archive" case. */
const KIND_ARCHIVE = {
  item: 'Item.wz or Character.wz',
  mob: 'Mob.wz',
  skill: 'Skill.wz',
  npc: 'Npc.wz',
  map: 'Map.wz',
};

const pad = (id, width) => String(id).padStart(width, '0');

/**
 * Where the client keeps a thing of each kind, as a list of guesses to try in
 * order: the .img that would hold it, and the child inside it when the .img is
 * a group rather than one entry.
 *
 * These are conventions, not facts about the archive in front of us -- clients
 * differ, and a private server adds content that follows none of them -- so
 * every guess is checked against the real tree before it is offered. See
 * derivePath().
 */
function candidatesFor(kind, id) {
  switch (kind) {
    // One .img per mob or NPC, at the archive root or one level down. Both the
    // zero-padded and the bare form turn up in the wild.
    case 'mob':
    case 'npc':
      return [{ image: `${pad(id, 7)}.img` }, { image: `${id}.img` }, { image: `${pad(id, 8)}.img` }];

    case 'map':
      return [{ image: `${pad(id, 9)}.img` }];

    // Skills are grouped by the job that owns them: 2001005 lives in 200.img,
    // under skill/2001005.
    case 'skill': {
      const book = Math.floor(id / 10000);
      return [
        { image: `${book}.img`, children: [`skill/${id}`] },
        { image: `${pad(book, 4)}.img`, children: [`skill/${id}`] },
      ];
    }

    case 'item':
      // Equips get one .img each in Character.wz; pets one each in Item.wz;
      // everything else is grouped in hundred-thousands.
      if (Math.floor(id / 1000000) === 1) return [{ image: `${pad(id, 8)}.img` }];
      if (Math.floor(id / 10000) === 500) return [{ image: `${pad(id, 7)}.img` }, { image: `${id}.img` }];
      return [{ image: `${pad(Math.floor(id / 10000), 4)}.img`, children: [pad(id, 8)] }];

    default:
      return [];
  }
}

export class Database {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    this.query = '';
    this.kind = localStorage.getItem('mb.dbKind') || 'all';
    if (!KINDS.some(([value]) => value === this.kind)) this.kind = 'all';

    this.results = [];
    this.truncated = false;
    this.page = 1;

    /** Whether String.wz is open and parsed; until the first answer, unknown. */
    this.available = false;
    this.checked = false;

    /** What stopped the capabilities call, and what stopped the last search. */
    this.error = null;
    this.searchError = null;

    /** The request in the air, so the next keystroke can cancel it. */
    this.searching = null;

    this.chipButtons = new Map();
  }

  /**
   * Runs an action at most once at a time, reporting anything it throws.
   *
   * Same rule as the other two modes. Nothing here writes, but the result menu
   * probes the tree before it opens, so two fast clicks on the same card would
   * otherwise both run the probe and stack two menus on one anchor.
   */
  async guard(key, run) {
    try {
      await runOnce(`db:${key}`, run);
    } catch (error) {
      toastError(error);
    }
  }

  async open() {
    this.render();          // paints the chrome while capabilities are asked
    await this.load();
  }

  async load() {
    try {
      const caps = await api.dbCapabilities();
      this.available = Boolean(caps.available);
      this.error = null;
    } catch (error) {
      this.available = false;
      this.error = error;
    }
    this.checked = true;

    // Results outlive a reload only if they can still be trusted. Closing
    // String.wz mid-session leaves a grid of names read out of an archive that
    // is no longer open, which is exactly the "loaded" flag over dropped data
    // that rule 9 exists to stop.
    if (!this.available) { this.results = []; this.truncated = false; }

    this.render();
    // A reload with a query still in the box answers it again rather than
    // showing results from before the archives changed.
    if (this.query.trim() && this.available) this.runSearch();
  }

  /* ============================================================
     SEARCHING
     ============================================================ */

  /**
   * Runs the current query, cancelling whatever was already in the air.
   *
   * Debouncing thins the requests out but does not order them: a slow answer for
   * "ab" arriving after a quick one for "abc" repainted the grid with results
   * for what the box no longer says. Each run aborts the last, and a late
   * response that is no longer the owner is dropped.
   */
  runSearch() {
    this.searching?.abort();

    const needle = this.query.trim();
    if (!needle) {
      this.searching = null;
      this.results = [];
      this.truncated = false;
      this.searchError = null;
      this.renderResults();
      return;
    }

    const mine = new AbortController();
    this.searching = mine;
    this.searchError = null;
    this.renderResults();          // paints the "Searching…" state

    api.dbSearch(needle, this.kind, LIMIT, { signal: mine.signal })
      .then((data) => {
        if (this.searching !== mine) return;
        this.searching = null;
        this.results = data.results || [];
        this.truncated = Boolean(data.truncated);
        this.page = 1;

        // String.wz can be closed mid-session, which changes what the toolbar is
        // allowed to offer -- so that one repaints the whole mode, not just the
        // results.
        const available = Boolean(data.available);
        if (available !== this.available) { this.available = available; this.render(); return; }
        this.renderResults();
      })
      .catch((error) => {
        if (mine.signal.aborted || this.searching !== mine) return;
        this.searching = null;
        this.results = [];
        this.searchError = error;
        this.renderResults();
      });
  }

  setKind(kind) {
    this.kind = kind;
    localStorage.setItem('mb.dbKind', kind);
    this.page = 1;
    // The kind is a server-side filter -- narrowing it returns more of that kind
    // than the interleaved All view had room for -- so it re-asks rather than
    // filtering what is already on screen.
    for (const [value, button] of this.chipButtons) {
      button.setAttribute('aria-pressed', value === kind ? 'true' : 'false');
    }
    this.runSearch();
  }

  /* ============================================================
     RENDER
     ============================================================ */

  render() {
    clear(this.host);
    this.host.className = 'stage-body db';
    this.chipButtons.clear();

    if (this.error) {
      this.host.append(emptyState(
        'alert', 'Could not reach the name index',
        this.error.message,
        el('div', { class: 'db-empty-actions' },
          el('button', { class: 'btn btn-primary', onclick: () => this.load() },
            icon('refresh', { size: 15 }), 'Try again'))));
      return;
    }

    this.resultHost = el('div');

    this.host.append(
      this.buildHead(),
      this.buildToolbar(),
      this.resultHost);

    this.renderResults();
  }

  /**
   * Names the archive the answers come from.
   *
   * Every name on this screen is read out of the user's own String.wz at the
   * user's own version, and which one that is decides whether an id resolves to
   * the right thing -- so the mode says which file it is reading rather than
   * presenting names as facts about MapleStory.
   */
  buildHead() {
    const sources = this.app.files.filter((file) => /^string/i.test(file.name));
    const subject = sources.length === 1
      ? sources[0].name
      : sources.length > 1
        ? `${sources.length} String archives`
        : 'no String archive';

    return el('div', { class: 'db-panel db-head' },
      el('div', { class: 'db-head-text' },
        el('div', { class: 'db-head-title', text: 'Game Data Search' }),
        el('div', { class: 'db-head-sub' },
          el('span', { text: 'Names from ' }),
          el('b', { text: subject }),
          el('span', { text: this.available ? '' : ' · not readable' }))),
      el('button', {
        class: 'btn btn-icon', 'data-tip': 'Check the open archives again',
        onclick: () => this.load(),
      }, icon('refresh', { size: 15 })));
  }

  /**
   * Writes the per-kind counts onto the filter pills.
   *
   * These used to be a row of six labelled boxes -- RESULTS, ITEMS, MOBS,
   * SKILLS, NPCS, MAPS -- sitting ninety pixels above six filter pills reading
   * All, Items, Mobs, Skills, NPCs, Maps. The same six words twice, and only
   * the lower six could be clicked; before a search the upper six were six
   * empty boxes holding an em dash each. The number belongs on the control
   * that acts on it, which is how the Mobs section has always done it.
   *
   * Counts only in the All view: a kind-filtered search asked the server for
   * that kind alone, so a 0 beside Maps would mean "you did not ask", not
   * "there are none".
   */
  refreshKindCounts() {
    const asked = this.kind === 'all' && Boolean(this.query.trim()) && !this.searching;
    for (const [value, button] of this.chipButtons) {
      const slot = button.querySelector('.cat-count');
      if (!slot) continue;
      const n = !asked ? null
        : value === 'all' ? this.results.length
        : this.results.filter((result) => result.kind === value).length;
      // Blank, not 0: a pill reading "Maps 0" is a count worth reading past,
      // and :empty takes the span out of the pill's layout entirely.
      slot.textContent = n ? fmt.format(n) : '';
    }
  }

  buildToolbar() {
    const search = el('input', {
      type: 'search', value: this.query,
      placeholder: this.available
        ? 'Search by name or id — "Blue Sword", "1302000", "01302"…'
        : 'Open String.wz to search by name',
      'aria-label': 'Search the client',
      disabled: !this.available && this.checked,
    });
    // Only the results are re-rendered, never the toolbar holding this input:
    // rebuilding it destroys the caret and drops IME composition mid-word.
    search.addEventListener('input', debounce(() => {
      this.query = search.value;
      this.page = 1;
      this.runSearch();
    }, 160));
    this.searchInput = search;

    const chips = el('div', { class: 'cat-tabs' });
    for (const [value, label] of KINDS) {
      const button = el('button', {
        class: 'cat-tab', 'aria-pressed': this.kind === value ? 'true' : 'false',
        disabled: !this.available && this.checked,
        onclick: () => this.setKind(value),
      }, label, el('span', { class: 'cat-count' }));
      this.chipButtons.set(value, button);
      chips.append(button);
    }

    return el('div', { class: 'db-panel' },
      el('div', { class: 'db-toolbar' },
        el('div', { class: 'db-search' }, el('span', { class: 'icon' }, icon('search', { size: 15 })), search),
        chips),
      this.truncated
        ? el('div', { class: 'notice', 'data-tone': 'warn' }, icon('alert', { size: 14 }),
            el('span', {
              text: `More than ${fmt.format(LIMIT)} things match this. These are the best of them — ` +
                    'type more of the name, or pick a kind, to see the rest.',
            }))
        : null);
  }

  /** Puts the caret in the search box; used by the palette command. */
  focusSearch() {
    this.searchInput?.focus();
    this.searchInput?.select();
  }

  /** Redraws only the result panel, leaving the search box untouched. */
  renderResults() {
    if (!this.resultHost) { this.render(); return; }
    // Searching per keystroke repaints this grid per keystroke, so the icons
    // the last one asked for are already stale. One scope per repaint; see
    // lazySprite in media.js.
    this.sprites?.abort();
    this.sprites = new AbortController();
    clear(this.resultHost);

    // The counts sit on the filter pills and describe these results, so they
    // move together.
    this.refreshKindCounts();

    const panel = el('div', { class: 'db-panel' });

    if (this.checked && !this.available) {
      panel.append(emptyState(
        'type', 'Open String.wz to search by name',
        'Names come from your client\'s own String.wz. Open String.wz or the whole client folder to '
        + 'enable Game Data Search.',
        el('button', { class: 'btn btn-primary', onclick: () => this.app.openFilePicker() },
          icon('folderOpen', { size: 15 }), 'Open String.wz')));
      this.resultHost.append(panel);
      return;
    }

    if (this.searchError) {
      panel.append(emptyState(
        'alert', 'That search did not finish',
        this.searchError.message,
        el('button', { class: 'btn btn-primary', onclick: () => this.runSearch() },
          icon('refresh', { size: 15 }), 'Try again')));
      this.resultHost.append(panel);
      return;
    }

    if (!this.query.trim()) {
      panel.append(emptyState(
        'search', 'Search the client',
        'Type a name or an id. Everything the client names is here: items, mobs, skills, NPCs and maps, ' +
        'ranked together so the best answer is first.'));
      this.resultHost.append(panel);
      return;
    }

    if (this.searching) {
      panel.append(el('div', { class: 'db-result-head' }, el('span', { text: 'Searching…' })));
      for (let i = 0; i < 6; i++) {
        panel.append(el('div', { class: 'skeleton skeleton-row', style: `width:${45 + ((i * 37) % 40)}%` }));
      }
      this.resultHost.append(panel);
      return;
    }

    const pages = Math.max(1, Math.ceil(this.results.length / PAGE_SIZE));
    this.page = Math.min(this.page, pages);
    const slice = this.results.slice((this.page - 1) * PAGE_SIZE, this.page * PAGE_SIZE);

    panel.append(el('div', { class: 'db-result-head' },
      el('span', {
        text: `${fmt.format(this.results.length)} result${this.results.length === 1 ? '' : 's'}` +
              (this.truncated ? ' (the best ones)' : ''),
      }),
      el('span', { text: `Page ${this.page} of ${pages}` })));

    if (!this.results.length) {
      panel.append(emptyState(
        'search', `Nothing matches "${this.query.trim()}"`,
        this.kind === 'all'
          ? 'Names come from your client, so anything it does not name cannot be found by name. ' +
            'Try an id — leading zeros are fine — or part of a name.'
          : `No ${KINDS.find(([v]) => v === this.kind)?.[1].toLowerCase()} match that. Try All.`,
        this.kind === 'all'
          ? el('button', { class: 'btn', text: 'Clear search',
              onclick: () => { this.query = ''; this.render(); } })
          : el('button', { class: 'btn', text: 'Search all categories',
              onclick: () => this.setKind('all') })));
    } else {
      // Rendered in the order the server sent them. It ranks by score and
      // interleaves the kinds so every pool that matched is in the first
      // screenful; re-sorting here by kind would undo exactly that.
      const grid = el('div', { class: 'db-grid' });
      for (const result of slice) grid.append(this.buildCard(result));
      panel.append(grid);
      if (pages > 1) panel.append(this.buildPager(pages));
    }

    this.resultHost.append(panel);
  }

  buildCard(result) {
    const glyph = KIND_GLYPH[result.kind] ?? 'file';

    const thumb = el('div', { class: 'db-thumb' });
    if (result.icon) {
      // No `loading="lazy"`: the shared loader decides when this is asked for,
      // and two schedulers on one element is one too many.
      const img = el('img', { alt: '', decoding: 'async' });
      // An item whose icon the client cannot supply falls back to the kind
      // glyph, never to a broken image.
      img.addEventListener('error', () => {
        img.remove();
        thumb.append(el('span', { class: 'placeholder' }, icon(glyph, { size: 20 })));
      });
      // Inside the 48px thumb with room to spare: a whole-number upscale of a
      // 32x32 icon is 32px here, and nothing overflows the checkerboard.
      img.addEventListener('load', () => scalePixelArt(img, 44, 44));
      thumb.append(img);
      // Through the shared loader: this grid repaints on every keystroke, and
      // sixty icons per repaint on the session's single gate is what made a
      // search feel slower the more it matched. Only the visible ones are
      // asked for, and the previous keystroke's are cancelled.
      lazySprite(img, result.icon, { signal: this.sprites?.signal });
    } else {
      thumb.append(el('span', { class: 'placeholder' }, icon(glyph, { size: 20 })));
    }

    // A button, so Tab reaches every result and Enter opens one. No data-tip:
    // a tooltip repeating the name already on the card, on all sixty of them,
    // is noise.
    const card = el('button', { class: 'db-card', 'data-kind': result.kind, 'data-id': result.id },
      thumb,
      el('div', { class: 'db-card-text' },
        el('div', { class: 'db-name', text: result.name || `${KIND_LABEL[result.kind] ?? 'Entry'} ${result.id}` }),
        el('div', { class: 'db-id', text: String(result.id) })),
      el('span', { class: 'db-badge', 'data-kind': result.kind, text: KIND_LABEL[result.kind] ?? result.kind }));

    card.addEventListener('click', () => this.openResult(result, card));
    return card;
  }

  buildPager(pages) {
    const pager = el('div', { class: 'pager' });
    // Only the rows change when paging, so the toolbar and its search box are
    // left alone.
    const go = (page) => { this.page = page; this.renderResults(); this.host.scrollTo({ top: 0 }); };

    pager.append(el('button', { text: '‹ Previous', disabled: this.page === 1, onclick: () => go(this.page - 1) }));

    const numbers = new Set([1, pages, this.page, this.page - 1, this.page + 1]);
    let previous = 0;
    for (const page of [...numbers].filter((p) => p >= 1 && p <= pages).sort((a, b) => a - b)) {
      if (page - previous > 1) pager.append(el('span', { text: '…', style: 'color:var(--text-3)' }));
      pager.append(el('button', {
        text: String(page), 'aria-current': page === this.page ? 'true' : 'false', onclick: () => go(page),
      }));
      previous = page;
    }

    pager.append(el('button', { text: 'Next ›', disabled: this.page === pages, onclick: () => go(this.page + 1) }));
    return pager;
  }

  /* ============================================================
     OPENING A RESULT
     ============================================================ */

  // Guarded because the path is probed *before* the menu opens: two fast clicks
  // would otherwise both probe and stack two menus on the same card.
  openResult(result, anchor) {
    return this.guard(`open:${result.kind}:${result.id}`, () => this.showResultMenu(result, anchor));
  }

  async showResultMenu(result, anchor) {
    anchor?.setAttribute('aria-busy', 'true');
    let path = null;
    try {
      path = await this.derivePath(result);
    } finally {
      anchor?.removeAttribute('aria-busy');
    }

    // The card may have been paged or searched away during the probe.
    if (anchor && !anchor.isConnected) return;
    const rect = anchor.getBoundingClientRect();

    const label = result.name || String(result.id);
    this.app.showMenu([
      path
        ? { icon: 'externalLink', label: 'Show in Explorer', run: () => {
            this.app.setMode('explorer');
            this.app.navigate(path);
          } }
        : null,
      result.kind === 'mob' && path
        ? { icon: 'layers', label: 'Open in the Mobs section', run: async () => {
            await this.app.openSection('mobs');
            this.app.mobs.openLinked(String(result.id));
          } }
        : null,
      { icon: 'copy', label: `Copy id ${result.id}`, run: () => this.app.copyText(String(result.id)) },
      result.name ? { icon: 'copy', label: 'Copy name', run: () => this.app.copyText(result.name) } : null,
      // Said as an action rather than as a disabled row: the reason it cannot be
      // opened is always that the archive holding it is not in this session.
      path
        ? null
        : { icon: 'folderOpen', label: `Open ${KIND_ARCHIVE[result.kind] ?? 'the archive'} to open ${label}`,
            run: () => this.app.openFilePicker() },
    ].filter(Boolean), rect.left, rect.bottom + 4);
  }

  /**
   * The Explorer path for a result, or null when there is not one.
   *
   * An id does not say where its node lives: which archive holds it, and whether
   * it is grouped, differs per kind and per client, and a private server adds
   * content that follows no convention at all. So the guesses in candidatesFor()
   * are checked against the real tree -- the name search finds the .img, and the
   * exact child is probed -- and anything unconfirmed resolves to null. A path
   * that turns out not to exist is worse than admitting there is not one.
   */
  async derivePath(result) {
    for (const candidate of candidatesFor(result.kind, result.id)) {
      let found;
      try {
        // Anchored regex, not the plain query. Name search matches on substring
        // and stops at the hit limit, so looking for "100.img" filled all 40
        // slots with 1100.img, 2100.img, 15100.img... and the one exact match
        // never made it into the response at all. Names only, so the walk lists
        // images without parsing them -- about a second over a whole client.
        found = await api.search({
          query: `^${candidate.image.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`,
          matchNames: true, matchValues: false, regex: true, limit: 20,
        });
      } catch {
        return null;          // the search failed; guessing from here would be a lie
      }

      const exact = found.hits || [];
      if (!exact.length) continue;

      for (const hit of exact) {
        for (const child of candidate.children || []) {
          const target = `${hit.path}/${child}`;
          try {
            await api.node(target);
            return target;
          } catch { /* not in this copy of the .img; try the next */ }
        }
      }
      // The .img is there and the entry inside it is not -- land on the .img
      // rather than on nothing, which is still where the answer is.
      return exact[0].path;
    }
    return null;
  }

  /** Right-click actions for the mode; shared with the app-wide context menu. */
  menuItems() {
    return [
      { icon: 'search', label: 'Focus the search box', run: () => this.focusSearch() },
      this.query
        ? { icon: 'close', label: 'Clear the search', run: () => { this.query = ''; this.render(); } }
        : null,
      { icon: 'refresh', label: 'Check the open archives again', run: () => this.load() },
    ].filter(Boolean);
  }
}

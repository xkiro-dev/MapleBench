/**
 * Mobs mode: a card grid over Mob.wz, plus a per-mob field editor and a
 * previewed bulk edit.
 *
 * Built in the same shape as Cash Shop mode -- one class, guard() around every
 * mutation, a toolbar / stats / grid / pager stack, and honest empty states --
 * because two modes that do the same kind of work should not feel like two
 * different applications.
 *
 * Every write goes through /api/mob/fields, which edits the same session the
 * Explorer edits, so mob changes share one dirty state, one undo history and
 * one save pipeline.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt, modal, confirmDialog, debounce, runOnce,
         busyPanel, busyBar, busyWhile } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';
import { thumbUrl, lazySprite } from './media.js';
import { openPortDialog } from './port.js';

const PAGE_SIZE = 60;

const FILTERS = [['all', 'All'], ['bosses', 'Bosses'], ['undead', 'Undead']];

const SORTS = [
  ['id', 'Mob ID'],
  ['name', 'Name (A to Z)'],
  ['level', 'Level (low to high)'],
  ['level-desc', 'Level (high to low)'],
  ['hp-desc', 'Max HP (high to low)'],
  ['exp-desc', 'EXP (high to low)'],
];

const OPS = [
  ['set', 'Set to'],
  ['add', 'Add'],
  ['multiply', 'Multiply by'],
  ['percent', 'Change by percent'],
];

const ROUNDING = [
  ['nearest', 'Round to the nearest whole number'],
  ['floor', 'Round down'],
  ['none', 'Leave the fraction alone'],
];

/**
 * Fields the bulk editor offers when it cannot read a real mob to find out what
 * this Mob.wz actually carries. These are the keys the list endpoint already
 * returns for every mob, so they are the safest possible guess.
 */
const FALLBACK_BULK_FIELDS = [
  ['maxHP', 'Max HP'],
  ['maxMP', 'Max MP'],
  ['exp', 'EXP'],
  ['level', 'Level'],
  ['PADamage', 'Physical attack'],
  ['MADamage', 'Magic attack'],
];

export class Mobs {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    /** null means "every open Mob archive", which is what the API does with no fileId. */
    this.fileId = null;
    this.mobs = [];
    this.stats = null;
    this.truncated = false;
    this.capabilities = { available: false, names: false };
    /** The failure that stopped the last load, so the mode can say so instead of showing an empty grid. */
    this.error = null;

    /**
     * True from the first line of load() until it settles.
     *
     * Without it, render() ran with the constructor's `available: false` and
     * painted "No Mob.wz open" for the ten seconds the list took to build --
     * telling the user their archive was not open while the app was busy
     * reading that exact archive. A wait is acceptable; a wait dressed as a
     * different problem sends people off to fix something that is not broken.
     */
    this.loading = false;

    this.query = '';
    this.filter = 'all';
    this.sort = localStorage.getItem('mb.mobSort') || 'id';
    this.page = 1;

    /** Session paths of the ticked cards; bulk edit acts on exactly this set. */
    this.selection = new Set();
  }

  /**
   * Runs a mutation at most once at a time, reporting anything it throws.
   *
   * Same rule as the cash shop: every write is a POST behind a button that
   * stays live while it is in the air, and a bulk apply issued twice doubles a
   * multiply. The keys are namespaced because the in-flight set is shared with
   * the rest of the app.
   */
  async guard(key, run) {
    try {
      await runOnce(`mob:${key}`, run);
    } catch (error) {
      toastError(error);
    }
  }

  async open(fileId = null) {
    this.fileId = fileId;
    this.page = 1;
    this.render();          // paints whatever we already have while the load runs
    await this.load();
  }

  async load() {
    this.error = null;

    // Set and painted before anything is awaited. Every early return below goes
    // through the finally, so no path can leave the panel spinning forever.
    this.loading = true;
    if (!this.mobs.length) this.render();
    else this.markReloading();

    try {
      // Capabilities first and on its own: it is the one call that distinguishes
      // "no Mob.wz is open" from "the backend is not answering", and those two
      // deserve completely different screens.
      try {
        this.capabilities = await api.mobCapabilities();
      } catch (error) {
        this.capabilities = { available: false, names: false };
        this.error = error;
        return;
      }

      if (!this.capabilities.available) {
        this.mobs = [];
        this.stats = null;
        this.selection.clear();
        return;
      }

      try {
        const data = await api.mobList(this.fileId, this.capabilities.names);
        this.mobs = data.mobs || [];
        this.stats = data.stats || null;
        this.truncated = Boolean(data.truncated);
        // A reload can drop mobs the selection still points at -- scoping to one
        // archive is the common way. Bulk-editing paths that are no longer listed
        // would act on things the user can no longer see.
        this.selection = new Set([...this.selection].filter((path) => this.mobs.some((m) => m.path === path)));
      } catch (error) {
        this.error = error;
        this.mobs = [];
        this.stats = null;
      }
    } finally {
      this.loading = false;
      this.render();
    }
  }

  /**
   * The reload case: a grid is already on screen and is about to be replaced by
   * a newer one.
   *
   * Deliberately not the skeleton panel. Every bulk edit, undo and save ticks
   * the session generation and throws the cached list away, so a reload is
   * common -- and swapping a full grid for placeholder rows each time reads as
   * the app losing its place. The bar says "working" while the numbers you were
   * looking at stay where they are.
   */
  markReloading() {
    const host = this.resultHost;
    if (!host) return;
    this.clearReloading?.();
    this.clearReloading = busyBar(host);
    host.setAttribute('aria-busy', 'true');
  }

  /* ============================================================
     FILTERING
     ============================================================ */

  get filtered() {
    const needle = this.query.trim().toLowerCase();
    const rows = this.mobs.filter((mob) => {
      if (this.filter === 'bosses' && !mob.isBoss) return false;
      if (this.filter === 'undead' && !mob.undead) return false;
      if (!needle) return true;
      return String(mob.mobId).includes(needle)
          || (mob.name || '').toLowerCase().includes(needle);
    });
    return this.order(rows);
  }

  order(rows) {
    const sorted = [...rows];
    const num = (value) => (typeof value === 'number' ? value : 0);
    switch (this.sort) {
      case 'name':
        // Mobs without a name string sort last rather than clumping at the top
        // under an empty label, which reads as a bug.
        sorted.sort((a, b) => {
          if (!a.name && !b.name) return a.mobId - b.mobId;
          if (!a.name) return 1;
          if (!b.name) return -1;
          return a.name.localeCompare(b.name) || a.mobId - b.mobId;
        });
        break;
      case 'level': sorted.sort((a, b) => num(a.level) - num(b.level) || a.mobId - b.mobId); break;
      case 'level-desc': sorted.sort((a, b) => num(b.level) - num(a.level) || a.mobId - b.mobId); break;
      case 'hp-desc': sorted.sort((a, b) => num(b.maxHP) - num(a.maxHP) || a.mobId - b.mobId); break;
      case 'exp-desc': sorted.sort((a, b) => num(b.exp) - num(a.exp) || a.mobId - b.mobId); break;
      default: sorted.sort((a, b) => a.mobId - b.mobId); break;
    }
    return sorted;
  }

  /* ============================================================
     RENDER
     ============================================================ */

  render() {
    // A repaint replaces resultHost, so the bar and the aria-busy flag on the
    // old one go with it; dropping the remover keeps markReloading honest.
    this.clearReloading = null;
    clear(this.host);
    this.host.className = 'stage-body mobs';

    if (this.error) {
      this.host.append(emptyState(
        'alert', 'Could not read the mob list',
        this.error.message,
        el('div', { class: 'mob-empty-actions' },
          el('button', { class: 'btn btn-primary', onclick: () => this.load() },
            icon('refresh', { size: 15 }), 'Try again'),
          el('button', { class: 'btn', onclick: () => this.app.openFilePicker() },
            icon('folderOpen', { size: 15 }), 'Open a file'))));
      return;
    }

    // Before the first answer we do not know whether Mob.wz is open, so the
    // "open Mob.wz" screen below would be a guess -- and for ten seconds it was
    // a wrong one. The panel is the honest thing to show until the list lands.
    if (this.loading && !this.mobs.length) {
      this.host.append(busyPanel({
        title: 'Reading the mobs…',
        note: 'A full Mob.wz is 2,742 images and takes about ten seconds the first time. '
            + 'After that it is instant until something changes.',
        className: 'mob-panel',
      }));
      return;
    }

    if (!this.capabilities.available) {
      this.host.append(emptyState(
        'layers', 'No Mob.wz open',
        'Mob editing reads the .img files inside Mob.wz. Open Mob.wz or the whole client folder.',
        el('button', { class: 'btn btn-primary', onclick: () => this.app.openFilePicker() },
          icon('folderOpen', { size: 15 }), 'Open Mob.wz')));
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
   * Names the archive being edited.
   *
   * The list endpoint happily spans every open Mob*.wz, so without this the
   * header would be a page of numbers with no clue which file they will be
   * written back into.
   */
  buildHead() {
    const candidates = this.app.files.filter((file) => /mob/i.test(file.name));
    const scoped = this.fileId ? this.app.files.find((f) => f.id === this.fileId) : null;

    const subject = scoped
      ? scoped.name
      : candidates.length === 1
        ? candidates[0].name
        : candidates.length > 1
          ? `${candidates.length} Mob archives`
          : 'every open archive';

    const head = el('div', { class: 'mob-panel mob-head' },
      el('div', { class: 'mob-head-text' },
        el('div', { class: 'mob-head-title', text: 'Mobs' }),
        el('div', { class: 'mob-head-sub' },
          el('span', { text: 'Editing ' }),
          el('b', { text: subject }),
          // The level range used to be a fourth stat tile. It is the only one
          // of the four the filter pills below do not already carry, and it is
          // one short phrase, so it joins the sentence that was already saying
          // what is open and how much of it there is.
          el('span', {
            text: this.stats
              ? ` · ${fmt.format(this.stats.total)} mob${this.stats.total === 1 ? '' : 's'}`
                + (this.stats.total
                    ? ` · levels ${fmt.format(this.stats.minLevel)}–${fmt.format(this.stats.maxLevel)}`
                    : '')
              : '',
          }))));

    // The picker only earns its place when there is a real choice to make.
    if (candidates.length > 1) {
      const select = el('select', { class: 'mob-select', 'aria-label': 'Which archive to edit' },
        el('option', { value: '', text: `All Mob archives (${candidates.length})`, selected: !this.fileId }),
        candidates.map((file) => el('option', { value: file.id, text: file.name, selected: file.id === this.fileId })));
      select.addEventListener('change', () => {
        this.selection.clear();
        this.open(select.value || null);
      });
      head.append(select);
    }

    head.append(el('button', {
      class: 'btn btn-icon', 'data-tip': 'Reload the mob list',
      onclick: () => this.load(),
    }, icon('refresh', { size: 15 })));

    return head;
  }

  /* There were four stat tiles here -- TOTAL MOBS 6,000, BOSSES 2,734, UNDEAD
     232, LEVEL RANGE 1–300 -- sitting ninety pixels above three filter pills
     reading All 6,000, Bosses 2,734, Undead 232. Three of the four numbers
     appeared twice on one screen, and only the lower copy could be clicked;
     the fourth is now in the header sentence. */

  buildToolbar() {
    const search = el('input', {
      type: 'search', value: this.query,
      placeholder: 'Search by mob ID or name…',
      'aria-label': 'Search mobs',
    });
    // Only the results are re-rendered, never the toolbar holding this input:
    // rebuilding it destroys the caret and drops IME composition mid-word.
    search.addEventListener('input', debounce(() => {
      this.query = search.value;
      this.page = 1;
      this.renderResults();
    }, 160));

    const chips = el('div', { class: 'cat-tabs' },
      FILTERS.map(([value, label]) => el('button', {
        class: 'cat-tab', 'aria-pressed': this.filter === value ? 'true' : 'false',
        onclick: () => { this.filter = value; this.page = 1; this.render(); },
      }, label, el('span', { class: 'cat-count', text: fmt.format(this.countFor(value)) }))));

    const sort = el('select', { class: 'mob-select', 'aria-label': 'Sort mobs' },
      SORTS.map(([value, label]) => el('option', { value, text: label, selected: value === this.sort })));
    sort.addEventListener('change', () => {
      this.sort = sort.value;
      localStorage.setItem('mb.mobSort', sort.value);
      this.page = 1;
      this.renderResults();
    });

    return el('div', { class: 'mob-panel' },
      el('div', { class: 'mob-toolbar' },
        el('div', { class: 'mob-search' }, el('span', { class: 'icon' }, icon('search', { size: 15 })), search),
        chips,
        sort),
      this.buildSelectionBar(),
      this.truncated
        ? el('div', { class: 'notice', 'data-tone': 'warn' }, icon('alert', { size: 14 }),
            el('span', { text: 'The archive holds more mobs than one listing returns, so this is a partial list. ' +
                               'Scope to a single archive, or search, to be sure you are seeing everything.' }))
        : null);
  }

  countFor(filter) {
    if (filter === 'bosses') return this.mobs.filter((m) => m.isBoss).length;
    if (filter === 'undead') return this.mobs.filter((m) => m.undead).length;
    return this.mobs.length;
  }

  buildSelectionBar() {
    const shown = this.filtered;
    const count = this.selection.size;

    return el('div', { class: 'mob-selbar', 'data-active': count ? 'true' : 'false' },
      el('span', { class: 'mob-selcount', text: count ? `${fmt.format(count)} selected` : 'Nothing selected' }),
      el('button', {
        class: 'btn btn-sm', disabled: !shown.length,
        onclick: () => {
          for (const mob of shown) this.selection.add(mob.path);
          this.render();
        },
      }, icon('check', { size: 14 }), `Select all ${fmt.format(shown.length)}`),
      el('button', {
        class: 'btn btn-sm', disabled: !count,
        onclick: () => { this.selection.clear(); this.render(); },
      }, icon('close', { size: 14 }), 'Clear'),
      // The one-click port. Beside Bulk edit rather than in the overflow menu
      // because it acts on exactly the same selection and is reached the same
      // way -- tick the cards, press the button. It opens a preview, never a
      // write, so it is safe to have next to a destructive-sounding one.
      el('button', {
        class: 'btn btn-sm', disabled: !count,
        'data-tip': 'Copy these mobs into another open client, with their names, sounds and linked art',
        onclick: () => this.openPortDialog(),
      }, icon('externalLink', { size: 14 }), count ? `Port ${fmt.format(count)}` : 'Port'),
      el('button', {
        class: 'btn btn-sm btn-primary', disabled: !count,
        onclick: () => this.openBulkDialog(),
      }, icon('sliders', { size: 14 }), count ? `Bulk edit ${fmt.format(count)}` : 'Bulk edit'));
  }

  /** Redraws only the result panel, leaving the search box untouched. */
  renderResults() {
    if (!this.resultHost) { this.render(); return; }
    // Every card about to be thrown away may still have a sprite queued, and it
    // belongs to a page nobody is looking at any more. One scope per repaint;
    // see lazySprite in media.js for what it cancels.
    this.sprites?.abort();
    this.sprites = new AbortController();
    clear(this.resultHost);

    const results = this.filtered;
    const pages = Math.max(1, Math.ceil(results.length / PAGE_SIZE));
    this.page = Math.min(this.page, pages);
    const slice = results.slice((this.page - 1) * PAGE_SIZE, this.page * PAGE_SIZE);

    const panel = el('div', { class: 'mob-panel' });
    panel.append(el('div', { class: 'mob-result-head' },
      el('span', { text: `${fmt.format(results.length)} mob${results.length === 1 ? '' : 's'}` +
                         (this.filter === 'bosses' ? ' · bosses' : this.filter === 'undead' ? ' · undead' : '') }),
      el('span', { text: `Page ${this.page} of ${pages}` })));

    if (!this.mobs.length) {
      // The archive is open and readable and holds nothing -- a different fact
      // from "your filter hid everything", and it needs a different sentence.
      panel.append(emptyState(
        'layers', 'No mobs in this archive',
        'The archive opened, but nothing in it looks like a mob image. If you scoped to one file, ' +
        'try the others.',
        el('button', { class: 'btn', onclick: () => this.load() }, icon('refresh', { size: 15 }), 'Reload')));
    } else if (!results.length) {
      panel.append(emptyState(
        'search',
        this.query ? `No mobs match "${this.query}"` : 'Nothing in this filter',
        this.query
          ? (this.capabilities.names
              ? 'Try a mob ID like 100100, or part of a name.'
              : 'String.wz is not open, so mobs have no names here — search by ID instead.')
          : 'Every mob in this archive is filtered out. Switch back to All.',
        this.query
          ? el('button', { class: 'btn', text: 'Clear search', onclick: () => { this.query = ''; this.render(); } })
          : el('button', { class: 'btn', text: 'Show all mobs', onclick: () => { this.filter = 'all'; this.render(); } })));
    } else {
      const grid = el('div', { class: 'mob-grid' });
      for (const mob of slice) grid.append(this.buildCard(mob));
      panel.append(grid);
      if (pages > 1) panel.append(this.buildPager(pages));
    }

    this.resultHost.append(panel);
  }

  /**
   * The mob's own sprite, straight out of the open archive.
   *
   * /api/thumb walks down to the first canvas under the .img and follows
   * _inlink/_outlink, so the frame layout does not have to be known here -- and
   * a linked mob shows the art it actually renders with rather than its 1x1
   * placeholder. A 204 means there is nothing to draw, which is ordinary, so it
   * falls back to the kind glyph instead of leaving a broken image.
   *
   * Requested through the shared loader: 2,742 rows at a measured 4.6-24.4 ms
   * each, all on the session's single gate, is a scroll pass that blocks
   * everything else in the app. That also means the <img> has to live inside
   * this box from the start rather than being put there on load -- the loader
   * watches the frame around the picture to decide when the row is worth
   * requesting, and an <img> that is not in it yet has no frame.
   */
  buildSprite(mob) {
    const glyph = icon('layers', { size: 16 });
    const box = el('div', { class: 'mob-sprite' }, glyph);

    const img = el('img', {
      alt: '', decoding: 'async',
      class: 'mob-sprite-img', 'data-ready': 'false',
      // Inline rather than in mob.css: it is the loader's business, not the
      // card's, and it must be gone the instant the sprite arrives.
      style: 'display:none',
    });
    // Listener before src: a cached image can fire load synchronously, so the
    // opposite order may silently fail to render.
    img.addEventListener('load', () => {
      if (!img.naturalWidth) return;
      glyph.remove();
      img.style.display = '';
      img.dataset.ready = 'true';
    });
    img.addEventListener('error', () => { /* keep the glyph */ });
    box.append(img);
    // media.js's thumbUrl, not api.thumbUrl: the response is cached for an
    // hour, so without the revision stamp a replaced sprite kept showing the
    // old bitmap here long after the Explorer had moved on.
    lazySprite(img, thumbUrl(mob.path), { signal: this.sprites?.signal });

    return box;
  }

  buildCard(mob) {
    const selected = this.selection.has(mob.path);
    const card = el('div', {
      class: 'mob-card', 'data-path': mob.path, 'data-mob-id': mob.mobId,
      'data-selected': selected ? 'true' : 'false',
    });

    const box = el('input', {
      type: 'checkbox', checked: selected,
      'aria-label': `Select ${mob.name || `mob ${mob.mobId}`}`,
    });
    box.addEventListener('change', () => {
      if (box.checked) this.selection.add(mob.path); else this.selection.delete(mob.path);
      card.dataset.selected = box.checked ? 'true' : 'false';
      // Only the counter and the bulk button change, so the grid is left alone
      // -- repainting it here would throw away the checkbox that was clicked.
      this.refreshSelectionBar();
    });

    const badges = el('div', { class: 'mob-badges' },
      mob.isBoss ? el('span', { class: 'mob-badge', 'data-kind': 'boss', text: 'Boss' }) : null,
      mob.undead ? el('span', { class: 'mob-badge', 'data-kind': 'undead', text: 'Undead' }) : null,
      mob.linkTarget ? el('span', { class: 'mob-badge', 'data-kind': 'link', text: 'Linked' }) : null,
      mob.dirty ? el('span', { class: 'mob-badge', 'data-kind': 'dirty', text: 'Unsaved' }) : null);

    const stat = (key, value) => el('div', { class: 'mob-row' },
      el('span', { class: 'k', text: key }),
      el('span', { class: 'v', text: value }));

    // Filtered, because this is Element.append rather than el(): append(null)
    // does not skip the child, it stringifies it and puts the word "null" on
    // the card.
    card.append(...[
      el('div', { class: 'mob-card-head' },
        el('label', { class: 'mob-pick' }, box),
        this.buildSprite(mob),
        el('div', { class: 'mob-card-title' },
          el('button', {
            class: 'mob-title', text: mob.name || `Mob ${mob.mobId}`,
            'data-tip': 'Open this mob', onclick: (event) => this.openMobCard(mob, event.currentTarget),
          }),
          el('div', { class: 'mob-sub', text: `ID ${mob.mobId}` })),
        el('span', { class: 'mob-level', 'data-tip': 'Level', text: `Lv ${fmt.format(mob.level ?? 0)}` })),
      badges.childElementCount ? badges : null,
      stat('HP', fmt.format(mob.maxHP ?? 0)),
      // Only when it has one. Nearly every mob in a client has no MP at all, so
      // this row was a labelled zero on card after card -- and a card is read by
      // running the eye down four rows, which means four rows is the budget and
      // one of them was reliably worthless. A mob that does have MP now stands
      // out by having the row at all.
      mob.maxMP ? stat('MP', fmt.format(mob.maxMP)) : null,
      stat('EXP', fmt.format(mob.exp ?? 0)),
      stat('Attack', `${fmt.format(mob.padamage ?? 0)} phys · ${fmt.format(mob.madamage ?? 0)} magic`),
      mob.elemAttr ? stat('Elements', mob.elemAttr) : null,
      el('div', { class: 'mob-actions' },
        el('button', { class: 'btn btn-sm', style: 'flex:1',
                       onclick: (event) => this.openMobCard(mob, event.currentTarget) },
          icon('edit', { size: 14 }), 'Edit stats'),
        el('button', {
          class: 'btn btn-sm btn-icon', 'data-tip': 'More',
          onclick: (event) => {
            event.stopPropagation();
            const rect = event.currentTarget.getBoundingClientRect();
            this.app.showMenu(this.menuItemsFor(mob), rect.left, rect.bottom + 4);
          },
        }, icon('more', { size: 15 }))),
    ].filter(Boolean));

    return card;
  }

  /** Shared by the card overflow and the app-wide right-click menu. */
  menuItemsFor(mob) {
    return [
      { icon: 'edit', label: 'Edit stats…', run: () => this.openMobCard(mob) },
      { icon: 'copy', label: `Copy mob ID ${mob.mobId}`, run: () => this.app.copyText(String(mob.mobId)) },
      mob.linkTarget
        ? { icon: 'externalLink', label: `Open the linked mob ${mob.linkTarget}`, run: () => this.openLinked(mob.linkTarget) }
        : null,
      { icon: 'externalLink', label: 'Show in Explorer', run: () => {
        this.app.setMode('explorer');
        this.app.navigate(mob.path);
      } },
    ].filter(Boolean);
  }

  /** Repaints the selection bar in place; see buildCard for why not the grid. */
  refreshSelectionBar() {
    const current = this.host.querySelector('.mob-selbar');
    if (current) current.replaceWith(this.buildSelectionBar());
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

  /** Jumps to the mob a link points at, if it is in the open archives. */
  openLinked(linkTarget) {
    const target = this.mobs.find((m) => m.path.endsWith(`/${linkTarget}.img`))
      ?? this.mobs.find((m) => m.mobId === Number(linkTarget));
    if (!target) {
      toast(`Mob ${linkTarget} is not in the archives you have open, so it cannot be opened here.`, 'warning');
      return;
    }
    this.openMobCard(target);
  }

  /* ============================================================
     MOB CARD
     ============================================================ */

  // Guarded because the detail is fetched *before* modal() runs: two fast
  // clicks would otherwise race, and the second card's commit closures would
  // point at a form the first one had already replaced.
  /**
   * @param trigger the element that was clicked, if any. It is marked busy for
   * the length of the fetch, because the card is only opened once the detail
   * arrives — so until then a click produced nothing at all on screen, and
   * guard() silently drops the second one. Two clicks, no feedback, then a
   * dialog: it reads as the app ignoring you.
   */
  openMobCard(mob, trigger = null) {
    return this.guard(`card:${mob.path}`,
      () => busyWhile(trigger, this.buildMobCard(mob)));
  }

  async buildMobCard(mob) {
    let detail;
    try {
      detail = await api.mobDetail(mob.path);
    } catch (error) {
      toastError(error, 'Could not read that mob');
      return;
    }

    /**
     * `baseline` is what each field held when this card was opened, which is
     * what "(was X)" reports. It is deliberately not the on-disk value: the
     * server does not send one, and inventing it would put a number beside the
     * field that no one can verify.
     */
    const ctx = {
      mob,
      detail,
      baseline: new Map(),
      search: '',
      modifiedOnly: false,
      showAbsent: false,
      closed: new Set(),
      touched: false,
      focusKey: null,
    };
    for (const group of detail.groups || []) {
      for (const field of group.fields || []) ctx.baseline.set(field.key, field.value ?? '');
    }

    // The head and the link strip are repainted with the sections: editing
    // `level` changes the pill in the header, and editing `link` changes
    // whether the warning strip applies at all.
    const headHost = el('div', { class: 'mob-detail-topline' });
    const sectionsHost = el('div', { class: 'mob-sections' });
    const counter = el('span', { class: 'mob-modcount' });

    /* --- the parts that must survive a repaint --- */
    const search = el('input', {
      type: 'search', placeholder: 'Find a field by name…', 'aria-label': 'Find a field',
    });
    search.addEventListener('input', debounce(() => {
      ctx.search = search.value;
      paint();
    }, 120));

    const modifiedToggle = el('button', {
      class: 'btn btn-sm', 'aria-pressed': 'false',
      onclick: () => {
        ctx.modifiedOnly = !ctx.modifiedOnly;
        modifiedToggle.setAttribute('aria-pressed', ctx.modifiedOnly ? 'true' : 'false');
        paint();
      },
    }, icon('filter', { size: 14 }), 'Modified only');

    const absentToggle = el('button', {
      class: 'btn btn-sm', 'aria-pressed': 'false',
      'data-tip': 'Fields this mob does not have. Writing one creates it.',
      onclick: () => {
        ctx.showAbsent = !ctx.showAbsent;
        absentToggle.setAttribute('aria-pressed', ctx.showAbsent ? 'true' : 'false');
        paint();
      },
    }, icon('plus', { size: 14 }), 'Show absent fields');

    const paint = () => {
      clear(headHost);
      headHost.append(this.buildDetailHead(ctx));
      if (ctx.detail.linkTarget) headHost.append(this.buildLinkStrip(ctx));

      clear(sectionsHost);

      let shown = 0;
      let modified = 0;
      for (const group of ctx.detail.groups || []) {
        const section = this.buildSection(ctx, group, commit);
        modified += section.modified;
        if (!section.node) continue;
        shown += section.shown;
        sectionsHost.append(section.node);
      }

      const extra = this.buildExtraSection(ctx);
      if (extra) sectionsHost.append(extra);

      if (!shown) {
        sectionsHost.append(emptyState(
          'search',
          ctx.search ? `No field matches "${ctx.search}"` : 'Nothing to show',
          ctx.modifiedOnly
            ? 'Nothing on this mob has been changed yet. Turn "Modified only" back off.'
            : 'The search looks at both the label and the raw WZ key.'));
      }

      counter.textContent = modified
        ? `${modified} field${modified === 1 ? '' : 's'} changed in this sitting`
        : '';
      counter.dataset.active = modified ? 'true' : 'false';

      // A commit repaints the whole body, which would otherwise pull the caret
      // out of the field the user pressed Enter in.
      if (ctx.focusKey) {
        const back = sectionsHost.querySelector(`[data-field-key="${CSS.escape(ctx.focusKey)}"] input`);
        ctx.focusKey = null;
        if (back && back.type !== 'checkbox') { back.focus(); back.select?.(); }
      }
    };

    const commit = (field, value, revert) => this.commitField(ctx, field, value, revert, paint);

    const body = el('div', { class: 'mob-detail' },
      headHost,
      el('div', { class: 'mob-detail-tools' },
        el('div', { class: 'mob-search' }, el('span', { class: 'icon' }, icon('search', { size: 15 })), search),
        modifiedToggle,
        absentToggle,
        counter),
      sectionsHost);

    paint();

    const { dialog, close } = modal({
      title: ctx.mob.name || `Mob ${ctx.mob.mobId}`,
      subtitle: ctx.mob.path.split('/').slice(1).join('/'),
      width: 'min(96vw, 880px)',
      body,
      actions: [{ label: 'Done' }],
    });
    // Handed to the head and the link strip, both of which are painted before
    // the dialog that owns them exists.
    ctx.close = close;

    // Committed fields change the numbers on the card behind this dialog, and
    // the list is the only thing that knows them. Reloading once on close beats
    // a reload per keystroke.
    dialog.addEventListener('close', () => { if (ctx.touched) this.load(); }, { once: true });
  }

  buildDetailHead(ctx) {
    const mob = ctx.mob;
    return el('div', { class: 'mob-detail-head' },
      el('div', {},
        el('div', { class: 'mob-detail-name', text: mob.name || `Mob ${mob.mobId}` }),
        el('div', { class: 'mob-sub', text: `ID ${mob.mobId}` })),
      el('span', { class: 'mob-level', text: `Lv ${fmt.format(mob.level ?? 0)}` }),
      mob.isBoss ? el('span', { class: 'mob-badge', 'data-kind': 'boss', text: 'Boss' }) : null,
      mob.undead ? el('span', { class: 'mob-badge', 'data-kind': 'undead', text: 'Undead' }) : null,
      el('span', { style: 'margin-left:auto' }),
      el('button', {
        class: 'btn btn-sm', 'data-tip': 'Open this .img in the Explorer',
        // Closing first: leaving the card sitting over the Explorer hides the
        // node it just navigated to, which reads as the button doing nothing.
        onclick: () => {
          ctx.close?.();
          this.app.setMode('explorer');
          this.app.navigate(mob.path);
        },
      }, icon('externalLink', { size: 14 }), 'Show in Explorer'));
  }

  /**
   * The trap this editor exists to stop.
   *
   * A linked mob keeps its stats in another image, so everything below is a
   * copy the game never reads. Editing it looks like it worked -- the write
   * succeeds, the field turns amber -- and nothing changes in-game.
   */
  buildLinkStrip(ctx) {
    const target = ctx.detail.linkTarget;
    return el('div', { class: 'mob-link-strip' },
      icon('alert', { size: 16 }),
      el('div', { class: 'mob-link-text' },
        el('b', { text: `This mob links to ${target}.` }),
        el('span', {
          text: ' Its real stats live in that image, so edits made here are written but will not ' +
                'change anything in-game. Edit the target instead.',
        })),
      el('button', {
        class: 'btn btn-sm',
        // One mob card at a time: stacking the target over the mob that links
        // to it makes it very easy to lose track of which one you are editing.
        onclick: () => { ctx.close?.(); this.openLinked(target); },
      }, icon('externalLink', { size: 14 }), `Open ${target}`));
  }

  /**
   * One collapsible section per API group.
   *
   * Returns the node plus its counts: `modified` is counted over the whole
   * group so the total in the header stays honest while a filter is on, and
   * `shown` is what survived the filters.
   */
  buildSection(ctx, group, commit) {
    const fields = group.fields || [];
    const modified = fields.filter((f) => this.isChanged(ctx, f)).length;

    const visible = fields.filter((field) => {
      if (!field.present && !ctx.showAbsent) return false;
      if (ctx.modifiedOnly && !this.isChanged(ctx, field)) return false;
      if (!ctx.search) return true;
      const needle = ctx.search.trim().toLowerCase();
      // The label as well as the raw key: nobody looking for "Knockback
      // resistance" knows it is stored as "pushed".
      return (field.label || '').toLowerCase().includes(needle)
          || (field.key || '').toLowerCase().includes(needle);
    });

    if (!visible.length) return { node: null, shown: 0, modified };

    const open = !ctx.closed.has(group.group);
    const details = el('details', { class: 'mob-section', open });
    details.addEventListener('toggle', () => {
      if (details.open) ctx.closed.delete(group.group);
      else ctx.closed.add(group.group);
    });

    details.append(el('summary', {},
      el('span', { class: 'mob-section-name', text: group.group }),
      el('span', { class: 'mob-section-meta',
        text: `${visible.length} field${visible.length === 1 ? '' : 's'}` +
              (visible.length !== fields.length ? ` of ${fields.length}` : '') }),
      modified
        ? el('span', { class: 'mob-section-mod', text: `${modified} modified` })
        : null));

    const grid = el('div', { class: 'mob-field-grid' });
    for (const field of visible) grid.append(this.buildField(ctx, field, commit));
    details.append(grid);

    return { node: details, shown: visible.length, modified };
  }

  isChanged(ctx, field) {
    if (!ctx.baseline.has(field.key)) return false;
    return (field.value ?? '') !== ctx.baseline.get(field.key);
  }

  buildField(ctx, field, commit) {
    const current = field.value ?? '';
    const changed = this.isChanged(ctx, field);
    const was = ctx.baseline.get(field.key);

    const row = el('div', {
      class: 'mob-field',
      'data-field-key': field.key,
      'data-absent': field.present ? null : 'true',
      'data-changed': changed ? 'true' : null,
    });

    row.append(el('div', { class: 'mob-field-label' },
      el('span', { class: 'mob-field-name', text: field.label || field.key }),
      field.unit ? el('span', { class: 'mob-unit', text: field.unit }) : null,
      // "(was X)" inline, so a changed value carries its own history and does
      // not need a diff view to be readable.
      changed ? el('span', { class: 'mob-was', text: `was ${was === '' ? 'empty' : was}` }) : null,
      !field.present ? el('span', { class: 'mob-absent-tag', text: 'not set' }) : null));

    if (field.kind === 'Flag') {
      const box = el('input', { type: 'checkbox', checked: current === '1' });
      box.addEventListener('change', () => {
        const next = box.checked ? '1' : '0';
        commit(field, next, () => { box.checked = current === '1'; });
      });
      row.append(el('label', { class: 'mob-check' }, box,
        el('span', { text: current === '1' ? 'On' : 'Off' })));
    } else {
      const input = el('input', {
        class: 'mob-input', value: current, spellcheck: 'false',
        inputmode: field.kind === 'Int' ? 'numeric' : undefined,
        'aria-label': field.label || field.key,
      });
      let committed = current;

      if (field.kind === 'Int') input.addEventListener('focus', () => input.select());

      const send = () => {
        const next = input.value;
        if (next === committed) return;

        if (field.kind === 'Int' && next.trim() !== '' && !Number.isInteger(Number(next))) {
          input.dataset.invalid = 'true';
          input.title = `${field.label || field.key} has to be a whole number.`;
          return;
        }
        delete input.dataset.invalid;
        input.title = '';
        ctx.focusKey = field.key;
        commit(field, next, () => { input.value = committed; });
      };

      input.addEventListener('blur', send);
      input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') { event.preventDefault(); send(); }
        else if (event.key === 'Escape') { event.preventDefault(); input.value = committed; input.blur(); }
      });

      row.append(input);
    }

    if (field.hint) row.append(el('div', { class: 'mob-field-hint', text: field.hint }));
    if (!field.present) {
      row.append(el('div', { class: 'mob-field-hint',
        text: 'This mob does not have this field. Setting a value creates it.' }));
    }
    return row;
  }

  /**
   * Keys the server did not model. They are shown read-only rather than hidden:
   * a mob carrying an unexpected key is worth knowing about, and pretending it
   * is not there is how an editor quietly loses data.
   */
  buildExtraSection(ctx) {
    const entries = Object.entries(ctx.detail.extra || {});
    if (!entries.length) return null;
    if (ctx.modifiedOnly) return null;

    const list = el('dl', { class: 'mob-extra' });
    for (const [key, value] of entries) {
      list.append(el('dt', { text: key }), el('dd', { text: String(value ?? '') }));
    }

    return el('details', { class: 'mob-section' },
      el('summary', {},
        el('span', { class: 'mob-section-name', text: 'Other keys' }),
        el('span', { class: 'mob-section-meta', text: `${entries.length} not editable here` })),
      list);
  }

  /**
   * Folds a fresh detail back into the list row it came from.
   *
   * The header pill, the badges and the card behind the dialog all read the
   * list DTO, and only the detail knows what was just written -- so without
   * this, setting level to 200 left "Lv 5" on screen until the next reload.
   * Keys are matched case-insensitively because the list DTO and the WZ nodes
   * do not agree on capitalisation (padamage vs PADamage).
   */
  syncSummary(ctx) {
    const values = new Map();
    for (const group of ctx.detail.groups || []) {
      for (const field of group.fields || []) {
        if (field.present) values.set((field.key || '').toLowerCase(), field.value);
      }
    }

    const number = (key, fallback) => {
      const parsed = Number(values.get(key));
      return values.has(key) && Number.isFinite(parsed) ? parsed : fallback;
    };

    const mob = ctx.mob;
    mob.level = number('level', mob.level);
    mob.maxHP = number('maxhp', mob.maxHP);
    mob.maxMP = number('maxmp', mob.maxMP);
    mob.exp = number('exp', mob.exp);
    mob.padamage = number('padamage', mob.padamage);
    mob.madamage = number('madamage', mob.madamage);
    if (values.has('boss')) mob.isBoss = values.get('boss') === '1';
    if (values.has('undead')) mob.undead = values.get('undead') === '1';
    if (values.has('elemattr')) mob.elemAttr = values.get('elemattr');
    mob.linkTarget = ctx.detail.linkTarget ?? mob.linkTarget;
    mob.dirty = ctx.detail.dirty ?? true;
  }

  commitField(ctx, field, value, revert, paint) {
    return this.guard(`field:${ctx.detail.path}:${field.key}`, async () => {
      try {
        const next = await api.mobFields(ctx.detail.path, [{ key: field.key, value }]);
        ctx.detail = next;
        ctx.touched = true;
        // Fields the server has never reported before -- an absent one that was
        // just created -- have no baseline, so record the empty they came from.
        for (const group of next.groups || []) {
          for (const item of group.fields || []) {
            if (!ctx.baseline.has(item.key)) ctx.baseline.set(item.key, '');
          }
        }
        this.syncSummary(ctx);
        this.app.markDirty();
        paint();
      } catch (error) {
        revert?.();
        toastError(error, 'Could not save that field');
      }
    });
  }

  /* ============================================================
     BULK EDIT
     ============================================================ */

  openBulkDialog() {
    return this.guard('bulk-dialog', () => this.buildBulkDialog());
  }

  /**
   * Hands the ticked mobs to the cross-client port dialog.
   *
   * Nothing about the port is mob-specific beyond the `kind` passed here, which
   * is the point: the same dialog serves NPCs from npc.js the moment that mode
   * grows the same button.
   *
   * Reloaded on close because a port that replaced a target mob changes rows in
   * this grid when the target archive is one of the ones it is showing -- and
   * it usually is, since the list spans every open Mob archive by default.
   */
  openPortDialog() {
    return this.guard('port-dialog', async () => {
      await openPortDialog({ app: this.app, kind: 'mob', paths: [...this.selection] });
      await this.load();
    });
  }

  /**
   * The field list comes from a real mob in the selection, because which fields
   * exist depends on the archive. A hardcoded list would offer keys this
   * Mob.wz does not have and hide the ones it does.
   */
  async bulkFieldOptions(path) {
    try {
      const detail = await api.mobDetail(path);
      const found = [];
      for (const group of detail.groups || []) {
        for (const field of group.fields || []) {
          if (field.kind !== 'Int') continue;      // add/multiply mean nothing on a flag or a string
          found.push([field.key, `${group.group} · ${field.label || field.key}`]);
        }
      }
      if (found.length) return found;
    } catch {
      /* fall through to the safe list rather than blocking the dialog */
    }
    return FALLBACK_BULK_FIELDS;
  }

  async buildBulkDialog() {
    const paths = [...this.selection];
    if (!paths.length) {
      toast('Tick a few mobs first — every card has a checkbox in its corner.', 'info');
      return;
    }

    const fields = await this.bulkFieldOptions(paths[0]);

    const fieldSelect = el('select', { class: 'mob-select', 'aria-label': 'Field to change' },
      fields.map(([key, label]) => el('option', { value: key, text: label })));
    const opSelect = el('select', { class: 'mob-select', 'aria-label': 'What to do' },
      OPS.map(([value, label]) => el('option', { value, text: label })));
    const valueInput = el('input', { class: 'mob-input', type: 'number', step: 'any', value: '1' });
    const roundSelect = el('select', { class: 'mob-select', 'aria-label': 'Rounding' },
      ROUNDING.map(([value, label]) => el('option', { value, text: label })));

    const previewHost = el('div', { class: 'mob-preview-host' });
    let preview = null;

    const applyButton = el('button', {
      class: 'btn btn-primary', disabled: true,
      'data-tip': 'Preview first — nothing is written until you have seen the numbers',
    }, icon('check', { size: 15 }), `Apply to ${fmt.format(paths.length)} mob${paths.length === 1 ? '' : 's'}`);

    const previewButton = el('button', { class: 'btn' }, icon('eye', { size: 15 }), 'Preview changes');

    /**
     * Any change to the recipe throws the preview away.
     *
     * An Apply that stays enabled after the value box changes is an Apply that
     * writes something other than what is on screen.
     */
    const invalidate = () => {
      preview = null;
      applyButton.disabled = true;
      clear(previewHost);
      previewHost.append(el('div', { class: 'mob-preview-empty' },
        icon('info', { size: 15 }),
        el('span', { text: 'Preview a change to see the exact before and after for every mob. ' +
                           'Nothing is written until then.' })));
    };
    for (const control of [fieldSelect, opSelect, roundSelect]) control.addEventListener('change', invalidate);
    valueInput.addEventListener('input', invalidate);
    invalidate();

    const recipe = () => ({
      paths,
      field: fieldSelect.value,
      op: opSelect.value,
      value: Number(valueInput.value),
      round: roundSelect.value,
    });

    previewButton.addEventListener('click', () => this.guard('bulk-preview', async () => {
      const body = recipe();
      if (!Number.isFinite(body.value)) {
        toast('Type a number to change the field by.', 'warning');
        return;
      }
      clear(previewHost);
      previewHost.append(el('div', { class: 'mob-preview-empty' }, el('span', { text: 'Working…' })));
      try {
        const result = await api.mobBulk({ ...body, dryRun: true });
        preview = result;
        clear(previewHost);
        previewHost.append(this.buildPreviewTable(result));
        const changing = (result.changes || []).filter((c) => !c.skipped).length;
        applyButton.disabled = changing === 0;
        applyButton.dataset.tip = changing
          ? `Write these ${fmt.format(changing)} changes`
          : 'Nothing here would change';
      } catch (error) {
        preview = null;
        applyButton.disabled = true;
        clear(previewHost);
        previewHost.append(el('div', { class: 'mob-preview-empty', 'data-tone': 'bad' },
          icon('alert', { size: 15 }),
          el('span', { text: error.message })));
      }
    }));

    const { close } = modal({
      title: 'Bulk edit mobs',
      subtitle: `${fmt.format(paths.length)} mob${paths.length === 1 ? '' : 's'} selected`,
      width: 'min(96vw, 900px)',
      body: el('div', { class: 'mob-bulk' },
        el('div', { class: 'mob-bulk-grid' },
          field('Field', fieldSelect),
          field('Change', opSelect),
          field('Value', valueInput),
          field('Rounding', roundSelect, 'Multiply and percent can produce fractions; WZ integers cannot hold one.')),
        el('div', { class: 'mob-bulk-actions' }, previewButton, applyButton),
        previewHost,
        el('div', { class: 'tips' },
          el('b', { text: 'How this behaves' }),
          el('ul', {},
            el('li', { text: 'Preview runs against the server, so what you see is exactly what would be written.' }),
            el('li', { text: 'A mob without the field is skipped, with the reason shown — it is never created here.' }),
            el('li', { text: 'Percent is applied by the server; read the preview rather than assuming a sign.' }),
            el('li', { text: 'Nothing reaches disk until you save, and the whole batch can be undone.' })))),
      actions: [{ label: 'Close' }],
    });

    applyButton.addEventListener('click', () => this.guard('bulk-apply', async () => {
      if (!preview) return;
      const changing = (preview.changes || []).filter((c) => !c.skipped);
      const ok = await confirmDialog({
        title: `Change ${fieldSelect.value} on ${fmt.format(changing.length)} mob${changing.length === 1 ? '' : 's'}?`,
        message: `${OPS.find(([v]) => v === opSelect.value)?.[1]} ${valueInput.value} — this is the preview you ` +
                 'are looking at. Nothing is written to disk until you save, and you can undo it.',
        confirmLabel: 'Apply the changes',
        danger: false,
      });
      if (!ok) return;

      try {
        const result = await api.mobBulk({ ...recipe(), dryRun: false });
        const skipped = (result.changes || []).filter((c) => c.skipped).length;
        close();
        toast(
          `Updated ${fmt.format(result.applied ?? 0)} mob${result.applied === 1 ? '' : 's'}` +
          (skipped ? `, skipped ${fmt.format(skipped)}.` : '.'),
          'success', {
            action: { label: 'Undo', run: async () => { await this.app.undo(); await this.load(); } },
          });
        this.app.markDirty();
        await this.load();
      } catch (error) {
        toastError(error, 'Could not apply those changes');
      }
    }));
  }

  buildPreviewTable(result) {
    const changes = result.changes || [];
    const skipped = changes.filter((c) => c.skipped).length;

    const body = el('tbody');
    for (const change of changes) {
      body.append(el('tr', { 'data-skipped': change.skipped ? 'true' : null },
        el('td', {},
          el('div', { class: 'mob-preview-name', text: change.name || `Mob ${change.mobId}` }),
          el('div', { class: 'mob-sub', text: String(change.mobId) })),
        el('td', { class: 'num', text: change.before ?? '—' }),
        el('td', { class: 'mob-preview-arrow' }, change.skipped ? '' : '→'),
        el('td', { class: 'num', text: change.skipped ? '—' : (change.after ?? '—') }),
        el('td', { class: 'mob-preview-note',
          text: change.skipped ? (change.reason || 'Skipped') : '' })));
    }

    return el('div', {},
      el('div', { class: 'mob-preview-head' },
        el('span', { text: `${fmt.format(changes.length - skipped)} would change` +
                           (skipped ? `, ${fmt.format(skipped)} skipped` : '') }),
        result.truncated
          ? el('span', { class: 'mob-preview-trunc',
              text: 'The server stopped listing early — more mobs are affected than are shown.' })
          : null),
      el('div', { class: 'mob-preview-scroll' },
        el('table', { class: 'mob-preview' },
          el('thead', {}, el('tr', {},
            ['Mob', 'Before', '', 'After', ''].map((h) => el('th', { text: h })))),
          body)));
  }
}

/** One labelled control in the bulk recipe. */
function field(label, control, hint) {
  return el('div', { class: 'mob-bulk-field' },
    el('label', { text: label }), control,
    hint ? el('div', { class: 'mob-field-hint', text: hint }) : null);
}

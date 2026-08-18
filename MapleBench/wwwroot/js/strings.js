/**
 * Strings section: edit the display names in String.wz.
 *
 * Built in the same shape as Mobs, Cash Shop and Database -- one class, guard()
 * around anything that writes or opens a dialog, a head / stats / toolbar /
 * table / pager stack, and honest empty states -- because five sections that do
 * the same kind of work should not feel like five different applications.
 *
 * This is the section the others lean on. String.wz holds every display name the
 * client shows, and its absence is why a newly created item turns up nameless
 * in game: the item exists in Item.wz and nothing names it. Until now the only
 * fix was finding the right path in the Explorer, which means knowing that a
 * skill name lives under Skill.img/0000012 and a map name under
 * Map.img/<region>/<id>. That is what this screen replaces -- ids in, names out,
 * and the backend works out where the entry belongs.
 *
 * Every write goes through /api/string/write or /api/string/bulk, which edit the
 * same session the Explorer edits, so name changes share one dirty state, one
 * undo history and one save pipeline.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt, modal, confirmDialog, debounce, runOnce, scalePixelArt } from './ui.js';
import { emptyState } from './inspector.js';
import { iconUrl } from './media.js';
import { icon } from './icons.js';

const PAGE_SIZE = 40;
const KIND_KEY = 'mb.stringKind';

/** Rule 1: a field key gets a label. Nobody looking for the line under an NPC's name knows it is stored as `func`. */
const FIELD_LABELS = {
  name: 'Name',
  desc: 'Description',
  func: 'Job line',
  mapName: 'Map name',
  streetName: 'Street name',
};

const FIELD_HINTS = {
  desc: 'The tooltip text. Newlines and the client\'s own #c colour codes are kept as typed.',
  func: 'The grey line under an NPC\'s name in game — "Rookie Instructor".',
  streetName: 'The area shown above the map name — "Henesys" in "Henesys : Market".',
};

/**
 * What a kind is called in a sentence, singular. The capabilities response
 * labels them in the plural ("Items"), which reads wrong in "nothing names item
 * 1302999 yet".
 */
const KIND_NOUN = { item: 'item', mob: 'mob', skill: 'skill', npc: 'NPC', map: 'map' };

/**
 * "a" or "an" per kind. A table rather than a vowel test on the first letter:
 * NPC is spelled with a consonant and said with a vowel, so the rule gets it
 * wrong exactly where it is most visible -- in a dialog title.
 */
const KIND_ARTICLE = { item: 'an', mob: 'a', skill: 'a', npc: 'an', map: 'a' };

/** A glyph per kind, for the rows that have no picture to show. */
const KIND_GLYPH = { item: 'image', mob: 'layers', skill: 'star', npc: 'info', map: 'grid4' };

/**
 * Why a row of this kind shows a glyph instead of art, said where the glyph is.
 *
 * Rule 11 asks for the real sprite wherever a thing has one, and rule 12 asks
 * for the edge to be named where the user reaches for it. Both apply here at
 * once, because the answer genuinely differs per kind: an item id resolves to an
 * inventory icon through /api/cashshop/icon, and nothing else does. A mob's
 * `stand/0` lives at a path inside Mob.wz that String.wz does not know, and
 * finding it means a search per row -- 129,496 requests to discover, mostly,
 * that the archive holding the art is not even open. So these rows say what is
 * missing and why rather than showing a grey box that reads as a bug.
 */
const NO_ART = {
  mob: 'Mob sprites live at a path inside Mob.wz. String.wz only stores the name, so this list ' +
       'cannot find the art from an id alone — the Mobs section shows it.',
  skill: 'Skill icons live inside Skill.wz, at a path String.wz does not record.',
  npc: 'NPC sprites live inside Npc.wz, at a path String.wz does not record.',
  map: 'Maps have a minimap rather than an icon, and it lives in Map.wz.',
};

/** The archive each kind's *data* lives in, for the "this only names it" warning. */
const KIND_HOME = {
  item: 'Item.wz or Character.wz',
  mob: 'Mob.wz',
  skill: 'Skill.wz',
  npc: 'Npc.wz',
  map: 'Map.wz',
};

export class StringsSection {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    /** From /api/string/capabilities; nothing is offered before it answers. */
    this.capabilities = { available: false, maxRows: 200, kinds: [], eqpCategories: [], mapRegions: [], unsupported: [] };
    this.checked = false;

    this.kind = localStorage.getItem(KIND_KEY) || 'item';
    this.query = '';
    this.page = 1;

    this.entries = [];
    this.total = 0;
    this.matched = 0;
    this.truncated = false;

    /**
     * The entry for a numeric query that does not exist yet. This is the whole
     * point of the section, so it gets its own banner rather than being folded
     * into "no results".
     */
    this.missing = null;

    /** What stopped the capabilities call, and what stopped the last list. */
    this.error = null;
    this.listError = null;

    /** The request in the air, so the next keystroke can cancel it. */
    this.searching = null;
    /**
     * The owner of the current search, kept after the request settles.
     * The exact-id probe runs *after* the list has already painted and cannot be
     * aborted (api.stringEntry takes no signal), so it needs a token that
     * outlives `searching` to know whether its answer is still wanted.
     */
    this.token = null;

    this.chipButtons = new Map();
  }

  /**
   * Runs an action at most once at a time, reporting anything it throws.
   *
   * Same rule as the other sections: every write is a POST behind a button that
   * stays live while it is in the air, and a create issued twice would write the
   * same entry twice. The keys are namespaced because the in-flight set is
   * shared with the rest of the app.
   */
  async guard(key, run) {
    try {
      await runOnce(`str:${key}`, run);
    } catch (error) {
      toastError(error);
    }
  }

  /* ============================================================
     LIFECYCLE
     ============================================================ */

  async open() {
    this.render();          // paints the chrome while capabilities are asked
    await this.load();
  }

  /** Called after an external change -- a save, an undo, an edit in the Explorer. */
  async refresh() {
    await this.load();
  }

  async load() {
    try {
      const caps = await api.stringCapabilities();
      this.capabilities = {
        available: Boolean(caps.available),
        maxRows: caps.maxRows || 200,
        kinds: caps.kinds || [],
        eqpCategories: caps.eqpCategories || [],
        mapRegions: caps.mapRegions || [],
        unsupported: caps.unsupported || [],
      };
      this.error = null;

      // A backend that answers but names no kinds is one this UI does not
      // understand. Guessing a column layout from a hardcoded table would put
      // editable boxes over fields that may not be there (rule 10).
      if (!this.capabilities.kinds.length) {
        this.error = new Error('The name editor answered without saying which kinds it can edit, so there is nothing safe to show.');
      }
    } catch (error) {
      this.capabilities = { ...this.capabilities, available: false };
      this.error = error;
    }
    this.checked = true;

    // A kind the open archive does not offer must not stay selected: its
    // columns would be right for a different String.wz.
    if (this.capabilities.kinds.length && !this.spec) {
      this.kind = this.capabilities.kinds[0].kind;
      localStorage.setItem(KIND_KEY, this.kind);
    }

    // Rows outlive a reload only if they can still be trusted. Closing String.wz
    // mid-session leaves a table of names read out of an archive that is no
    // longer open, which is exactly the "loaded" flag over dropped data that
    // rule 9 exists to stop.
    if (!this.capabilities.available) {
      this.entries = [];
      this.total = 0;
      this.matched = 0;
      this.missing = null;
    }

    this.render();
    if (this.capabilities.available && !this.error) this.runSearch();
  }

  /** The capabilities entry for the current kind: its label and its fields. */
  get spec() {
    return this.capabilities.kinds.find((k) => k.kind === this.kind) ?? null;
  }

  /** The fields this kind writes, in the order the backend lists them. */
  get fields() {
    return this.spec?.fields ?? [];
  }

  /* ============================================================
     SEARCHING
     ============================================================ */

  /**
   * Runs the current query, cancelling whatever was already in the air.
   *
   * Debouncing thins the requests out but does not order them: a slow answer for
   * "ab" arriving after a quick one for "abc" repainted the table with results
   * for what the box no longer says. Each run aborts the last, and a late
   * response that is no longer the owner is dropped.
   */
  runSearch() {
    this.searching?.abort();

    const mine = new AbortController();
    this.searching = mine;
    this.token = mine;
    this.listError = null;
    this.missing = null;
    this.renderResults();          // paints the "Searching…" state

    const needle = this.query.trim();

    api.stringList(this.kind, needle, this.capabilities.maxRows, { signal: mine.signal })
      .then((data) => {
        if (this.token !== mine) return;
        this.searching = null;
        this.entries = data.entries || [];
        this.total = data.total ?? 0;
        this.matched = data.matched ?? this.entries.length;
        this.truncated = Boolean(data.truncated);
        this.page = 1;

        // String.wz can be closed mid-session, which changes what the toolbar is
        // allowed to offer -- so that one repaints the whole section, not just
        // the results.
        const available = Boolean(data.available);
        if (available !== this.capabilities.available) {
          this.capabilities = { ...this.capabilities, available };
          this.render();
          return;
        }
        this.renderResults();
        this.probeMissing(needle, mine);
      })
      .catch((error) => {
        if (mine.signal.aborted || this.token !== mine) return;
        this.searching = null;
        this.entries = [];
        this.matched = 0;
        this.listError = error;
        this.renderResults();
      });
  }

  /**
   * Asks whether a typed id has an entry at all.
   *
   * The list can only return entries that exist, so "1302999" matching nothing
   * is ambiguous between "no such id" and "that id has no name" -- and the
   * second is the case this whole section is for. One extra call settles it, and
   * it is cheap: the backend probes a dictionary rather than scanning.
   */
  probeMissing(needle, mine) {
    if (!/^\d+$/.test(needle)) return;
    const id = Number(needle);
    if (!Number.isSafeInteger(id) || id <= 0) return;
    if (this.entries.some((entry) => entry.id === id)) return;

    api.stringEntry(this.kind, id)
      .then((entry) => {
        // Cannot be aborted, so ownership is checked instead: a probe for a
        // query the box no longer holds is thrown away.
        if (this.token !== mine || entry.present) return;
        this.missing = entry;
        this.renderResults();
      })
      .catch(() => { /* the banner is a bonus; the list has already answered */ });
  }

  setKind(kind) {
    if (kind === this.kind) return;
    this.kind = kind;
    localStorage.setItem(KIND_KEY, kind);
    this.page = 1;
    for (const [value, button] of this.chipButtons) {
      button.setAttribute('aria-pressed', value === kind ? 'true' : 'false');
    }
    // The columns are per kind, so the whole section repaints rather than only
    // the rows -- a Map row has no "Description" to put under that header.
    this.render();
    this.runSearch();
  }

  /* ============================================================
     RENDER
     ============================================================ */

  render() {
    clear(this.host);
    this.host.className = 'stage-body strings';
    this.chipButtons.clear();

    if (this.error) {
      this.host.append(emptyState(
        'alert', 'Could not reach the name editor',
        this.error.message,
        el('div', { class: 'str-empty-actions' },
          el('button', { class: 'btn btn-primary', onclick: () => this.load() },
            icon('refresh', { size: 15 }), 'Try again'))));
      return;
    }

    // Before the first capabilities answer nothing is known -- not which kinds
    // exist, not which fields they carry, not whether String.wz is open at all.
    // Painting a table with no columns and an empty state saying "nothing of
    // this kind is named" would be three claims made before anything was asked.
    if (!this.checked) {
      const panel = el('div', { class: 'str-panel' });
      for (let i = 0; i < 6; i++) {
        panel.append(el('div', { class: 'skeleton skeleton-row', style: `width:${45 + ((i * 37) % 40)}%` }));
      }
      this.host.append(this.buildHead(), panel);
      return;
    }

    if (!this.capabilities.available) {
      this.host.append(this.buildHead(), emptyState(
        'type', 'No String.wz open',
        'Display names live in String.wz. Open String.wz or the whole client folder before editing names.',
        el('button', { class: 'btn btn-primary', onclick: () => this.app.openFilePicker() },
          icon('folderOpen', { size: 15 }), 'Open String.wz')));
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
   * Names the archive being written to.
   *
   * Every name on this screen is read out of, and written back into, the user's
   * own String.wz at the user's own version. Which one that is decides whether
   * an id resolves to the right thing, so the section says which file it is
   * editing rather than presenting names as facts about MapleStory.
   */
  buildHead() {
    const sources = this.app.files.filter((file) => /^string/i.test(file.name));
    const subject = sources.length === 1
      ? sources[0].name
      : sources.length > 1
        ? `${sources.length} String archives`
        : 'no String archive';

    return el('div', { class: 'str-panel str-head' },
      el('div', { class: 'str-head-text' },
        el('div', { class: 'str-head-title', text: 'Strings' }),
        el('div', { class: 'str-head-sub' },
          el('span', { text: 'Editing ' }),
          el('b', { text: subject }),
          el('span', { text: this.capabilities.available ? '' : ' · not open' }))),
      el('button', {
        class: 'btn btn-icon', 'data-tip': 'Check the open archives again',
        onclick: () => this.load(),
      }, icon('refresh', { size: 15 })));
  }

  /* There was a row of three counts here: "Items named 81,481", "Listed
     81,481" and "Shown here 500". Two of them were the same number whenever
     there was no query, and all three were restated a hundred pixels below in
     the sentence over the table -- "First 500 of 81,481, by id" -- which says
     it in one line, in the place you are already looking when you wonder how
     much of the list you can see. Three boxes and ninety pixels for a fact the
     result head already carried. */

  buildToolbar() {
    const search = el('input', {
      type: 'search', value: this.query,
      placeholder: `Search ${(this.spec?.label ?? this.kind).toLowerCase()} by name or id — "Blue Sword", "1302000"…`,
      'aria-label': 'Search names',
    });
    // Only the results are re-rendered, never the toolbar holding this input:
    // rebuilding it destroys the caret and drops IME composition mid-word.
    search.addEventListener('input', debounce(() => {
      this.query = search.value;
      this.page = 1;
      this.runSearch();
    }, 160));
    // Enter re-runs immediately rather than waiting out the debounce, which is
    // what someone who has just typed an id expects.
    search.addEventListener('keydown', (event) => {
      if (event.key !== 'Enter') return;
      event.preventDefault();
      this.query = search.value;
      this.page = 1;
      this.runSearch();
    });
    this.searchInput = search;

    const chips = el('div', { class: 'cat-tabs' });
    for (const kind of this.capabilities.kinds) {
      const button = el('button', {
        class: 'cat-tab', 'aria-pressed': this.kind === kind.kind ? 'true' : 'false',
        // The fields are named on the chip: which of them a kind carries is not
        // guessable, and it is the first thing you need to know before picking.
        'data-tip': `${(kind.fields || []).map((f) => FIELD_LABELS[f] ?? f).join(' and ')}`,
        onclick: () => this.setKind(kind.kind),
      }, kind.label);
      this.chipButtons.set(kind.kind, button);
      chips.append(button);
    }

    return el('div', { class: 'str-panel' },
      el('div', { class: 'str-toolbar' },
        el('div', { class: 'str-search' }, el('span', { class: 'icon' }, icon('search', { size: 15 })), search),
        chips,
        el('button', {
          class: 'btn btn-primary',
          'data-tip': 'Give an id a name, creating its entry if it has none',
          onclick: () => this.openWriteDialog(),
        }, icon('plus', { size: 15 }), 'Name an id'),
        el('button', {
          class: 'btn', 'data-tip': 'Paste a column of ids and names from a spreadsheet',
          onclick: () => this.openBulkDialog(),
        }, icon('list', { size: 15 }), 'Bulk names')),
      this.truncated
        ? el('div', { class: 'str-note-bar' }, icon('alert', { size: 14 }),
            el('span', {
              text: `${fmt.format(this.matched)} entries match this and the best ` +
                    `${fmt.format(this.entries.length)} are shown. Type more of the name, or an id, to narrow it.`,
            }))
        : null,
      this.buildUnsupported());
  }

  /**
   * Rule 12: name the edge where the user reaches for it.
   *
   * The client's own UI text is in String.wz too, sitting right beside the
   * things this screen edits, and it is not keyed by id -- so there is no safe
   * generic write for it. Saying so here beats letting someone search for
   * "Level up!" and conclude the search is broken.
   */
  buildUnsupported() {
    if (!this.capabilities.unsupported.length) return null;
    return el('details', { class: 'str-limits' },
      el('summary', {}, icon('info', { size: 14 }),
        el('span', { text: `Not editable here (${this.capabilities.unsupported.length})` })),
      el('ul', {}, this.capabilities.unsupported.map((line) => el('li', { text: line }))),
      el('div', { class: 'str-limits-foot' },
        el('button', {
          class: 'btn btn-sm',
          onclick: () => { this.app.setMode('explorer'); },
        }, icon('externalLink', { size: 14 }), 'Open the Explorer')));
  }

  /** Puts the caret in the search box; used by the palette command. */
  focusSearch() {
    this.searchInput?.focus();
    this.searchInput?.select();
  }

  /** Redraws only the result panel, leaving the search box untouched. */
  renderResults() {
    if (!this.resultHost) { this.render(); return; }
    clear(this.resultHost);

    const panel = el('div', { class: 'str-panel' });

    if (this.listError) {
      panel.append(emptyState(
        'alert', 'That search did not finish',
        this.listError.message,
        el('button', { class: 'btn btn-primary', onclick: () => this.runSearch() },
          icon('refresh', { size: 15 }), 'Try again')));
      this.resultHost.append(panel);
      return;
    }

    if (this.searching) {
      panel.append(el('div', { class: 'str-result-head' }, el('span', { text: 'Searching…' })));
      for (let i = 0; i < 6; i++) {
        panel.append(el('div', { class: 'skeleton skeleton-row', style: `width:${45 + ((i * 37) % 40)}%` }));
      }
      this.resultHost.append(panel);
      return;
    }

    // The headline case, above everything else: an id nothing names yet.
    if (this.missing) panel.append(this.buildMissingBanner(this.missing));

    if (!this.total) {
      // The archive is open and holds no entry of this kind at all -- a
      // different fact from "your search matched nothing", and it needs a
      // different sentence.
      panel.append(emptyState(
        'type', `Nothing of this kind is named in ${this.headSubject()}`,
        `This String.wz has no ${(this.spec?.label ?? this.kind).toLowerCase()} entries — either the image that ` +
        'holds them is missing, or it is empty. You can still create the first one.',
        el('div', { class: 'str-empty-actions' },
          el('button', { class: 'btn btn-primary', onclick: () => this.openWriteDialog() },
            icon('plus', { size: 15 }), 'Name an id'))));
      this.resultHost.append(panel);
      return;
    }

    const pages = Math.max(1, Math.ceil(this.entries.length / PAGE_SIZE));
    this.page = Math.min(this.page, pages);
    const slice = this.entries.slice((this.page - 1) * PAGE_SIZE, this.page * PAGE_SIZE);

    panel.append(el('div', { class: 'str-result-head' },
      el('span', {
        text: this.query.trim()
          ? `${fmt.format(this.matched)} match${this.matched === 1 ? '' : 'es'}` +
            (this.truncated ? `, best ${fmt.format(this.entries.length)} shown` : '')
          : `First ${fmt.format(this.entries.length)} of ${fmt.format(this.total)}, by id`,
      }),
      el('span', { text: `Page ${this.page} of ${pages}` })));

    if (!this.entries.length) {
      const needle = this.query.trim();
      panel.append(emptyState(
        'search', `Nothing matches "${needle}"`,
        /^\d+$/.test(needle)
          ? (this.missing
              ? 'That id has no entry yet — the banner above will create one.'
              : 'No entry with that id, and no name containing those digits.')
          : 'Names are read from your own client, so anything it does not name cannot be found by name. ' +
            'Try an id — leading zeros are fine — or part of a name.',
        el('div', { class: 'str-empty-actions' },
          el('button', { class: 'btn', text: 'Clear search',
            onclick: () => { this.query = ''; this.render(); this.runSearch(); } }),
          el('button', { class: 'btn btn-primary', onclick: () => this.openWriteDialog(/^\d+$/.test(needle) ? needle : '') },
            icon('plus', { size: 15 }), 'Name an id'))));
    } else {
      panel.append(this.buildTable(slice));
      if (pages > 1) panel.append(this.buildPager(pages));
    }

    this.resultHost.append(panel);
  }

  headSubject() {
    const sources = this.app.files.filter((file) => /^string/i.test(file.name));
    return sources.length === 1 ? sources[0].name : 'this archive';
  }

  /**
   * The reason this section exists, said plainly.
   *
   * Not a blank row in the table: an empty editable cell where an entry ought to
   * be says "this name is empty", and the true answer is "there is no entry at
   * all, which is why the client shows nothing". Those need different words and
   * different buttons.
   */
  buildMissingBanner(entry) {
    const noun = KIND_NOUN[entry.kind] ?? entry.kind;
    return el('div', { class: 'str-missing' },
      el('span', { class: 'str-missing-glyph' }, icon('alert', { size: 18 })),
      // The item's own icon, where there is one. It is the proof that the id is
      // real and is the thing the user meant: the art is in Item.wz and only the
      // name is missing, which is exactly the state this banner describes. Not
      // shown for the other kinds, where it could only ever be a second grey
      // glyph beside the alert.
      entry.kind === 'item' ? this.buildThumb('item', entry.id, { size: 40 }) : null,
      el('div', { class: 'str-missing-text' },
        el('b', { text: `Nothing names ${noun} ${entry.id} yet.` }),
        el('span', {
          text: ` In game it shows up blank. Creating the entry puts it in ${entry.image}` +
                `${entry.group ? ` (${entry.group})` : ''}, which is where the client looks for it.`,
        })),
      el('button', {
        class: 'btn btn-primary',
        onclick: () => this.openWriteDialog(String(entry.id)),
      }, icon('plus', { size: 15 }), `Name ${noun} ${entry.id}`));
  }

  buildTable(entries) {
    const fields = this.fields;

    const body = el('tbody');
    for (const entry of entries) body.append(this.buildRow(entry));

    return el('div', { class: 'str-table-scroll' },
      el('table', { class: 'str-table' },
        el('thead', {}, el('tr', {},
          el('th', { class: 'str-col-icon' }),
          el('th', { class: 'str-col-id', text: 'ID' }),
          fields.map((field) => el('th', {
            text: FIELD_LABELS[field] ?? field,
            'data-tip': FIELD_HINTS[field] ?? null,
          })),
          el('th', { class: 'str-col-where', text: 'Lives in' }),
          el('th', { class: 'str-col-act' }))),
        body));
  }

  /**
   * The picture for one id, or an honest admission that there is not one.
   *
   * Only items have a source that takes an id and nothing else:
   * /api/cashshop/icon reads the inventory icon out of the user's own
   * Item.wz/Character.wz and follows _inlink/_outlink to get there. Every other
   * kind would need a session path, and this list is keyed by id -- see NO_ART.
   *
   * The URL comes from media.js rather than api.js directly because it carries
   * the session epoch: an item id means nothing without the archive it was read
   * from, and closing Item.wz used to leave its icons rendering over the next
   * session's items.
   */
  buildThumb(kind, id, { size = 34 } = {}) {
    const glyph = KIND_GLYPH[kind] ?? 'file';
    // data-art starts false and is only ever flipped by a load that produced
    // real pixels, so the checkerboard behind it never appears under nothing.
    const box = el('span', { class: 'str-thumb', 'data-art': 'false', style: `--thumb:${size}px` });

    // The glyph goes in first and stays until art actually arrives. Painting the
    // empty box first and filling it on failure meant a row sat blank for the
    // length of a 404 -- which, on a page of forty ids the client has no icon
    // for, is forty boxes that look like images still loading and never do.
    const placeholder = el('span', { class: 'str-thumb-glyph' }, icon(glyph, { size: Math.round(size * 0.47) }));
    box.append(placeholder);

    if (kind !== 'item') {
      box.dataset.tip = NO_ART[kind] ?? 'There is no art for this kind here.';
      return box;
    }

    const img = el('img', { alt: '', loading: 'lazy' });

    // Both listeners are attached BEFORE src is set. A cached image fires
    // `load` synchronously during the assignment, so wiring them afterwards
    // means the handler never runs and the icon is never sized.
    img.addEventListener('error', () => {
      // Nothing to swap in: the glyph never left. An id the client has no icon
      // for answers 404, and that is the common case on a recipe or a hair.
      img.remove();
    });
    img.addEventListener('load', () => {
      // A 204 or an empty body can still resolve as a load; an image with no
      // pixels is not art, and stretching it would draw a smear.
      if (!img.naturalWidth || !img.naturalHeight) { img.remove(); return; }
      // Whole-number upscale inside the fixed box, with room to breathe.
      scalePixelArt(img, size - 6, size - 6);
      box.dataset.art = 'true';
      placeholder.remove();
    });

    // In the document rather than detached, so loading="lazy" has a box to
    // measure against and the rows below the fold cost nothing until scrolled to.
    box.append(img);
    img.src = iconUrl(id);
    return box;
  }

  buildRow(entry) {
    const row = el('tr', { 'data-id': entry.id });

    // Rule 11: a row with a picture is worth three with ids.
    row.append(el('td', { class: 'str-col-icon' }, this.buildThumb(entry.kind ?? this.kind, entry.id)));

    row.append(el('td', { class: 'str-col-id' },
      el('button', {
        class: 'str-idbtn', text: String(entry.id),
        'data-tip': 'Actions for this entry',
        onclick: (event) => {
          const rect = event.currentTarget.getBoundingClientRect();
          this.app.showMenu(this.menuItemsFor(entry), rect.left, rect.bottom + 4);
        },
      })));

    for (const field of this.fields) row.append(this.buildCell(entry, field));

    // Where the entry physically lives. Not decoration: String.wz has four
    // layouts and the same kind of thing sits in different images depending on
    // its id, which is exactly what makes this hard to do by hand.
    const where = el('td', { class: 'str-col-where' },
      el('span', { class: 'str-where-img', text: entry.image || '—' }),
      entry.group ? el('span', { class: 'str-where-group', text: entry.group }) : null);
    row.append(where);

    row.append(el('td', { class: 'str-col-act' },
      el('button', {
        class: 'btn btn-sm btn-icon', 'data-tip': 'Show this entry in the Explorer',
        disabled: !entry.path,
        onclick: () => {
          this.app.setMode('explorer');
          this.app.navigate(entry.path);
        },
      }, icon('externalLink', { size: 14 }))));

    return row;
  }

  /**
   * One inline-editable field.
   *
   * Committed on blur and on Enter, reverted on Escape -- the same contract the
   * mob card uses, so Tab across a row commits each cell and moves on.
   */
  buildCell(entry, field) {
    const current = entry.fields?.[field] ?? null;

    const input = el('input', {
      class: 'str-input', value: current ?? '', spellcheck: 'false',
      'aria-label': `${FIELD_LABELS[field] ?? field} of ${entry.id}`,
      // An unset field reads as "Not set", never as an empty box: the entry
      // exists, this one field of it does not, and typing here creates it.
      placeholder: 'Not set — type to add it',
      'data-unset': current === null ? 'true' : null,
    });

    const note = el('div', { class: 'str-note', hidden: true });
    let committed = current ?? '';

    const say = (text, tone) => {
      note.textContent = text || '';
      note.hidden = !text;
      if (tone) note.dataset.tone = tone; else delete note.dataset.tone;
    };

    const send = () => {
      const next = input.value;
      if (next === committed) return;

      // A field that was never set and is still blank is not a request to write
      // an empty string node. Clearing one that *did* have text is, and it goes
      // through -- an empty name and a missing name are different states and the
      // backend treats them as such.
      if (current === null && next.trim() === '' && committed === '') {
        input.value = '';
        return;
      }

      this.commitField(entry, field, next, {
        input,
        say,
        accept: (value) => { committed = value; },
        revert: () => { input.value = committed; },
      });
    };

    input.addEventListener('blur', send);
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); send(); }
      else if (event.key === 'Escape') { event.preventDefault(); input.value = committed; input.blur(); }
    });

    return el('td', {}, el('div', { class: 'str-cell' }, input, note));
  }

  /**
   * Writes one field of one entry.
   *
   * A single field is one node, so this commits straight through rather than
   * previewing: rule 2's diff-first requirement is about writes that touch more
   * than one node, which is the create flow and the bulk apply below. What it
   * does still do is report a skip -- the backend refuses to overwrite a field
   * that holds a group of values rather than a string, and a silent no-op there
   * is the exact failure this app exists not to have (rule 3).
   */
  commitField(entry, field, value, ui) {
    return this.guard(`field:${this.kind}:${entry.id}:${field}`, async () => {
      ui.input.setAttribute('aria-busy', 'true');
      try {
        const result = await api.stringWrite({ kind: this.kind, id: entry.id, [field]: value });
        const change = (result.changes || []).find((c) => c.field === field);

        if (change?.skipped) {
          // "Already set to this." is a fact, not a failure: keep what is on
          // screen and say why nothing happened.
          ui.accept(value);
          ui.say(change.reason || 'Skipped.', change.reason === 'Already set to this.' ? null : 'warn');
          if (change.reason && change.reason !== 'Already set to this.') {
            toast(change.reason, 'warning', { title: `${FIELD_LABELS[field] ?? field} was not written` });
          }
          return;
        }

        ui.accept(value);
        ui.say(change?.created ? 'Added.' : 'Saved.', 'good');
        // The row now knows its own new state, so the table is not reloaded --
        // a reload here would take the caret out of the cell that was just
        // typed in and undo the point of editing inline.
        if (result.entry) {
          entry.fields = result.entry.fields || entry.fields;
          entry.path = result.entry.path ?? entry.path;
          entry.present = true;
        }
        if (value !== '') delete ui.input.dataset.unset;
        this.app.markDirty();
      } catch (error) {
        ui.revert();
        ui.say(error.message, 'bad');
        toastError(error, 'Could not save that name');
      } finally {
        ui.input.removeAttribute('aria-busy');
      }
    });
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

  /** Shared by the id button and the app-wide right-click menu. */
  menuItemsFor(entry) {
    const noun = KIND_NOUN[entry.kind] ?? entry.kind;
    return [
      { icon: 'copy', label: `Copy id ${entry.id}`, run: () => this.app.copyText(String(entry.id)) },
      entry.fields?.[this.fields[0]]
        ? { icon: 'copy', label: 'Copy name', run: () => this.app.copyText(entry.fields[this.fields[0]]) }
        : null,
      entry.path
        ? { icon: 'externalLink', label: 'Show in Explorer', run: () => {
            this.app.setMode('explorer');
            this.app.navigate(entry.path);
          } }
        : null,
      { icon: 'edit', label: `Edit every field of ${noun} ${entry.id}…`, run: () => this.openWriteDialog(String(entry.id)) },
    ].filter(Boolean);
  }

  /** Right-click actions for the section; shared with the app-wide context menu. */
  menuItems() {
    return [
      { icon: 'search', label: 'Focus the search box', run: () => this.focusSearch() },
      { icon: 'plus', label: 'Name an id…', run: () => this.openWriteDialog() },
      { icon: 'list', label: 'Bulk names…', run: () => this.openBulkDialog() },
      this.query
        ? { icon: 'close', label: 'Clear the search', run: () => { this.query = ''; this.render(); this.runSearch(); } }
        : null,
      { icon: 'refresh', label: 'Check the open archives again', run: () => this.load() },
    ].filter(Boolean);
  }

  /* ============================================================
     NAME AN ID  --  the create-or-update flow
     ============================================================ */

  openWriteDialog(seedId = '') {
    // Guarded because capabilities and the first probe run *before* modal():
    // two fast clicks would otherwise race, and the second dialog's commit
    // closures would point at a form the first one had already replaced.
    return this.guard('write-dialog', () => this.buildWriteDialog(seedId));
  }

  async buildWriteDialog(seedId) {
    const fields = this.fields;
    if (!fields.length) {
      toast('This kind has no editable fields, so there is nothing to write.', 'warning');
      return;
    }

    const noun = KIND_NOUN[this.kind] ?? this.kind;

    const idInput = el('input', {
      class: 'str-input', type: 'number', min: '1', value: seedId,
      placeholder: `e.g. ${this.entries[0]?.id ?? 1302000}`, 'aria-label': `${noun} id`,
    });

    const inputs = new Map();
    for (const field of fields) {
      const control = field === 'desc'
        ? el('textarea', { class: 'str-input str-area', rows: '3', spellcheck: 'false' })
        : el('input', { class: 'str-input', spellcheck: 'false' });
      control.setAttribute('aria-label', FIELD_LABELS[field] ?? field);
      inputs.set(field, control);
    }

    /* --- where a new entry would be filed --- */
    const groupSelect = el('select', { class: 'str-select', 'aria-label': 'Where to file a new entry' });
    const groupWrap = el('div', { class: 'str-field', hidden: true },
      el('label', { text: this.kind === 'map' ? 'Region' : 'Category' }),
      groupSelect,
      el('div', { class: 'field-hint' }));
    const groupHint = groupWrap.querySelector('.field-hint');

    const fillGroups = (options) => {
      clear(groupSelect);
      groupSelect.append(el('option', { value: '', text: 'Work it out from the archive' }));
      for (const option of options) groupSelect.append(el('option', { value: option, text: option }));
    };

    const statusHost = el('div', { class: 'str-status' });
    const previewHost = el('div', { class: 'str-preview-host' });

    /** The entry as the backend currently sees it, or null before the first probe. */
    let known = null;
    let preview = null;

    const previewButton = el('button', { class: 'btn' }, icon('eye', { size: 15 }), 'Preview');
    const writeButton = el('button', {
      class: 'btn btn-primary', disabled: true,
      'data-tip': 'Preview first — nothing is written until you have seen what it would do',
    }, icon('check', { size: 15 }), 'Write it');

    /**
     * Any change to the form throws the preview away.
     *
     * A write button that stays enabled after the id box changes is a write
     * button that writes something other than what is on screen.
     */
    const invalidate = () => {
      preview = null;
      writeButton.disabled = true;
      clear(previewHost);
      previewHost.append(el('div', { class: 'str-preview-empty' },
        icon('info', { size: 15 }),
        el('span', {
          text: 'Preview to see exactly what would be created or changed, field by field. ' +
                'Nothing is written until then.',
        })));
    };

    /** Asks the backend what this id currently is, and says so. */
    const probe = async () => {
      const id = Number(idInput.value);
      clear(statusHost);
      invalidate();

      if (!Number.isSafeInteger(id) || id <= 0) {
        known = null;
        groupWrap.hidden = true;
        statusHost.append(el('div', { class: 'str-status-line', 'data-tone': 'idle' },
          el('span', { text: `Type the ${noun} id you want to name.` })));
        return;
      }

      statusHost.append(el('div', { class: 'str-status-line', 'data-tone': 'idle' },
        el('span', { text: 'Looking it up…' })));

      let entry;
      try {
        entry = await api.stringEntry(this.kind, id);
      } catch (error) {
        known = null;
        clear(statusHost);
        statusHost.append(el('div', { class: 'str-status-line', 'data-tone': 'bad' },
          icon('alert', { size: 15 }), el('span', { text: error.message })));
        return;
      }

      // The box may have moved on while the lookup was in the air.
      if (Number(idInput.value) !== id) return;
      known = entry;

      clear(statusHost);
      // The picture of what is about to be named, where the id resolves to one.
      // Typing 1302000 when you meant 1032000 is the mistake this catches, and
      // it catches it before the write rather than after.
      if (this.kind === 'item') statusHost.append(this.buildThumb('item', id, { size: 44 }));

      if (entry.present) {
        statusHost.append(el('div', { class: 'str-status-line', 'data-tone': 'known' },
          icon('info', { size: 15 }),
          el('span', {
            text: `${entry.image}${entry.group ? ` › ${entry.group}` : ''} already names this ${noun}. ` +
                  'Writing here updates that entry rather than making a second one.',
          })));
        // Prefilled with what is there, so an edit starts from the truth and a
        // field left alone is left alone.
        for (const [field, control] of inputs) control.value = entry.fields?.[field] ?? '';
        groupWrap.hidden = true;
      } else {
        statusHost.append(el('div', { class: 'str-status-line', 'data-tone': 'new' },
          icon('plus', { size: 15 }),
          el('span', {
            text: `Nothing names ${noun} ${id} yet. The entry will be created in ${entry.image}.`,
          })));
        for (const control of inputs.values()) control.value = '';

        // A new equip needs an Eqp category and a new map needs a region; the
        // other images take the id directly and have nothing to choose.
        const needsGroup = (this.kind === 'item' && entry.image === 'Eqp.img') || this.kind === 'map';
        if (needsGroup) {
          const options = this.kind === 'map' ? this.capabilities.mapRegions : this.capabilities.eqpCategories;
          fillGroups(options);
          groupHint.textContent = options.length
            ? 'Left alone, the backend picks the group the ids around this one already use, and says which. ' +
              'Choose one here to override it.'
            : 'This archive lists none, so the backend will have to work it out on its own.';
          groupWrap.hidden = false;
        } else {
          groupWrap.hidden = true;
        }
      }
    };
    // Deliberately no focus move at the end of a probe: it runs 250 ms after a
    // keystroke, and pulling the caret into the name box while someone is still
    // typing an id sends the rest of the digits into the name.

    idInput.addEventListener('input', debounce(probe, 250));
    idInput.addEventListener('change', () => probe());
    for (const control of inputs.values()) control.addEventListener('input', invalidate);
    groupSelect.addEventListener('change', invalidate);

    /** The row as the bulk endpoint wants it -- one entry, so its preview is this one's. */
    const recipe = () => {
      const entry = { id: Number(idInput.value) };
      for (const [field, control] of inputs) {
        const value = control.value;
        // Null is "leave it alone", "" is "make it empty". Only send a field the
        // user actually put something in, or deliberately cleared on an entry
        // that already had text there.
        const had = known?.present ? (known.fields?.[field] ?? null) : null;
        if (value !== '' || (had !== null && had !== '')) entry[field] = value;
      }
      if (groupSelect.value && !groupWrap.hidden) {
        if (this.kind === 'map') entry.region = groupSelect.value;
        else entry.category = groupSelect.value;
      }
      return entry;
    };

    previewButton.addEventListener('click', () => this.guard('write-preview', async () => {
      const entry = recipe();
      if (!Number.isSafeInteger(entry.id) || entry.id <= 0) {
        toast(`Type the ${noun} id first.`, 'warning');
        return;
      }
      if (!this.fields.some((field) => field in entry)) {
        toast(`Type at least one of ${this.fields.map((f) => FIELD_LABELS[f] ?? f).join(' or ')}.`, 'warning');
        return;
      }

      clear(previewHost);
      previewHost.append(el('div', { class: 'str-preview-empty' }, el('span', { text: 'Working…' })));
      try {
        // The single write has no dry run of its own, so the bulk endpoint's is
        // used with one row. Both call the same Apply() on the backend, so a
        // preview cannot disagree with the write that follows it.
        const result = await api.stringBulk({ kind: this.kind, entries: [entry], dryRun: true });
        preview = result.rows?.[0] ?? null;
        clear(previewHost);
        previewHost.append(this.buildPreview(result, { single: true }));
        const writes = (preview?.changes || []).filter((c) => !c.skipped).length;
        writeButton.disabled = !preview || preview.skipped || (writes === 0 && !preview.createdEntry);
        writeButton.dataset.tip = writeButton.disabled
          ? (preview?.reason || 'Nothing here would change')
          : `Write ${fmt.format(writes)} field${writes === 1 ? '' : 's'}`;
      } catch (error) {
        preview = null;
        writeButton.disabled = true;
        clear(previewHost);
        previewHost.append(el('div', { class: 'str-preview-empty', 'data-tone': 'bad' },
          icon('alert', { size: 15 }), el('span', { text: error.message })));
      }
    }));

    invalidate();
    await probe();

    const { close } = modal({
      title: `Name ${KIND_ARTICLE[this.kind] ?? 'a'} ${noun}`,
      subtitle: `Writes into ${this.headSubject()} · one undo step`,
      width: 'min(96vw, 760px)',
      body: el('div', { class: 'str-write' },
        el('div', { class: 'str-field' },
          el('label', { text: `${noun.charAt(0).toUpperCase()}${noun.slice(1)} id` }),
          idInput,
          el('div', { class: 'field-hint',
            text: `Leading zeros are fine. This names the id — it does not create the ${noun} itself, ` +
                  `which lives in ${KIND_HOME[this.kind] ?? 'its own archive'}.` })),
        statusHost,
        ...fields.map((field) => el('div', { class: 'str-field' },
          el('label', { text: FIELD_LABELS[field] ?? field }),
          inputs.get(field),
          FIELD_HINTS[field] ? el('div', { class: 'field-hint', text: FIELD_HINTS[field] }) : null)),
        groupWrap,
        el('div', { class: 'str-write-actions' }, previewButton, writeButton),
        previewHost),
      actions: [{ label: 'Close' }],
      // With an id already known -- arrived at from the "nothing names this"
      // banner -- the id box is answered and the caret belongs in the name.
      onOpen: () => { (seedId ? inputs.get(fields[0]) : idInput)?.focus(); },
    });

    writeButton.addEventListener('click', () => this.guard('write-apply', async () => {
      if (!preview) return;
      const entry = recipe();
      try {
        const result = await api.stringWrite({ kind: this.kind, id: entry.id, ...entry });
        this.app.markDirty();

        const skipped = (result.changes || []).filter((c) => c.skipped);
        close();

        // The backend's own sentence, verbatim. It is the only thing that knows
        // *why* a category was chosen -- "44 ids in the same range are already
        // in the Taming category" -- and paraphrasing it here would be a guess
        // about someone else's data.
        const because = result.groupReason ? ` ${result.groupReason}` : '';
        toast(
          (result.createdEntry
            ? `Created ${noun} ${result.id} in ${result.image}${result.group ? ` › ${result.group}` : ''}.`
            : `Updated ${noun} ${result.id}.`) +
          because +
          (skipped.length ? ` ${skipped.length} field left alone: ${skipped.map((c) => c.reason).join(' ')}` : ''),
          skipped.length ? 'warning' : 'success', {
            title: result.createdEntry ? 'Entry created' : 'Entry updated',
            action: { label: 'Undo', run: async () => { await this.app.undo(); await this.refresh(); } },
          });

        // Land on the entry that was just written, so the result is on screen
        // rather than somewhere in the list the user has to go and find.
        this.query = String(result.id);
        if (this.searchInput) this.searchInput.value = this.query;
        await this.load();
      } catch (error) {
        toastError(error, 'Could not write that name');
      }
    }));
  }

  /* ============================================================
     BULK NAMES
     ============================================================ */

  openBulkDialog() {
    return this.guard('bulk-dialog', () => this.buildBulkDialog());
  }

  async buildBulkDialog() {
    const fields = this.fields;
    const noun = KIND_NOUN[this.kind] ?? this.kind;
    const max = this.capabilities.maxRows;

    const paste = el('textarea', {
      class: 'str-paste', rows: '10', spellcheck: 'false',
      placeholder: `1302000\tBlue Sword${fields.length > 1 ? `\tA sword forged in Perion.` : ''}\n` +
                   `1302001\tSteel Sword${fields.length > 1 ? `\tHeavier than it looks.` : ''}`,
      'aria-label': 'Ids and names, one per line',
    });

    const groupSelect = el('select', { class: 'str-select', 'aria-label': 'Where to file new entries' });
    const needsGroup = this.kind === 'item' || this.kind === 'map';
    const groupOptions = this.kind === 'map' ? this.capabilities.mapRegions : this.capabilities.eqpCategories;
    groupSelect.append(el('option', { value: '', text: 'Work it out per id from the archive' }));
    for (const option of groupOptions) groupSelect.append(el('option', { value: option, text: option }));

    const parsedHost = el('div', { class: 'str-parsed' });
    const previewHost = el('div', { class: 'str-preview-host' });

    let preview = null;
    let rows = [];

    const applyButton = el('button', {
      class: 'btn btn-primary', disabled: true,
      'data-tip': 'Preview first — nothing is written until you have seen the rows',
    }, icon('check', { size: 15 }), 'Apply');

    const previewButton = el('button', { class: 'btn' }, icon('eye', { size: 15 }), 'Preview (dry run)');

    const invalidate = () => {
      preview = null;
      applyButton.disabled = true;
      clear(previewHost);
      previewHost.append(el('div', { class: 'str-preview-empty' },
        icon('info', { size: 15 }),
        el('span', {
          text: 'Preview runs the whole batch against the server without writing anything, and shows ' +
                'the before and after for every field of every row.',
        })));
    };

    const reparse = () => {
      const result = parseRows(paste.value, fields, max);
      rows = result.rows;
      clear(parsedHost);

      if (!paste.value.trim()) {
        parsedHost.append(el('span', { class: 'str-parsed-idle', text: 'Nothing pasted yet.' }));
      } else {
        parsedHost.append(el('span', {
          text: `${fmt.format(rows.length)} row${rows.length === 1 ? '' : 's'} understood`,
        }));
        // Rule 3: lines that will not be sent are named, with the reason,
        // before anything runs -- not silently dropped.
        if (result.rejected.length) {
          parsedHost.append(el('span', { class: 'str-parsed-bad' },
            icon('alert', { size: 13 }),
            el('span', {
              text: `${fmt.format(result.rejected.length)} line${result.rejected.length === 1 ? '' : 's'} ignored: ` +
                    result.rejected.slice(0, 3).map((r) => `line ${r.line} (${r.reason})`).join(', ') +
                    (result.rejected.length > 3 ? '…' : ''),
            })));
        }
        if (result.overflow) {
          parsedHost.append(el('span', { class: 'str-parsed-bad' },
            icon('alert', { size: 13 }),
            el('span', {
              text: `Only the first ${fmt.format(max)} rows are taken; ${fmt.format(result.overflow)} more are ` +
                    'left out. Apply these, then paste the rest.',
            })));
        }
      }
      invalidate();
    };

    paste.addEventListener('input', debounce(reparse, 200));
    groupSelect.addEventListener('change', invalidate);
    reparse();

    const entries = () => rows.map((row) => {
      const entry = { ...row.fields, id: row.id };
      if (groupSelect.value) {
        if (this.kind === 'map') entry.region = groupSelect.value;
        else entry.category = groupSelect.value;
      }
      return entry;
    });

    previewButton.addEventListener('click', () => this.guard('bulk-preview', async () => {
      if (!rows.length) {
        toast('Paste at least one line of "id, name" first.', 'warning');
        return;
      }
      clear(previewHost);
      previewHost.append(el('div', { class: 'str-preview-empty' }, el('span', { text: 'Working…' })));
      try {
        const result = await api.stringBulk({ kind: this.kind, entries: entries(), dryRun: true });
        preview = result;
        clear(previewHost);
        previewHost.append(this.buildPreview(result));
        const writes = countWrites(result);
        applyButton.disabled = writes === 0;
        applyButton.dataset.tip = writes
          ? `Write ${fmt.format(writes)} field${writes === 1 ? '' : 's'}`
          : 'Nothing here would change';
      } catch (error) {
        preview = null;
        applyButton.disabled = true;
        clear(previewHost);
        previewHost.append(el('div', { class: 'str-preview-empty', 'data-tone': 'bad' },
          icon('alert', { size: 15 }), el('span', { text: error.message })));
      }
    }));

    const { close } = modal({
      title: `Bulk ${noun} names`,
      subtitle: `Writes into ${this.headSubject()} · one undo step for the whole batch`,
      width: 'min(96vw, 1000px)',
      body: el('div', { class: 'str-bulk' },
        el('div', { class: 'str-field' },
          el('label', { text: `Ids and ${fields.map((f) => (FIELD_LABELS[f] ?? f).toLowerCase()).join(' and ')}` }),
          paste,
          el('div', { class: 'field-hint',
            text: `One row per line: the id first, then ${fields.map((f) => (FIELD_LABELS[f] ?? f).toLowerCase()).join(', then ')}. ` +
                  'Tab-separated, so a column pasted straight out of a spreadsheet works; a comma or a space ' +
                  'after the id works too when there is only one field to set.' })),
        parsedHost,
        needsGroup
          ? el('div', { class: 'str-field' },
              el('label', { text: this.kind === 'map' ? 'Region for new entries' : 'Category for new entries' }),
              groupSelect,
              el('div', { class: 'field-hint',
                text: groupOptions.length
                  ? 'Applies to every row that has to be created. Left alone, each id is filed where the ids ' +
                    'around it already are.'
                  : 'This archive lists none, so each id is filed where the backend works out it belongs.' }))
          : null,
        el('div', { class: 'str-write-actions' }, previewButton, applyButton),
        previewHost,
        el('div', { class: 'tips' },
          el('b', { text: 'How this behaves' }),
          el('ul', {},
            el('li', { text: 'Preview runs against the server with nothing written, so what you see is exactly what would happen.' }),
            el('li', { text: 'An id with no entry gets one created; an id that already has one is updated in place, even if its key is zero-padded.' }),
            el('li', { text: 'A field already holding that exact text is skipped, and says so — that is not a failure.' }),
            el('li', { text: 'A blank cell leaves that field alone. To empty a field that has text, clear it in the table instead.' }),
            el('li', { text: `This names ids. It does not create the ${noun}s themselves — those live in ${KIND_HOME[this.kind] ?? 'their own archive'}.` }),
            el('li', { text: 'Nothing reaches disk until you save, and the whole batch is one undo.' })))),
      actions: [{ label: 'Close' }],
    });

    applyButton.addEventListener('click', () => this.guard('bulk-apply', async () => {
      if (!preview) return;
      const writes = countWrites(preview);
      const creates = preview.created ?? 0;

      const ok = await confirmDialog({
        title: `Write ${fmt.format(writes)} field${writes === 1 ? '' : 's'} across ${fmt.format(rows.length)} ${noun}${rows.length === 1 ? '' : 's'}?`,
        message: (creates ? `${fmt.format(creates)} of them have no entry yet and one will be created. ` : '') +
                 'This is the preview you are looking at. Nothing is written to disk until you save, and the ' +
                 'whole batch is a single undo.',
        confirmLabel: 'Apply the names',
        danger: false,
      });
      if (!ok) return;

      try {
        const result = await api.stringBulk({ kind: this.kind, entries: entries(), dryRun: false });
        this.app.markDirty();
        close();

        // Rule 3: if N rows were asked for and M were changed, say so and say
        // why the others were not.
        const skipped = result.skipped ?? 0;
        toast(
          `Wrote ${fmt.format(result.applied ?? 0)} field${result.applied === 1 ? '' : 's'}` +
          (result.created ? `, creating ${fmt.format(result.created)} entr${result.created === 1 ? 'y' : 'ies'}` : '') +
          (skipped ? `. ${fmt.format(skipped)} row${skipped === 1 ? ' was' : 's were'} skipped — reopen the preview to see why.` : '.'),
          skipped ? 'warning' : 'success', {
            title: 'Names written',
            action: { label: 'Undo', run: async () => { await this.app.undo(); await this.refresh(); } },
          });
        await this.load();
      } catch (error) {
        toastError(error, 'Could not write those names');
      }
    }));
  }

  /**
   * The before → after table, shared by the single write's preview and the
   * bulk one.
   *
   * Every row is listed, including the ones that would do nothing, with the
   * server's own reason beside them. A preview that hid its skips would be the
   * silent-skip failure with extra steps.
   */
  buildPreview(result, { single = false } = {}) {
    const rows = result.rows || [];
    const writes = countWrites(result);
    const skippedRows = rows.filter((r) => r.skipped).length;
    const skippedFields = rows.reduce((sum, r) => sum + (r.changes || []).filter((c) => c.skipped).length, 0);

    const body = el('tbody');
    for (const row of rows) {
      if (row.skipped) {
        body.append(el('tr', { 'data-skipped': 'true' },
          el('td', { class: 'str-preview-id', text: String(row.id) }),
          el('td', { text: '—' }),
          el('td', { class: 'str-preview-arrow', text: '' }),
          el('td', { text: '—' }),
          el('td', { class: 'str-preview-note', text: row.reason || 'Skipped.' })));
        continue;
      }

      const changes = row.changes || [];
      if (!changes.length) {
        body.append(el('tr', { 'data-skipped': 'true' },
          el('td', { class: 'str-preview-id', text: String(row.id) }),
          el('td', { text: '—' }),
          el('td', { class: 'str-preview-arrow', text: '' }),
          el('td', { text: '—' }),
          el('td', { class: 'str-preview-note', text: 'Nothing was given to write for this row.' })));
        continue;
      }

      changes.forEach((change, index) => {
        const first = index === 0;
        body.append(el('tr', { 'data-skipped': change.skipped ? 'true' : null },
          el('td', { class: 'str-preview-id' },
            first
              ? el('div', {},
                  el('div', { text: String(row.id) }),
                  row.createdEntry
                    ? el('span', { class: 'str-tag', 'data-kind': 'new',
                        text: row.group ? `new · ${row.group}` : 'new entry' })
                    : null)
              : null),
          el('td', {},
            el('span', { class: 'str-preview-field', text: FIELD_LABELS[change.field] ?? change.field }),
            el('span', { class: 'str-preview-text', 'data-empty': change.before == null ? 'true' : null,
              text: change.before == null ? 'not set' : (change.before || 'empty') })),
          el('td', { class: 'str-preview-arrow', text: change.skipped ? '' : '→' }),
          el('td', {},
            el('span', { class: 'str-preview-text', 'data-empty': change.skipped ? 'true' : null,
              text: change.skipped ? '—' : (change.after === '' ? 'empty' : (change.after ?? '—')) })),
          el('td', { class: 'str-preview-note', text: change.skipped ? (change.reason || 'Skipped.') : (change.created ? 'creates this field' : '') })));
      });
    }

    return el('div', {},
      el('div', { class: 'str-preview-head' },
        el('span', {
          text: `${fmt.format(writes)} field${writes === 1 ? '' : 's'} would be written` +
                (result.created ? `, ${fmt.format(result.created)} entr${result.created === 1 ? 'y' : 'ies'} created` : '') +
                (skippedFields || skippedRows
                  ? `, ${fmt.format(skippedFields + skippedRows)} skipped`
                  : ''),
        }),
        single ? null : el('span', { class: 'str-preview-sub', text: `${fmt.format(rows.length)} row${rows.length === 1 ? '' : 's'}` })),
      el('div', { class: 'str-preview-scroll' },
        el('table', { class: 'str-preview' },
          el('thead', {}, el('tr', {},
            ['ID', 'Now', '', 'Would become', 'Note'].map((h) => el('th', { text: h })))),
          body)));
  }
}

/** Fields the server said it would write, across every row of a result. */
function countWrites(result) {
  return (result.rows || []).reduce(
    (sum, row) => sum + (row.skipped ? 0 : (row.changes || []).filter((c) => !c.skipped).length),
    0);
}

/**
 * Turns a pasted block into bulk rows.
 *
 * Tab-separated first, because that is what a spreadsheet column actually puts
 * on the clipboard and it is the only separator that survives a name containing
 * a comma ("Roger, the Instructor"). Only when a line has no tab at all does it
 * fall back to splitting on the first comma or run of spaces after the id, which
 * is what someone typing by hand writes.
 *
 * Lines it cannot read are returned rather than dropped -- the caller says how
 * many and why before anything is sent.
 */
function parseRows(text, fields, max) {
  const rows = [];
  const rejected = [];
  let overflow = 0;

  const lines = text.split(/\r?\n/);
  lines.forEach((raw, index) => {
    const line = raw.trim();
    if (!line) return;

    const cells = line.includes('\t')
      ? raw.split('\t')
      : (line.match(/^(\d+)\s*[,|]?\s*(.*)$/) ?? []).slice(1);

    if (!cells.length) {
      rejected.push({ line: index + 1, reason: 'no id at the start' });
      return;
    }

    const id = Number(String(cells[0]).trim());
    if (!Number.isSafeInteger(id) || id <= 0) {
      rejected.push({ line: index + 1, reason: `"${String(cells[0]).trim().slice(0, 12)}" is not an id` });
      return;
    }

    const values = {};
    fields.forEach((field, position) => {
      const cell = cells[position + 1];
      if (cell == null) return;
      const value = cell.trim();
      // Blank leaves the field alone; there is deliberately no way to empty a
      // field from here, because a stray trailing tab would otherwise wipe one.
      if (value !== '') values[field] = value;
    });

    if (!Object.keys(values).length) {
      rejected.push({ line: index + 1, reason: 'id with no text after it' });
      return;
    }

    if (rows.length >= max) { overflow++; return; }
    rows.push({ id, fields: values });
  });

  return { rows, rejected, overflow };
}

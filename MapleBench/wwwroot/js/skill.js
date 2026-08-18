/**
 * Skills section: a card grid over Skill.wz, plus the level table that is the
 * whole reason this section exists.
 *
 * Built in the same shape as Mobs and Cash Shop -- one class, guard() around
 * every mutation, a head / stats / toolbar / grid / pager stack, and honest
 * empty states -- because sections that do the same kind of work should not feel
 * like different applications.
 *
 * What is genuinely different here is the storage split. A skill's per-level
 * values live either as literal `level/N/damage` nodes or as formulas over the
 * level variable in a `common` block -- and in a v232 client 3,935 of 4,846
 * skills are the second kind, which no other WZ editor renders as levels at all.
 * The level table draws both as one grid and marks every cell with where its
 * value came from: a computed cell is visually distinct and read-only, because
 * typing into it would edit a node the client does not read (rule 6).
 *
 * Every write goes through /api/skill/*, which edits the same session the
 * Explorer edits, so skill changes share one dirty state, one undo history and
 * one save pipeline.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt, modal, confirmDialog, promptForText, debounce, runOnce,
         scalePixelArt, statChip, statRow } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';
import { thumbUrl, lazySprite } from './media.js';

const PAGE_SIZE = 60;

/**
 * Rows of the level table drawn at once. 73 skills in a v232 client declare
 * maxLevel 300, and 300 rows times a dozen columns is 3,600 cells of layout for
 * a screen that shows twenty of them.
 */
const LEVEL_PAGE = 40;

const FILTERS = [
  ['all', 'All'],
  ['formula', 'Formulas'],
  ['explicit', 'Levels'],
  ['mixed', 'Both'],
  ['bad', 'Broken formulas'],
];

const SORTS = [
  ['id', 'Skill ID'],
  ['name', 'Name (A to Z)'],
  ['book', 'Book, then ID'],
  ['level-desc', 'Max level (high to low)'],
  ['bad-desc', 'Broken formulas first'],
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

/** How the API's storage words read on a badge. */
const STORAGE_LABEL = {
  formula: 'Formulas',
  explicit: 'Levels',
  mixed: 'Both',
  none: 'No levels',
};

/** One line saying what each storage shape means, where the badge is not enough. */
const STORAGE_BLURB = {
  formula: 'Every level is computed from the formulas in this skill\'s common block. There are no level nodes.',
  explicit: 'Every level is a real level/N node the client reads directly.',
  mixed: 'This skill has both. The client reads the formulas and ignores the level nodes until the common block is gone.',
  none: 'This skill declares no levels at all.',
};

/** The cell sources that are computed rather than stored, so never editable. */
const COMPUTED = new Set(['formula', 'constant', 'container', 'needs', 'error']);

export class SkillSection {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    /** null means "every open Skill archive", which is what the API does with no fileId. */
    this.fileId = null;

    /**
     * The book being browsed, as its session path -- /api/skill/list matches
     * `book` against the image's path, not against its id, whatever the id looks
     * like. `bookId` is what gets remembered, because a path carries the session
     * file id (f1, f2...) and that is different every run.
     */
    this.bookPath = null;
    this.bookId = localStorage.getItem('mb.skillBook') || null;

    this.books = [];
    this.skills = [];
    this.stats = null;
    this.truncated = false;
    this.capabilities = { available: false, names: false };

    /** The failure that stopped the last load, so the section can say so instead of showing an empty grid. */
    this.error = null;
    /** True while the list is in the air; the first read of a client takes seconds, so it is drawn. */
    this.loading = false;

    this.query = '';
    this.filter = localStorage.getItem('mb.skillFilter') || 'all';
    if (!FILTERS.some(([value]) => value === this.filter)) this.filter = 'all';
    this.sort = localStorage.getItem('mb.skillSort') || 'id';
    this.page = 1;

    /** Session paths of the ticked cards; bulk edit acts on exactly this set. */
    this.selection = new Set();

    /** The open skill dialog, so an external save or undo can repaint it. */
    this.detail = null;
  }

  /**
   * Runs a mutation at most once at a time, reporting anything it throws.
   *
   * Same rule as the other sections: every write is a POST behind a button that
   * stays live while it is in the air, and a bake issued twice would run the
   * second one against a skill whose common block the first one deleted. The
   * keys are namespaced because the in-flight set is shared with the rest of the
   * app.
   */
  async guard(key, run) {
    try {
      await runOnce(`skill:${key}`, run);
    } catch (error) {
      toastError(error);
    }
  }

  async open(fileId = this.fileId) {
    this.fileId = fileId;
    this.page = 1;
    this.render();          // paints whatever we already have while the load runs
    await this.load();
  }

  /** Called after a save or an undo somewhere else changed what is on screen. */
  async refresh() {
    await this.load();
    // A skill card open over the grid is showing values the undo may have moved.
    if (this.detail) await this.detail.reload();
  }

  async load() {
    this.error = null;

    // Set before the FIRST await, not after it.
    //
    // It used to be raised only once capabilities had answered, which left the
    // "No Skill.wz open" screen up for however long that call took -- and the
    // capabilities call takes the session gate and can queue behind a build, so
    // "however long" reached seconds. Telling someone their archive is not open
    // while the app is reading it is the same bug the Mobs section had, just
    // shorter.
    this.loading = true;
    this.render();

    // Capabilities first and on its own: it is the one call that distinguishes
    // "no Skill.wz is open" from "the backend is not answering", and those two
    // deserve completely different screens.
    try {
      this.capabilities = await api.skillCapabilities();
    } catch (error) {
      this.capabilities = { available: false, names: false };
      this.error = error;
      this.loading = false;
      this.render();
      return;
    }

    if (!this.capabilities.available) {
      this.books = [];
      this.skills = [];
      this.stats = null;
      this.selection.clear();
      this.loading = false;
      this.render();
      return;
    }

    try {
      const [books, list] = await Promise.all([
        api.skillBooks(this.fileId),
        api.skillList(this.fileId, this.resolveBook(), this.capabilities.names),
      ]);

      this.books = books.books || [];
      // Resolved after the books arrive, so a remembered book id that this
      // client does not have falls back to every book rather than to an empty
      // grid with a filter the user cannot see.
      this.bookPath = this.resolveBook();

      this.skills = list.skills || [];
      this.stats = list.stats || null;
      this.truncated = Boolean(list.truncated);

      // A reload can drop skills the selection still points at -- narrowing to
      // one book is the common way. Bulk-editing paths that are no longer listed
      // would act on things the user can no longer see.
      this.selection = new Set([...this.selection].filter((path) => this.skills.some((s) => s.path === path)));
    } catch (error) {
      this.error = error;
      this.skills = [];
      this.stats = null;
    } finally {
      this.loading = false;
    }
    this.render();
  }

  /**
   * The `book` argument for the list call: a path, because that is what the
   * endpoint compares against. Null when nothing is chosen or the remembered
   * book is not in this client.
   */
  resolveBook() {
    if (!this.bookId) return null;
    const match = this.books.find((book) => book.bookId === this.bookId);
    return match ? match.path : null;
  }

  setBook(bookId) {
    this.bookId = bookId || null;
    if (this.bookId) localStorage.setItem('mb.skillBook', this.bookId);
    else localStorage.removeItem('mb.skillBook');
    this.selection.clear();
    this.page = 1;
    this.load();
  }

  /* ============================================================
     FILTERING
     ============================================================ */

  get filtered() {
    const needle = this.query.trim().toLowerCase();
    const rows = this.skills.filter((skill) => {
      if (this.filter === 'bad' && !skill.badFormulas) return false;
      if (['formula', 'explicit', 'mixed'].includes(this.filter) && skill.storage !== this.filter) return false;
      if (!needle) return true;
      return String(skill.skillId).includes(needle)
          || (skill.name || '').toLowerCase().includes(needle)
          || (skill.bookName || '').toLowerCase().includes(needle);
    });
    return this.order(rows);
  }

  order(rows) {
    const sorted = [...rows];
    switch (this.sort) {
      case 'name':
        // Skills String.wz does not name sort last rather than clumping at the
        // top under an empty label, which reads as a bug.
        sorted.sort((a, b) => {
          if (!a.name && !b.name) return a.skillId - b.skillId;
          if (!a.name) return 1;
          if (!b.name) return -1;
          return a.name.localeCompare(b.name) || a.skillId - b.skillId;
        });
        break;
      case 'book':
        sorted.sort((a, b) =>
          (a.bookName || a.bookId || '').localeCompare(b.bookName || b.bookId || '') || a.skillId - b.skillId);
        break;
      case 'level-desc':
        sorted.sort((a, b) => (b.maxLevel ?? 0) - (a.maxLevel ?? 0) || a.skillId - b.skillId);
        break;
      case 'bad-desc':
        sorted.sort((a, b) => (b.badFormulas ?? 0) - (a.badFormulas ?? 0) || a.skillId - b.skillId);
        break;
      default:
        sorted.sort((a, b) => a.skillId - b.skillId);
        break;
    }
    return sorted;
  }

  countFor(filter) {
    if (filter === 'bad') return this.skills.filter((s) => s.badFormulas > 0).length;
    if (filter === 'all') return this.skills.length;
    return this.skills.filter((s) => s.storage === filter).length;
  }

  /* ============================================================
     RENDER
     ============================================================ */

  render() {
    clear(this.host);
    this.host.className = 'stage-body skills';

    if (this.error) {
      this.host.append(emptyState(
        'alert', 'Could not read the skill list',
        this.error.message,
        el('div', { class: 'sk-empty-actions' },
          el('button', { class: 'btn btn-primary', onclick: () => this.load() },
            icon('refresh', { size: 15 }), 'Try again'),
          el('button', { class: 'btn', onclick: () => this.app.openFilePicker() },
            icon('folderOpen', { size: 15 }), 'Open a file'))));
      return;
    }

    if (!this.capabilities.available) {
      this.host.append(emptyState(
        'star', 'No Skill.wz open',
        'Skill editing reads Skill.wz and uses String.wz for names. Open the client folder to make '
        + 'both available.',
        el('button', { class: 'btn btn-primary', onclick: () => this.app.openFilePicker() },
          icon('folderOpen', { size: 15 }), 'Open client')));
      return;
    }

    this.resultHost = el('div');

    // Filtered, because this is Element.append rather than el(): append(null)
    // does not skip the child, it puts the word "null" on the page -- and
    // statRow returns null when there is not a single count worth a box.
    this.host.append(...[
      this.buildHead(),
      this.buildStats(),
      this.buildToolbar(),
      this.resultHost,
    ].filter(Boolean));

    this.renderResults();
  }

  /**
   * Names the archive being edited.
   *
   * The list endpoint happily spans every open Skill*.wz, so without this the
   * header would be a page of numbers with no clue which file they will be
   * written back into.
   */
  buildHead() {
    const candidates = this.app.files.filter((file) => /^skill/i.test(file.name));
    const scoped = this.fileId ? this.app.files.find((f) => f.id === this.fileId) : null;

    const subject = scoped
      ? scoped.name
      : candidates.length === 1
        ? candidates[0].name
        : candidates.length > 1
          ? `${candidates.length} Skill archives`
          : 'every open archive';

    const head = el('div', { class: 'sk-panel sk-head' },
      el('div', { class: 'sk-head-text' },
        el('div', { class: 'sk-head-title', text: 'Skills' }),
        el('div', { class: 'sk-head-sub' },
          el('span', { text: 'Editing ' }),
          el('b', { text: subject }),
          el('span', {
            text: this.stats
              ? ` · ${fmt.format(this.stats.total)} skill${this.stats.total === 1 ? '' : 's'}` +
                ` in ${fmt.format(this.stats.books)} book${this.stats.books === 1 ? '' : 's'}`
              : '',
          }),
          this.capabilities.names
            ? null
            : el('span', { class: 'sk-head-warn', text: ' · String.wz is not open, so skills have no names' }))));

    // The picker only earns its place when there is a real choice to make.
    if (candidates.length > 1) {
      const select = el('select', { class: 'sk-select', 'aria-label': 'Which archive to edit' },
        el('option', { value: '', text: `All Skill archives (${candidates.length})`, selected: !this.fileId }),
        candidates.map((file) => el('option', { value: file.id, text: file.name, selected: file.id === this.fileId })));
      select.addEventListener('change', () => {
        this.selection.clear();
        this.open(select.value || null);
      });
      head.append(select);
    }

    head.append(el('button', {
      class: 'btn btn-icon', 'data-tip': 'Reload the skill list',
      onclick: () => this.load(),
    }, icon('refresh', { size: 15 })));

    return head;
  }

  /**
   * Six counts, of which only the ones that happened are shown.
   *
   * `null` still prints an em dash -- a row of zeros over a client that has not
   * been read yet reads as corrupt data -- but a real zero now drops the box
   * instead of filling it. On an ordinary client "Both" and "Broken formulas"
   * are both zero, so two of the six boxes were there to report that nothing
   * was wrong, in the same row and the same weight as the four that carry the
   * shape of the archive. When one of them is not zero it now appears, which is
   * the only time it is worth reading.
   */
  buildStats() {
    const s = this.stats;
    const n = (value) => (typeof value === 'number' ? value : null);

    return statRow('sk-stats', [
      statChip('Skills', n(s?.total), { drop: false }),
      statChip('Books', n(s?.books), { drop: false }),
      statChip('Formula-only', n(s?.formulaDriven), { tone: 'purple',
        tip: 'Their per-level values exist only as expressions over the level. Most of a client is this.' }),
      statChip('Explicit levels', n(s?.explicitLevels), { tone: 'green',
        tip: 'Their values are real level/N nodes the client reads directly.' }),
      statChip('Both', n(s?.mixed), { tone: 'amber',
        tip: 'A common block AND level nodes. The client reads the formulas and ignores the nodes.' }),
      statChip('Broken formulas', n(s?.badFormulas), { tone: 'red',
        tip: 'Expressions this build could not parse. They are reported with the error rather than shown as 0.' }),
    ]);
  }

  buildToolbar() {
    const search = el('input', {
      type: 'search', value: this.query,
      placeholder: this.capabilities.names
        ? 'Search by skill ID, name or book…'
        : 'Search by skill ID — open String.wz for names',
      'aria-label': 'Search skills',
    });
    // Only the results are re-rendered, never the toolbar holding this input:
    // rebuilding it destroys the caret and drops IME composition mid-word.
    search.addEventListener('input', debounce(() => {
      this.query = search.value;
      this.page = 1;
      this.renderResults();
    }, 160));
    this.searchInput = search;

    const chips = el('div', { class: 'cat-tabs' },
      FILTERS.map(([value, label]) => el('button', {
        class: 'cat-tab', 'aria-pressed': this.filter === value ? 'true' : 'false',
        onclick: () => {
          this.filter = value;
          localStorage.setItem('mb.skillFilter', value);
          this.page = 1;
          this.render();
        },
      }, label, el('span', { class: 'cat-count', text: fmt.format(this.countFor(value)) }))));

    const books = el('select', { class: 'sk-select', 'aria-label': 'Which skill book to browse' },
      el('option', {
        value: '', selected: !this.bookId,
        text: `Every book (${fmt.format(this.books.length)})`,
      }),
      // Named, not numbered: "1100" is meaningless and "The Basics of a Dawn
      // Warrior" is the thing the user is looking for (rule 1). The id stays
      // beside it because that is what the path keys on.
      this.books.map((book) => el('option', {
        value: book.bookId, selected: book.bookId === this.bookId,
        text: `${book.name || `Book ${book.bookId}`} · ${book.bookId} · ${fmt.format(book.skillCount)}`,
      })));
    books.addEventListener('change', () => this.setBook(books.value));

    const sort = el('select', { class: 'sk-select', 'aria-label': 'Sort skills' },
      SORTS.map(([value, label]) => el('option', { value, text: label, selected: value === this.sort })));
    sort.addEventListener('change', () => {
      this.sort = sort.value;
      localStorage.setItem('mb.skillSort', sort.value);
      this.page = 1;
      this.renderResults();
    });

    return el('div', { class: 'sk-panel' },
      el('div', { class: 'sk-toolbar' },
        el('div', { class: 'sk-search' }, el('span', { class: 'icon' }, icon('search', { size: 15 })), search),
        books,
        sort),
      el('div', { class: 'sk-toolbar' }, chips),
      this.buildSelectionBar(),
      this.truncated
        ? el('div', { class: 'sk-note' }, icon('alert', { size: 14 }),
            el('span', { text: 'The archive holds more skills than one listing returns, so this is a partial ' +
                               'list. Pick a single book, or search, to be sure you are seeing everything.' }))
        : null);
  }

  buildSelectionBar() {
    const shown = this.filtered;
    const count = this.selection.size;

    return el('div', { class: 'sk-selbar', 'data-active': count ? 'true' : 'false' },
      el('span', { class: 'sk-selcount', text: count ? `${fmt.format(count)} selected` : 'Nothing selected' }),
      el('button', {
        class: 'btn btn-sm', disabled: !shown.length,
        onclick: () => {
          for (const skill of shown) this.selection.add(skill.path);
          this.render();
        },
      }, icon('check', { size: 14 }), `Select all ${fmt.format(shown.length)}`),
      el('button', {
        class: 'btn btn-sm', disabled: !count,
        onclick: () => { this.selection.clear(); this.render(); },
      }, icon('close', { size: 14 }), 'Clear'),
      el('button', {
        class: 'btn btn-sm btn-primary', disabled: !count,
        onclick: () => this.openBulkDialog(),
      }, icon('sliders', { size: 14 }), count ? `Bulk edit ${fmt.format(count)}` : 'Bulk edit'));
  }

  /** Puts the caret in the search box; for a palette command. */
  focusSearch() {
    this.searchInput?.focus();
    this.searchInput?.select();
  }

  /** Redraws only the result panel, leaving the search box untouched. */
  renderResults() {
    if (!this.resultHost) { this.render(); return; }
    // The icons the outgoing page still has queued belong to nobody now. One
    // scope per repaint; see lazySprite in media.js.
    this.sprites?.abort();
    this.sprites = new AbortController();
    clear(this.resultHost);

    const panel = el('div', { class: 'sk-panel' });

    if (this.loading) {
      panel.append(el('div', { class: 'sk-result-head' },
        el('span', { text: 'Reading every skill book…' }),
        el('span', { text: 'about 8 seconds the first time, instant after that' })));
      for (let i = 0; i < 6; i++) {
        panel.append(el('div', { class: 'skeleton skeleton-row', style: `width:${45 + ((i * 37) % 40)}%` }));
      }
      this.resultHost.append(panel);
      return;
    }

    const results = this.filtered;
    const pages = Math.max(1, Math.ceil(results.length / PAGE_SIZE));
    this.page = Math.min(this.page, pages);
    const slice = results.slice((this.page - 1) * PAGE_SIZE, this.page * PAGE_SIZE);

    panel.append(el('div', { class: 'sk-result-head' },
      el('span', { text: `${fmt.format(results.length)} skill${results.length === 1 ? '' : 's'}` +
                         (this.filter === 'all' ? '' : ` · ${FILTERS.find(([v]) => v === this.filter)[1].toLowerCase()}`) }),
      el('span', { text: `Page ${this.page} of ${pages}` })));

    if (!this.skills.length) {
      // The archive is open and readable and holds nothing -- a different fact
      // from "your filter hid everything", and it needs a different sentence.
      panel.append(emptyState(
        'star',
        this.bookId ? 'No skills in this book' : 'No skills in this archive',
        this.bookId
          ? 'That book opened but has nothing under its skill node. Switch back to every book.'
          : 'The archive opened, but nothing in it looks like a skill book — a book is an .img with a ' +
            '"skill" node inside it. If you scoped to one file, try the others.',
        this.bookId
          ? el('button', { class: 'btn', onclick: () => this.setBook(null) },
              icon('layers', { size: 15 }), 'Show every book')
          : el('button', { class: 'btn', onclick: () => this.load() },
              icon('refresh', { size: 15 }), 'Reload')));
    } else if (!results.length) {
      panel.append(emptyState(
        'search',
        this.query ? `No skills match "${this.query}"` : 'Nothing in this filter',
        this.query
          ? (this.capabilities.names
              ? 'Try a skill ID like 1001, part of a name, or the name of a job book.'
              : 'String.wz is not open, so skills have no names here — search by ID instead.')
          : this.filter === 'bad'
            ? 'Every formula in this archive parsed cleanly. That is the good outcome.'
            : 'Every skill here is filtered out. Switch back to All.',
        this.query
          ? el('button', { class: 'btn', text: 'Clear search', onclick: () => { this.query = ''; this.render(); } })
          : el('button', { class: 'btn', text: 'Show all skills',
              onclick: () => { this.filter = 'all'; localStorage.setItem('mb.skillFilter', 'all'); this.render(); } })));
    } else {
      const grid = el('div', { class: 'sk-grid' });
      for (const skill of slice) grid.append(this.buildCard(skill));
      panel.append(grid);
      if (pages > 1) panel.append(this.buildPager(pages));
    }

    this.resultHost.append(panel);
  }

  buildCard(skill) {
    const selected = this.selection.has(skill.path);
    const card = el('div', {
      class: 'sk-card', 'data-path': skill.path, 'data-skill-id': skill.skillId,
      'data-selected': selected ? 'true' : 'false',
    });

    const box = el('input', {
      type: 'checkbox', checked: selected,
      'aria-label': `Select ${skill.name || `skill ${skill.skillId}`}`,
    });
    box.addEventListener('change', () => {
      if (box.checked) this.selection.add(skill.path); else this.selection.delete(skill.path);
      card.dataset.selected = box.checked ? 'true' : 'false';
      // Only the counter and the bulk button change, so the grid is left alone
      // -- repainting it here would throw away the checkbox that was clicked.
      this.refreshSelectionBar();
    });

    const badges = el('div', { class: 'sk-badges' },
      el('span', { class: 'sk-badge', 'data-kind': skill.storage, text: STORAGE_LABEL[skill.storage] ?? skill.storage }),
      skill.badFormulas
        ? el('span', {
            class: 'sk-badge', 'data-kind': 'bad',
            'data-tip': 'A formula in this skill could not be parsed. Open it to see the error.',
            text: `${fmt.format(skill.badFormulas)} broken`,
          })
        : null,
      skill.passive ? el('span', { class: 'sk-badge', 'data-kind': 'passive', text: 'Passive' }) : null,
      skill.invisible ? el('span', { class: 'sk-badge', 'data-kind': 'hidden', text: 'Hidden' }) : null,
      skill.dirty ? el('span', { class: 'sk-badge', 'data-kind': 'dirty', text: 'Unsaved' }) : null);

    // A formula is shown as the expression it is, in mono. Rounding it into a
    // number would be inventing a level to show it at.
    const expression = (label, value) => (value
      ? el('div', { class: 'sk-row' },
          el('span', { class: 'k', text: label }),
          el('code', { class: 'v', text: value }))
      : null);

    const stored = skill.storage === 'formula' ? '' : ' at level 1';

    card.append(...[
      el('div', { class: 'sk-card-head' },
        el('label', { class: 'sk-pick' }, box),
        skillIcon(skill.path, 40, this.sprites?.signal),
        el('div', { class: 'sk-card-title' },
          el('button', {
            class: 'sk-title', text: skill.name || `Skill ${skill.skillId}`,
            'data-tip': 'Open the level table', onclick: () => this.openSkillCard(skill),
          }),
          el('div', { class: 'sk-sub', text: `ID ${skill.skillId} · ${skill.bookName || `book ${skill.bookId}`}` })),
        el('span', {
          class: 'sk-level', 'data-tip': 'Levels this skill declares',
          // Never 0: the API sends null when nothing declares a level count, and
          // "Lv 0" would read as a fact rather than as an absence.
          text: skill.maxLevel ? `Lv 1–${fmt.format(skill.maxLevel)}` : 'Lv —',
        })),
      badges,
      expression(`Damage${stored}`, skill.damage),
      expression(`MP cost${stored}`, skill.mpCon),
      expression(`Cooldown${stored}`, skill.cooltime),
      el('div', { class: 'sk-row' },
        el('span', { class: 'k', text: 'Stored levels' }),
        el('span', { class: 'v', text: skill.levelCount
          ? `${fmt.format(skill.levelCount)} node${skill.levelCount === 1 ? '' : 's'}`
          : 'none — computed' })),
      el('div', { class: 'sk-actions' },
        el('button', { class: 'btn btn-sm', style: 'flex:1', onclick: () => this.openSkillCard(skill) },
          icon('grid4', { size: 14 }), 'Levels'),
        el('button', {
          class: 'btn btn-sm btn-icon', 'data-tip': 'More',
          onclick: (event) => {
            event.stopPropagation();
            const rect = event.currentTarget.getBoundingClientRect();
            this.app.showMenu(this.menuItemsFor(skill), rect.left, rect.bottom + 4);
          },
        }, icon('more', { size: 15 }))),
    ].filter(Boolean));

    return card;
  }

  /** Shared by the card overflow and the app-wide right-click menu. */
  menuItemsFor(skill) {
    return [
      { icon: 'grid4', label: 'Open the level table…', run: () => this.openSkillCard(skill) },
      skill.storage === 'formula' || skill.storage === 'mixed'
        ? { icon: 'sliders', label: 'Bake the formulas into levels…', run: () => this.openSkillCard(skill, { bake: true }) }
        : null,
      { icon: 'copy', label: `Copy skill ID ${skill.skillId}`, run: () => this.app.copyText(String(skill.skillId)) },
      { icon: 'externalLink', label: 'Show in Explorer', run: () => {
        this.app.setMode('explorer');
        this.app.navigate(skill.path);
      } },
    ].filter(Boolean);
  }

  /** Repaints the selection bar in place; see buildCard for why not the grid. */
  refreshSelectionBar() {
    const current = this.host.querySelector('.sk-selbar');
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

  /* ============================================================
     THE SKILL CARD
     ============================================================ */

  // Guarded because the detail is fetched *before* modal() runs: two fast clicks
  // would otherwise race, and the second card's commit closures would point at a
  // form the first one had already replaced.
  openSkillCard(skill, options = {}) {
    return this.guard(`card:${skill.path}`, () => this.buildSkillCard(skill, options));
  }

  async buildSkillCard(skill, { bake = false } = {}) {
    let detail;
    try {
      detail = await api.skillDetail(skill.path);
    } catch (error) {
      toastError(error, 'Could not read that skill');
      return;
    }

    const ctx = {
      skill,
      detail,
      levelPage: 1,
      touched: false,
      /**
       * Values the user has typed for free variables, by name. Kept on the card
       * rather than in the archive: they are what the client would supply at
       * runtime, so writing them anywhere would be inventing data.
       */
      vars: {},
      /** { level, key } of the cell to put the caret back in after a commit. */
      focus: null,
      close: null,
    };
    this.detail = ctx;

    const body = el('div', { class: 'sk-detail' });

    const paint = () => {
      clear(body);
      body.append(...[
        this.buildDetailHead(ctx),
        ctx.detail.warning ? this.buildTrapStrip(ctx) : null,
        ctx.detail.variables?.length ? this.buildVariableStrip(ctx) : null,
        this.buildFormulaPanel(ctx),
        this.buildLevelTable(ctx),
        this.buildFieldPanel(ctx),
      ].filter(Boolean));

      // A commit repaints the whole body, which would otherwise pull the caret
      // out of the cell the user pressed Enter in.
      if (ctx.focus) {
        const { level, key } = ctx.focus;
        ctx.focus = null;
        const back = body.querySelector(`[data-cell="${CSS.escape(`${level}:${key}`)}"] input`);
        if (back) { back.focus(); back.select?.(); }
      }
    };

    /** Re-reads the skill from the server and repaints. Every write ends here. */
    ctx.reload = async (next) => {
      ctx.detail = next ?? await api.skillDetail(ctx.skill.path, ctx.vars);
      // The card behind the dialog reads the list DTO, and only the detail knows
      // what was just written -- without this, baking a skill leaves "Formulas"
      // on the card until the next full reload.
      ctx.skill.storage = ctx.detail.storage;
      ctx.skill.maxLevel = ctx.detail.maxLevel;
      ctx.skill.levelCount = ctx.detail.levels.filter((row) => row.present).length;
      ctx.skill.dirty = ctx.detail.dirty ?? ctx.skill.dirty;
      paint();
    };

    paint();

    const { dialog, close } = modal({
      title: ctx.detail.name || `Skill ${ctx.detail.skillId}`,
      subtitle: `${ctx.detail.bookName || `Book ${ctx.detail.bookId}`} · ${ctx.detail.path.split('/').slice(1).join('/')}`,
      width: 'min(97vw, 1180px)',
      body,
      actions: [{ label: 'Done' }],
    });
    // Handed to the head and the strips, all of which are painted before the
    // dialog that owns them exists.
    ctx.close = close;

    dialog.addEventListener('close', () => {
      if (this.detail === ctx) this.detail = null;
      // Committed cells change the numbers on the card behind this dialog, and
      // the list is the only thing that knows them. Reloading once on close
      // beats a reload per keystroke.
      if (ctx.touched) this.load();
    }, { once: true });

    if (bake) this.openBakeDialog(ctx);
  }

  buildDetailHead(ctx) {
    const d = ctx.detail;
    const levels = d.levels.filter((row) => row.present).length;

    return el('div', { class: 'sk-detail-head' },
      skillIcon(d.path, 52),
      el('div', { class: 'sk-detail-id' },
        el('div', { class: 'sk-detail-name', text: d.name || `Skill ${d.skillId}` }),
        el('div', { class: 'sk-sub', text: `ID ${d.skillId} · ${STORAGE_BLURB[d.storage] ?? ''}` })),
      el('span', { class: 'sk-badge', 'data-kind': d.storage, text: STORAGE_LABEL[d.storage] ?? d.storage }),
      el('span', {
        class: 'sk-level',
        text: d.maxLevel ? `Lv 1–${fmt.format(d.maxLevel)}` : 'Lv —',
      }),
      el('span', { class: 'sk-sub', text: `${fmt.format(levels)} stored level${levels === 1 ? '' : 's'}` }),
      el('span', { style: 'margin-left:auto' }),
      d.hasCommon
        ? el('button', {
            class: 'btn btn-sm btn-primary',
            'data-tip': 'Turn the formulas into real level nodes',
            onclick: () => this.openBakeDialog(ctx),
          }, icon('sliders', { size: 14 }), 'Bake into levels…')
        : null,
      el('button', {
        class: 'btn btn-sm', 'data-tip': 'Open this skill in the Explorer',
        // Closing first: leaving the card sitting over the Explorer hides the
        // node it just navigated to, which reads as the button doing nothing.
        onclick: () => {
          ctx.close?.();
          this.app.setMode('explorer');
          this.app.navigate(ctx.detail.path);
        },
      }, icon('externalLink', { size: 14 }), 'Show in Explorer'));
  }

  /**
   * The trap this section exists to stop, stated where it is sprung.
   *
   * While a `common` block exists the client computes every level from the
   * formulas and never looks at `level/`, so adding `level/1/damage` to such a
   * skill writes fine, turns the cell green, and changes precisely nothing in
   * game. The server sends the sentence; this puts it above the table it is
   * about.
   */
  buildTrapStrip(ctx) {
    return el('div', { class: 'sk-trap' },
      icon('alert', { size: 16 }),
      el('div', { class: 'sk-trap-text' },
        el('b', { text: 'The client reads the formulas, not the level nodes. ' }),
        el('span', { text: ctx.detail.warning })),
      el('button', {
        class: 'btn btn-sm',
        onclick: () => this.openBakeDialog(ctx),
      }, icon('sliders', { size: 14 }), 'Bake into levels…'));
  }

  /* ============================================================
     FREE VARIABLES
     ============================================================ */

  /**
   * Inputs for the names a formula reads that this skill does not define.
   *
   * MapleStory formulas are not confined to the level variable. Most names
   * resolve inside the skill's own common block -- "damage = 140+y" reads the
   * y sitting next to it -- and those never reach here. What does reach here is
   * a name the archive genuinely does not carry, like the x30 in "200+4*x30",
   * which the client knows from somewhere this editor cannot see.
   *
   * The alternatives were both worse than asking. Refusing the formula calls
   * the game's own data invalid, which it is not. Assuming 0 fills the column
   * with numbers that look computed and are not, and a wrong damage table that
   * looks right is the failure this whole screen is built to avoid.
   *
   * Nothing typed here is written anywhere. It changes what the preview
   * computes and stops when the card closes.
   */
  buildVariableStrip(ctx) {
    const strip = el('div', { class: 'sk-varbar' });

    strip.append(el('div', { class: 'sk-varbar-lead' },
      icon('info', { size: 14 }),
      el('span', {
        text: ctx.detail.variables.length === 1
          ? 'One value in these formulas comes from the client, not the archive:'
          : 'Some values in these formulas come from the client, not the archive:',
      })));

    for (const variable of ctx.detail.variables) {
      const input = el('input', {
        class: 'sk-input sk-var-input',
        value: ctx.vars[variable.name] ?? variable.value ?? '',
        placeholder: '?',
        spellcheck: 'false',
        inputmode: 'decimal',
        'aria-label': `Value for ${variable.name}`,
      });

      // Committed on blur and Enter rather than on every keystroke: each commit
      // is a round trip that repaints the table, and doing that per character
      // makes the box impossible to type in.
      const commit = () => {
        const text = input.value.trim();
        const next = text === '' ? undefined : Number(text);
        if (text !== '' && !Number.isFinite(next)) {
          toast(`"${text}" is not a number.`, 'warn');
          input.value = ctx.vars[variable.name] ?? '';
          return;
        }
        if (ctx.vars[variable.name] === next) return;
        if (next === undefined) delete ctx.vars[variable.name];
        else ctx.vars[variable.name] = next;
        ctx.reload().catch((error) => toastError(error, 'Could not recompute the table'));
      };
      input.addEventListener('blur', commit);
      input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') { event.preventDefault(); commit(); }
        if (event.key === 'Escape') { input.value = ctx.vars[variable.name] ?? ''; input.blur(); }
      });

      strip.append(el('label', { class: 'sk-var' },
        el('code', { class: 'sk-var-name', text: variable.name }),
        input));
    }

    return strip;
  }

  /* ============================================================
     FORMULAS
     ============================================================ */

  /**
   * The formulas, readable and editable.
   *
   * For 81% of a client this block *is* the skill's per-level data -- the level
   * table below is a rendering of it. Editing one here changes every level at
   * once, which is both the fast way to retune a skill and the only edit the
   * client will actually read while the common block exists.
   */
  buildFormulaPanel(ctx) {
    const columns = ctx.detail.columns.filter((column) => column.formula != null);
    if (!columns.length) return null;

    const bad = columns.filter((column) => column.source === 'error').length;

    const list = el('div', { class: 'sk-formulas' });
    for (const column of columns) list.append(this.buildFormulaRow(ctx, column));

    // The dialect comes from the capabilities call rather than from a constant
    // here, so the help under the boxes cannot drift from the parser that has
    // to accept what is typed into them.
    const formula = this.capabilities.formula || {};

    const details = el('details', { class: 'sk-section', open: true });
    // Filtered, because this is Element.append rather than el(): append(null)
    // does not skip the child, it stringifies it and puts the word "null" on
    // the panel.
    details.append(...[
      el('summary', {},
        el('span', { class: 'sk-section-name', text: 'Formulas' }),
        el('span', { class: 'sk-section-meta',
          text: `${columns.length} entr${columns.length === 1 ? 'y' : 'ies'} in common · x is the level` }),
        bad
          ? el('span', { class: 'sk-section-bad', text: `${bad} could not be parsed` })
          : null),
      list,
      el('div', { class: 'sk-hint',
        text: `Expressions over ${formula.variable || 'x'}, the level. Operators ` +
              `${formula.operators || '+ - * / % ( )'} and the functions ` +
              `${(formula.functions || ['u', 'd', 'min', 'max']).join(', ')} ` +
              '(u rounds up, d rounds down). Editing one here changes every level at once.' }),
      ctx.detail.hasPvpCommon
        ? el('div', { class: 'sk-hint',
            text: 'This skill also has a PVPcommon block with its own formulas. It is not shown or ' +
                  'touched here — open it in the Explorer.' })
        : null,
    ].filter(Boolean));
    return details;
  }

  buildFormulaRow(ctx, column) {
    const editable = column.formulaPath && column.source !== 'container';

    const row = el('div', { class: 'sk-formula', 'data-source': column.source });
    row.append(el('div', { class: 'sk-formula-label' },
      el('span', { class: 'sk-formula-name', text: column.label || column.key }),
      el('code', { class: 'sk-key', text: column.key }),
      column.unit ? el('span', { class: 'sk-unit', text: column.unit }) : null,
      column.source === 'constant'
        ? el('span', { class: 'sk-tag', 'data-tone': 'flat', text: 'same at every level' })
        : null,
      column.source === 'container'
        ? el('span', { class: 'sk-tag', 'data-tone': 'flat', text: 'a group of values' })
        : null));

    if (!editable) {
      row.append(el('code', { class: 'sk-formula-static', text: column.formula ?? '' }));
      row.append(el('div', { class: 'sk-hint',
        text: 'This holds a group of values rather than one expression. Open it in the Explorer to edit ' +
              'what is inside it.' }));
      return row;
    }

    const input = el('input', {
      class: 'sk-input sk-mono', value: column.formula ?? '', spellcheck: 'false',
      'aria-label': `${column.label || column.key} formula`,
    });
    const committed = column.formula ?? '';

    const send = () => {
      if (input.value === committed) return;
      this.commitFormula(ctx, column, input.value, () => { input.value = committed; });
    };
    input.addEventListener('blur', send);
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); send(); }
      else if (event.key === 'Escape') { event.preventDefault(); input.value = committed; input.blur(); }
    });

    row.append(input);

    if (column.formulaError) {
      row.append(el('div', { class: 'sk-formula-error' },
        icon('alert', { size: 13 }),
        el('span', { text: column.formulaError })));
    }
    if (column.hint) row.append(el('div', { class: 'sk-hint', text: column.hint }));
    return row;
  }

  /**
   * Writes one `common/<key>` expression.
   *
   * Through /api/node/value rather than a skill-shaped endpoint because a
   * formula is one WZ string and the detail hands over its exact path for
   * precisely this. It is still WzEditService underneath, so it shares the one
   * dirty state and one undo history like everything else (rule 5).
   */
  commitFormula(ctx, column, value, revert) {
    return this.guard(`formula:${column.formulaPath}`, async () => {
      try {
        await api.setValue(column.formulaPath, value);
        ctx.touched = true;
        this.app.markDirty();
        // Re-read rather than patch: one formula decides a whole column of the
        // table below, and maxLevel decides how many rows there are.
        await ctx.reload();
        toast(`${column.label || column.key} is now ${value || 'empty'} for every level.`, 'success', {
          action: { label: 'Undo', run: async () => { await this.app.undo(); await this.refresh(); } },
        });
      } catch (error) {
        revert?.();
        toastError(error, 'Could not save that formula');
      }
    });
  }

  /* ============================================================
     THE LEVEL TABLE
     ============================================================ */

  buildLevelTable(ctx) {
    const d = ctx.detail;
    const section = el('div', { class: 'sk-section' });
    // Held, so paging can replace exactly this node. Finding it by selector
    // would pick the wrong table the moment a second dialog is open over it.
    ctx.tableHost = section;

    if (!d.columns.length || !d.levels.length) {
      section.append(el('div', { class: 'sk-section-bar' },
        el('span', { class: 'sk-section-name', text: 'Levels' })));
      section.append(emptyState(
        'grid4', 'This skill has no levels',
        'Nothing declares a maxLevel and there are no level nodes, so there is no table to draw. ' +
        'Adding level 1 creates the first one.',
        el('button', {
          class: 'btn btn-primary',
          onclick: () => this.levelOp(ctx, { op: 'add', level: 1 }, 'Added level 1'),
        }, icon('plus', { size: 15 }), 'Add level 1')));
      return section;
    }

    const pages = Math.max(1, Math.ceil(d.levels.length / LEVEL_PAGE));
    ctx.levelPage = Math.min(Math.max(1, ctx.levelPage), pages);
    const slice = d.levels.slice((ctx.levelPage - 1) * LEVEL_PAGE, ctx.levelPage * LEVEL_PAGE);

    const jump = el('input', {
      class: 'sk-input sk-jump', type: 'number', min: '1', placeholder: 'Level…',
      'aria-label': 'Jump to a level',
    });
    jump.addEventListener('keydown', (event) => {
      if (event.key !== 'Enter') return;
      event.preventDefault();
      const wanted = Number(jump.value);
      const index = d.levels.findIndex((row) => row.level === wanted);
      if (index < 0) { toast(`This skill has no level ${jump.value}.`, 'warning'); return; }
      ctx.levelPage = Math.floor(index / LEVEL_PAGE) + 1;
      this.repaintTable(ctx);
    });

    section.append(el('div', { class: 'sk-section-bar' },
      el('span', { class: 'sk-section-name', text: 'Levels' }),
      el('span', { class: 'sk-section-meta',
        text: `${fmt.format(d.levels.length)} row${d.levels.length === 1 ? '' : 's'} · ` +
              `${fmt.format(d.columns.length)} field${d.columns.length === 1 ? '' : 's'}` +
              (pages > 1 ? ` · showing ${slice[0].level}–${slice[slice.length - 1].level}` : '') }),
      el('span', { style: 'margin-left:auto' }),
      jump,
      el('button', {
        class: 'btn btn-sm',
        'data-tip': 'Create the next level node',
        onclick: () => this.addNextLevel(ctx),
      }, icon('plus', { size: 14 }), 'Add level')));

    if (d.truncated) {
      section.append(el('div', { class: 'sk-note' }, icon('alert', { size: 14 }),
        el('span', { text: `This skill declares more levels than the server will build a table for. ` +
                           'The rows below stop short of its maxLevel.' })));
    }

    section.append(this.buildTable(ctx, slice));

    if (pages > 1) {
      const pager = el('div', { class: 'pager' });
      const go = (page) => { ctx.levelPage = page; this.repaintTable(ctx); };
      pager.append(el('button', { text: '‹ Previous', disabled: ctx.levelPage === 1, onclick: () => go(ctx.levelPage - 1) }));
      const numbers = new Set([1, pages, ctx.levelPage, ctx.levelPage - 1, ctx.levelPage + 1]);
      let previous = 0;
      for (const page of [...numbers].filter((p) => p >= 1 && p <= pages).sort((a, b) => a - b)) {
        if (page - previous > 1) pager.append(el('span', { text: '…', style: 'color:var(--text-3)' }));
        const first = d.levels[(page - 1) * LEVEL_PAGE].level;
        const last = d.levels[Math.min(page * LEVEL_PAGE, d.levels.length) - 1].level;
        pager.append(el('button', {
          text: `${first}–${last}`, 'aria-current': page === ctx.levelPage ? 'true' : 'false',
          onclick: () => go(page),
        }));
        previous = page;
      }
      pager.append(el('button', { text: 'Next ›', disabled: ctx.levelPage === pages, onclick: () => go(ctx.levelPage + 1) }));
      section.append(pager);
    }

    section.append(el('div', { class: 'sk-legend' },
      el('span', { class: 'sk-legend-item' },
        el('span', { class: 'sk-swatch', 'data-source': 'explicit' }), 'stored — editable'),
      el('span', { class: 'sk-legend-item' },
        el('span', { class: 'sk-swatch', 'data-source': 'formula' }), 'computed from a formula — read-only'),
      el('span', { class: 'sk-legend-item' },
        el('span', { class: 'sk-swatch', 'data-source': 'constant' }), 'the same at every level'),
      el('span', { class: 'sk-legend-item' },
        el('span', { class: 'sk-swatch', 'data-source': 'missing' }), 'not set — typing creates it'),
      el('span', { class: 'sk-legend-item' },
        el('span', { class: 'sk-swatch', 'data-source': 'needs' }), 'waiting on a value from the client'),
      el('span', { class: 'sk-legend-item' },
        el('span', { class: 'sk-swatch', 'data-source': 'error' }), 'unreadable formula')));

    return section;
  }

  /** Repaints only the table, so paging does not disturb the formula boxes. */
  repaintTable(ctx) {
    const current = ctx.tableHost;
    if (!current?.isConnected) return;
    current.replaceWith(this.buildLevelTable(ctx));
  }

  buildTable(ctx, rows) {
    const columns = ctx.detail.columns;

    const head = el('tr', {},
      el('th', { class: 'sk-th-level', text: 'Level' }),
      columns.map((column) => el('th', { 'data-source': column.source },
        el('span', { class: 'sk-th-label', text: column.label || column.key }),
        // The raw key under the label: a write has to match that spelling, and
        // someone who knows the WZ needs to see it (rule 1, both halves).
        el('code', { class: 'sk-th-key', text: column.key + (column.unit ? ` (${column.unit})` : '') }),
        column.formula != null
          ? el('code', {
              class: 'sk-th-formula',
              'data-tip': column.source === 'error'
                ? `This formula could not be parsed: ${column.formulaError}`
                : `common/${column.key} = ${column.formula}`,
              text: column.formula,
            })
          : null)));

    const body = el('tbody');
    for (const row of rows) {
      const tr = el('tr', { 'data-present': row.present ? 'true' : 'false' });
      tr.append(el('th', { class: 'sk-th-level', scope: 'row' },
        el('button', {
          class: 'sk-level-btn',
          'data-tip': row.present
            ? `level/${row.level} exists. Copy, remove or renumber it.`
            : `There is no level/${row.level} node — this row is computed. Level actions.`,
          onclick: (event) => {
            const rect = event.currentTarget.getBoundingClientRect();
            this.app.showMenu(this.levelMenu(ctx, row), rect.left, rect.bottom + 4);
          },
        }, String(row.level)),
        row.present ? null : el('span', { class: 'sk-ghost', 'data-tip': 'No node for this level', text: '·' })));

      for (const column of columns) {
        const cell = row.cells.find((c) => c.key === column.key);
        tr.append(this.buildCell(ctx, row, column, cell));
      }
      body.append(tr);
    }

    return el('div', { class: 'sk-table-scroll' },
      el('table', { class: 'sk-table' }, el('thead', {}, head), body));
  }

  /**
   * One cell.
   *
   * The whole point of the screen is in the branch: a computed cell is a
   * rendering of an expression, not a stored value, so it is drawn as text with
   * the expression behind it and there is no input to type into. Making it an
   * input that silently discarded the edit -- or worse, wrote a level node the
   * client ignores -- is the failure this section exists to prevent.
   */
  buildCell(ctx, row, column, cell) {
    const source = cell?.source ?? 'missing';
    const td = el('td', { 'data-source': source, 'data-cell': `${row.level}:${column.key}` });

    if (COMPUTED.has(source)) {
      // Waiting on a value, not broken. Drawn quietly and pointing at the box
      // that fixes it, because a red cell down thirty rows reads as "this skill
      // is damaged" when the formula is perfectly good.
      if (source === 'needs') {
        td.append(el('span', {
          class: 'sk-cell-needs',
          'data-tip': cell?.error
            || `This formula reads ${(column.needs || []).join(', ')}, which the archive does not carry. `
               + 'Type a value at the top of the card and the column fills in.',
          text: (column.needs || []).join(', ') || '?',
        }));
        return td;
      }

      if (source === 'error') {
        td.append(el('span', {
          class: 'sk-cell-bad', 'data-tip': cell?.error || column.formulaError || 'This formula could not be read.',
          // No number at all. A 0 here is indistinguishable from a real 0, and
          // the one skill in a stock v232 client that lands here (65000003,
          // y = "140+y") would read as a working skill with no effect.
          text: '—',
        }, icon('alert', { size: 12 })));
        return td;
      }

      td.append(el('span', {
        class: 'sk-cell-computed',
        'data-tip': source === 'container'
          ? `${column.key} holds a group of values. Open it in the Explorer.`
          : `Computed from common/${column.key} = ${column.formula}. Edit the formula, or bake it into levels.`,
        text: cell?.value ?? '—',
      }));
      return td;
    }

    const value = cell?.value ?? '';
    const input = el('input', {
      class: 'sk-input sk-cell-input', value, spellcheck: 'false',
      'aria-label': `${column.label || column.key} at level ${row.level}`,
      placeholder: source === 'missing' ? '—' : null,
      inputmode: column.kind === 'Int' ? 'numeric' : null,
    });
    let committed = value;

    if (column.kind === 'Int') input.addEventListener('focus', () => input.select());

    const send = () => {
      const next = input.value;
      if (next === committed) return;

      if (column.kind === 'Int' && next.trim() !== '' && !Number.isFinite(Number(next))) {
        input.dataset.invalid = 'true';
        input.title = `${column.label || column.key} has to be a number.`;
        return;
      }
      delete input.dataset.invalid;
      input.title = '';
      // Recorded before the request, so a blur that follows the Enter that
      // started it does not send the same edit twice.
      committed = next;
      ctx.focus = { level: row.level, key: column.key };
      this.commitCell(ctx, row, column, next, () => { input.value = committed = value; });
    };

    input.addEventListener('blur', send);
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); send(); }
      else if (event.key === 'Escape') { event.preventDefault(); input.value = committed; input.blur(); }
      // Down and up move to the same field one level away, which is how anyone
      // filling in a table actually works (rule 8).
      else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        const step = event.key === 'ArrowDown' ? 1 : -1;
        const target = this.neighbourCell(td, step);
        if (!target) return;
        event.preventDefault();
        send();
        target.focus();
        target.select?.();
      }
    });

    td.append(input);
    return td;
  }

  /** The input in the same column, `step` rows away, if it is editable. */
  neighbourCell(td, step) {
    const tr = td.parentElement;
    const index = [...tr.children].indexOf(td);
    const next = step > 0 ? tr.nextElementSibling : tr.previousElementSibling;
    return next?.children[index]?.querySelector('input') ?? null;
  }

  commitCell(ctx, row, column, value, revert) {
    return this.guard(`cell:${ctx.detail.path}:${row.level}:${column.key}`, async () => {
      try {
        const next = await api.skillWriteLevels(ctx.detail.path, [
          { level: row.level, key: column.key, value },
        ]);
        ctx.touched = true;
        this.app.markDirty();
        await ctx.reload(next);
        // Said once per commit rather than as a toast per cell: the cell turns
        // green and that is the receipt. The trap is what needs saying.
        if (ctx.detail.hasCommon) {
          toast(
            `Written to level/${row.level}/${column.key} — but this skill still has a common block, so the ` +
            'client will keep computing this value from the formula and ignore what you just wrote.',
            'warning', { title: 'Saved, but the client will not read it' });
        }
      } catch (error) {
        revert?.();
        toastError(error, 'Could not save that value');
      }
    });
  }

  /* ============================================================
     LEVEL OPERATIONS
     ============================================================ */

  levelMenu(ctx, row) {
    const level = row.level;
    return [
      row.present
        ? null
        : { icon: 'plus', label: `Create level ${level} as a real node`,
            run: () => this.levelOp(ctx, { op: 'add', level }, `Added level ${level}`) },
      row.present
        ? { icon: 'copy', label: `Copy level ${level} to a new level…`, run: () => this.cloneLevel(ctx, level) }
        : null,
      row.present
        ? { icon: 'edit', label: `Renumber level ${level}…`, run: () => this.renameLevel(ctx, level) }
        : null,
      row.present
        ? { icon: 'trash', label: `Remove level ${level}`, run: () => this.removeLevel(ctx, level) }
        : null,
      { icon: 'externalLink', label: 'Show this level in Explorer', run: () => {
        ctx.close?.();
        this.app.setMode('explorer');
        this.app.navigate(row.path);
      } },
    ].filter(Boolean);
  }

  addNextLevel(ctx) {
    // The first gap, not maxLevel+1: a skill missing level 7 wants level 7 far
    // more often than it wants level 31.
    const present = new Set(ctx.detail.levels.filter((row) => row.present).map((row) => row.level));
    let level = 1;
    while (present.has(level)) level++;
    return this.levelOp(ctx, { op: 'add', level }, `Added level ${level}`);
  }

  async cloneLevel(ctx, from) {
    const answer = await promptForText({
      title: `Copy level ${from}`,
      message: 'Which level number should the copy become? It has to be a level that does not exist yet.',
      value: String(from + 1),
      confirmLabel: 'Copy the level',
    });
    if (!answer) return;
    const level = Number(answer);
    if (!Number.isInteger(level) || level < 1) { toast('Levels are whole numbers starting at 1.', 'warning'); return; }
    return this.levelOp(ctx, { op: 'clone', from, level }, `Copied level ${from} to ${level}`);
  }

  async renameLevel(ctx, level) {
    const answer = await promptForText({
      title: `Renumber level ${level}`,
      message: 'What should this level become? The target has to be free.',
      value: String(level + 1),
      confirmLabel: 'Renumber',
    });
    if (!answer) return;
    const to = Number(answer);
    if (!Number.isInteger(to) || to < 1) { toast('Levels are whole numbers starting at 1.', 'warning'); return; }
    return this.levelOp(ctx, { op: 'rename', level, to }, `Level ${level} is now level ${to}`);
  }

  async removeLevel(ctx, level) {
    const ok = await confirmDialog({
      title: `Remove level ${level}?`,
      message: `Everything stored under level/${level} goes with it. Nothing reaches disk until you save, ` +
               'and this is one Ctrl+Z.',
      confirmLabel: 'Remove the level',
    });
    if (!ok) return;
    return this.levelOp(ctx, { op: 'remove', level }, `Removed level ${level}`);
  }

  levelOp(ctx, request, success) {
    return this.guard(`level:${ctx.detail.path}:${request.op}:${request.level}`, async () => {
      try {
        const next = await api.skillLevelOp({ path: ctx.detail.path, ...request });
        ctx.touched = true;
        this.app.markDirty();
        await ctx.reload(next);
        toast(success, 'success', {
          action: { label: 'Undo', run: async () => { await this.app.undo(); await this.refresh(); } },
        });
      } catch (error) {
        toastError(error, 'Could not change that level');
      }
    });
  }

  /* ============================================================
     SKILL-WIDE FIELDS
     ============================================================ */

  /** masterLevel, weapon, elemAttr and the rest of info/ — one value per skill, not per level. */
  buildFieldPanel(ctx) {
    const groups = ctx.detail.groups || [];
    if (!groups.length) return null;

    const details = el('details', { class: 'sk-section' });
    details.append(el('summary', {},
      el('span', { class: 'sk-section-name', text: 'Skill-wide fields' }),
      el('span', { class: 'sk-section-meta',
        text: groups.reduce((total, group) => total + group.fields.length, 0) + ' fields outside the level table' })));

    for (const group of groups) {
      details.append(el('div', { class: 'sk-group-name', text: group.group }));
      const grid = el('div', { class: 'sk-field-grid' });
      for (const field of group.fields) grid.append(this.buildField(ctx, field));
      details.append(grid);
    }
    return details;
  }

  buildField(ctx, field) {
    const row = el('div', { class: 'sk-field' });
    row.append(el('div', { class: 'sk-field-label' },
      el('span', { class: 'sk-field-name', text: field.label || field.key }),
      el('code', { class: 'sk-key', text: field.key }),
      field.unit ? el('span', { class: 'sk-unit', text: field.unit }) : null));

    if (!field.editable) {
      // A container: shown, never faked into an editable box. Rule 12 asks for
      // the edge to be visible with a reason beside it.
      row.append(el('div', { class: 'sk-field-static', text: field.value ?? '—' }));
      row.append(el('div', { class: 'sk-hint' },
        el('span', { text: 'A group of values — ' }),
        el('button', {
          class: 'sk-link',
          onclick: () => { ctx.close?.(); this.app.setMode('explorer'); this.app.navigate(field.path); },
        }, 'edit it in the Explorer')));
      return row;
    }

    const value = field.value ?? '';

    if (field.kind === 'Flag') {
      const box = el('input', { type: 'checkbox', checked: value === '1' });
      box.addEventListener('change', () => {
        this.commitField(ctx, field, box.checked ? '1' : '0', () => { box.checked = value === '1'; });
      });
      row.append(el('label', { class: 'sk-check' }, box, el('span', { text: value === '1' ? 'On' : 'Off' })));
      if (field.hint) row.append(el('div', { class: 'sk-hint', text: field.hint }));
      return row;
    }

    const input = el('input', {
      class: 'sk-input', value, spellcheck: 'false',
      inputmode: field.kind === 'Int' ? 'numeric' : null,
      'aria-label': field.label || field.key,
    });
    const send = () => {
      if (input.value === value) return;
      this.commitField(ctx, field, input.value, () => { input.value = value; });
    };
    input.addEventListener('blur', send);
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); send(); }
      else if (event.key === 'Escape') { event.preventDefault(); input.value = value; input.blur(); }
    });
    row.append(input);
    if (field.hint) row.append(el('div', { class: 'sk-hint', text: field.hint }));
    return row;
  }

  /** Same reasoning as commitFormula: one WZ node, its own path, WzEditService underneath. */
  commitField(ctx, field, value, revert) {
    return this.guard(`field:${field.path}`, async () => {
      try {
        await api.setValue(field.path, value);
        ctx.touched = true;
        this.app.markDirty();
        await ctx.reload();
      } catch (error) {
        revert?.();
        toastError(error, 'Could not save that field');
      }
    });
  }

  /* ============================================================
     BAKE (expand-common)
     ============================================================ */

  openBakeDialog(ctx) {
    return this.guard(`bake-dialog:${ctx.detail.path}`, () => this.buildBakeDialog(ctx));
  }

  /**
   * The headline action, and the dangerous one.
   *
   * It writes hundreds of nodes from one click -- 272 cells for skill 5221017,
   * as one undo entry -- and then deletes the common block. The deletion is not
   * tidying: while the block exists the client computes from the formulas and
   * never reads level/, so a bake that kept it would produce a perfectly correct
   * level table that changes nothing in game. That sentence is in the dialog,
   * next to the switch that turns it off, because it is the one thing a user
   * needs to understand before pressing the button (rules 2 and 6).
   */
  async buildBakeDialog(ctx) {
    const d = ctx.detail;
    if (!d.hasCommon) {
      toast('This skill has no common block, so there are no formulas to bake.', 'info');
      return;
    }

    const levelsInput = el('input', {
      class: 'sk-input', type: 'number', min: '1', value: String(d.maxLevel ?? 1),
      'aria-label': 'How many levels to write',
    });
    const overwrite = el('input', { type: 'checkbox' });
    const removeCommon = el('input', { type: 'checkbox', checked: true });

    const removeWarn = el('div', { class: 'sk-bake-warn' });
    const paintRemoveWarn = () => {
      clear(removeWarn);
      removeWarn.dataset.tone = removeCommon.checked ? 'ok' : 'bad';
      removeWarn.append(
        icon(removeCommon.checked ? 'info' : 'alert', { size: 14 }),
        el('span', {
          text: removeCommon.checked
            ? 'Correct. The client reads a common block in preference to level nodes, so it has to go for ' +
              'the levels below to be the ones that count. maxLevel goes with it, which is exactly how every ' +
              'level-based skill in a stock client is stored.'
            : 'The bake will have no effect in game. The client will keep computing every level from the ' +
              'formulas and ignore everything this writes. Only leave this off if you are keeping the ' +
              'formulas on purpose.',
        }));
    };
    paintRemoveWarn();

    const previewHost = el('div', { class: 'sk-preview-host' });
    let preview = null;

    const applyButton = el('button', {
      class: 'btn btn-red', disabled: true,
      'data-tip': 'Preview first — nothing is written until you have seen what it does',
    }, icon('check', { size: 15 }), 'Bake');

    const previewButton = el('button', { class: 'btn' }, icon('eye', { size: 15 }), 'Preview the bake');

    /**
     * Any change to the recipe throws the preview away.
     *
     * An Apply that stays enabled after the level count changes is an Apply that
     * writes something other than what is on screen.
     */
    const invalidate = () => {
      preview = null;
      applyButton.disabled = true;
      applyButton.textContent = '';
      applyButton.append(icon('check', { size: 15 }), 'Bake');
      clear(previewHost);
      previewHost.append(el('div', { class: 'sk-preview-empty' },
        icon('info', { size: 15 }),
        el('span', { text: 'Preview to see every node this would create and everything it would delete. ' +
                           'Nothing is written until then.' })));
    };
    levelsInput.addEventListener('input', invalidate);
    for (const box of [overwrite, removeCommon]) {
      box.addEventListener('change', () => { paintRemoveWarn(); invalidate(); });
    }
    invalidate();

    const recipe = () => ({
      path: d.path,
      levels: Number(levelsInput.value) || undefined,
      overwrite: overwrite.checked,
      removeCommon: removeCommon.checked,
    });

    previewButton.addEventListener('click', () => this.guard('bake-preview', async () => {
      clear(previewHost);
      previewHost.append(el('div', { class: 'sk-preview-empty' }, el('span', { text: 'Working…' })));
      try {
        const result = await api.skillExpandCommon({ ...recipe(), dryRun: true });
        preview = result;
        clear(previewHost);
        previewHost.append(this.buildBakePreview(ctx, result, removeCommon.checked));
        const writing = (result.changes || []).filter((c) => !c.skipped).length;
        applyButton.disabled = writing === 0;
        applyButton.textContent = '';
        applyButton.append(icon('check', { size: 15 }),
          writing ? `Write ${fmt.format(writing)} values and delete the common block` : 'Nothing to write');
        if (!removeCommon.checked && writing) {
          applyButton.textContent = '';
          applyButton.append(icon('check', { size: 15 }), `Write ${fmt.format(writing)} values`);
        }
      } catch (error) {
        preview = null;
        applyButton.disabled = true;
        clear(previewHost);
        previewHost.append(el('div', { class: 'sk-preview-empty', 'data-tone': 'bad' },
          icon('alert', { size: 15 }),
          el('span', { text: error.message })));
      }
    }));

    const { close } = modal({
      title: 'Bake the formulas into explicit levels',
      subtitle: d.name || `Skill ${d.skillId}`,
      width: 'min(96vw, 940px)',
      body: el('div', { class: 'sk-bake' },
        el('p', { class: 'sk-bake-lede',
          text: 'Every formula in this skill\'s common block is evaluated at every level and written as a ' +
                'real level/N node. After this the values are stored, editable one level at a time, and ' +
                'independent of each other — which is what you want if levels should stop following one curve.' }),
        el('div', { class: 'sk-bake-grid' },
          bakeField('Levels to write', levelsInput,
            `The skill declares ${d.maxLevel ?? 'no'} level${d.maxLevel === 1 ? '' : 's'}. Fewer writes fewer rows; the rest are lost when the block goes.`),
          bakeField('Replace values that already exist',
            el('label', { class: 'sk-check' }, overwrite, el('span', { text: 'Overwrite' })),
            'Off, a level that already holds a literal value is left alone and listed as skipped.'),
          bakeField('Delete the common block afterwards',
            el('label', { class: 'sk-check' }, removeCommon, el('span', { text: 'Delete it' })),
            null)),
        removeWarn,
        el('div', { class: 'sk-bake-actions' }, previewButton, applyButton),
        previewHost,
        el('div', { class: 'tips' },
          el('b', { text: 'How this behaves' }),
          el('ul', {},
            el('li', { text: 'The preview runs against the server, so what you see is exactly what would be written.' }),
            el('li', { text: 'A formula that cannot be parsed is skipped with its error — never written as 0.' }),
            el('li', { text: 'The whole bake is one undo step, however many nodes it creates.' }),
            el('li', { text: 'A PVPcommon block, if there is one, is left alone.' }),
            el('li', { text: 'Nothing reaches disk until you save.' })))),
      actions: [{ label: 'Close' }],
    });

    applyButton.addEventListener('click', () => this.guard('bake-apply', async () => {
      if (!preview) return;
      const writing = (preview.changes || []).filter((c) => !c.skipped);
      const levels = new Set(writing.map((c) => c.level)).size;

      const ok = await confirmDialog({
        title: `Bake ${fmt.format(writing.length)} values across ${fmt.format(levels)} levels?`,
        message: (removeCommon.checked
          ? 'The common block and its formulas will be deleted. That is what makes the bake take effect — ' +
            'without it the client keeps reading the formulas. '
          : 'The common block is being kept, so the client will keep reading the formulas and this will have ' +
            'no effect in game. ') +
          'This is one Ctrl+Z, and nothing reaches disk until you save.',
        confirmLabel: removeCommon.checked ? 'Bake and delete the block' : 'Bake anyway',
        danger: true,
      });
      if (!ok) return;

      try {
        const result = await api.skillExpandCommon({ ...recipe(), dryRun: false });
        const skipped = (result.changes || []).filter((c) => c.skipped).length;
        close();
        toast(
          `Wrote ${fmt.format(result.applied ?? 0)} value${result.applied === 1 ? '' : 's'} across ` +
          `${fmt.format(result.levelsWritten ?? 0)} level${result.levelsWritten === 1 ? '' : 's'}` +
          (result.removedCommon ? ', and removed the common block' : '') +
          (skipped ? `. Skipped ${fmt.format(skipped)} — see the preview for why.` : '.'),
          'success', {
            action: { label: 'Undo', run: async () => { await this.app.undo(); await this.refresh(); } },
          });
        // Notes are the things that are not per row -- a dropped maxLevel, an
        // untouched PVPcommon. They are not decoration and they do not belong in
        // a toast that disappears.
        for (const note of result.notes || []) toast(note, 'info', { title: 'About that bake' });
        ctx.touched = true;
        this.app.markDirty();
        await ctx.reload(result.detail ?? undefined);
        await this.load();
      } catch (error) {
        toastError(error, 'Could not bake this skill');
      }
    }));
  }

  buildBakePreview(ctx, result, removingCommon) {
    const changes = result.changes || [];
    const writing = changes.filter((c) => !c.skipped);
    const skipped = changes.filter((c) => c.skipped);
    const levels = new Set(writing.map((c) => c.level));

    const body = el('tbody');
    for (const change of changes) {
      body.append(el('tr', { 'data-skipped': change.skipped ? 'true' : null },
        el('td', { class: 'num', text: String(change.level) }),
        el('td', {}, el('code', { text: change.key })),
        el('td', { class: 'num', text: change.before ?? '—' }),
        el('td', { class: 'sk-preview-arrow', text: change.skipped ? '' : '→' }),
        el('td', { class: 'num', text: change.skipped ? '—' : (change.after ?? '—') }),
        el('td', { class: 'sk-preview-note', text: change.skipped ? (change.reason || 'Skipped') : (change.wzType || '') })));
    }

    // What gets deleted, spelled out. "The common block will be removed" is not
    // enough when the block is the only copy of the formulas.
    const doomed = ctx.detail.columns.filter((column) => column.formula != null);

    return el('div', {},
      el('div', { class: 'sk-preview-summary' },
        el('div', { class: 'sk-preview-stat' },
          el('span', { class: 'k', text: 'Values created' }),
          el('span', { class: 'v', text: fmt.format(writing.length) })),
        el('div', { class: 'sk-preview-stat' },
          el('span', { class: 'k', text: 'Levels touched' }),
          el('span', { class: 'v', text: fmt.format(levels.size) })),
        el('div', { class: 'sk-preview-stat', 'data-tone': skipped.length ? 'warn' : null },
          el('span', { class: 'k', text: 'Skipped' }),
          el('span', { class: 'v', text: fmt.format(skipped.length) })),
        el('div', { class: 'sk-preview-stat' },
          el('span', { class: 'k', text: 'Undo steps' }),
          el('span', { class: 'v', text: '1' }))),

      removingCommon
        ? el('div', { class: 'sk-delete-box' },
            el('div', { class: 'sk-delete-head' },
              icon('trash', { size: 14 }),
              el('b', { text: `And these ${doomed.length + 1} nodes are deleted` })),
            el('div', { class: 'sk-delete-list' },
              doomed.map((column) => el('div', {},
                el('code', { text: `common/${column.key}` }),
                el('span', { class: 'sk-delete-value', text: ` = ${column.formula}` }))),
              ctx.detail.maxLevel
                ? el('div', {},
                    el('code', { text: 'common/maxLevel' }),
                    el('span', { class: 'sk-delete-value', text: ` = ${ctx.detail.maxLevel}` }))
                : null),
            el('div', { class: 'sk-hint',
              text: 'The formulas are the only copy of the curve. After this the level values above are all ' +
                    'that is left of it — which is the point, but it is not reversible except by undo.' }))
        : null,

      (result.notes || []).length
        ? el('ul', { class: 'sk-notes' }, (result.notes || []).map((note) => el('li', { text: note })))
        : null,

      el('div', { class: 'sk-preview-head' },
        el('span', { text: `${fmt.format(writing.length)} would be written` +
                           (skipped.length ? `, ${fmt.format(skipped.length)} skipped` : '') })),
      el('div', { class: 'sk-preview-scroll' },
        el('table', { class: 'sk-preview' },
          el('thead', {}, el('tr', {}, ['Level', 'Field', 'Before', '', 'After', 'Type / reason']
            .map((h) => el('th', { text: h })))),
          body)));
  }

  /* ============================================================
     BULK EDIT
     ============================================================ */

  openBulkDialog() {
    return this.guard('bulk-dialog', () => this.buildBulkDialog());
  }

  /**
   * The field list comes from a real skill in the selection, because which
   * fields exist depends on the client. A hardcoded list would offer keys this
   * Skill.wz does not have and hide the ones it does.
   */
  async bulkFieldOptions(path) {
    try {
      const detail = await api.skillDetail(path);
      const found = (detail.columns || [])
        .filter((column) => column.kind !== 'Point' && column.source !== 'container')
        .map((column) => [column.key, `${column.group} · ${column.label || column.key}`]);
      if (found.length) return found;
    } catch {
      /* fall through rather than blocking the dialog */
    }
    // Every skill that has a common block at all has damage or mpCon; this is
    // the safest possible guess when the probe failed.
    return [['damage', 'Damage'], ['mpCon', 'MP cost'], ['cooltime', 'Cooldown'], ['time', 'Duration']];
  }

  async buildBulkDialog() {
    const paths = [...this.selection];
    if (!paths.length) {
      toast('Tick a few skills first — every card has a checkbox in its corner.', 'info');
      return;
    }

    const fields = await this.bulkFieldOptions(paths[0]);

    const fieldSelect = el('select', { class: 'sk-select', 'aria-label': 'Field to change' },
      fields.map(([key, label]) => el('option', { value: key, text: label })));
    const levelInput = el('input', { class: 'sk-input', type: 'number', min: '0', value: '0' });
    const opSelect = el('select', { class: 'sk-select', 'aria-label': 'What to do' },
      OPS.map(([value, label]) => el('option', { value, text: label })));
    const valueInput = el('input', { class: 'sk-input', type: 'number', step: 'any', value: '1' });
    const roundSelect = el('select', { class: 'sk-select', 'aria-label': 'Rounding' },
      ROUNDING.map(([value, label]) => el('option', { value, text: label })));

    const previewHost = el('div', { class: 'sk-preview-host' });
    let preview = null;

    const applyButton = el('button', {
      class: 'btn btn-primary', disabled: true,
      'data-tip': 'Preview first — nothing is written until you have seen the numbers',
    }, icon('check', { size: 15 }), `Apply to ${fmt.format(paths.length)} skill${paths.length === 1 ? '' : 's'}`);

    const previewButton = el('button', { class: 'btn' }, icon('eye', { size: 15 }), 'Preview changes');

    const invalidate = () => {
      preview = null;
      applyButton.disabled = true;
      clear(previewHost);
      previewHost.append(el('div', { class: 'sk-preview-empty' },
        icon('info', { size: 15 }),
        el('span', { text: 'Preview a change to see the exact before and after for every skill, and the ' +
                           'reason each skipped one was skipped. Nothing is written until then.' })));
    };
    for (const control of [fieldSelect, opSelect, roundSelect]) control.addEventListener('change', invalidate);
    for (const control of [valueInput, levelInput]) control.addEventListener('input', invalidate);
    invalidate();

    const recipe = () => ({
      paths,
      field: fieldSelect.value,
      level: Number(levelInput.value) || 0,
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
      previewHost.append(el('div', { class: 'sk-preview-empty' }, el('span', { text: 'Working…' })));
      try {
        const result = await api.skillBulk({ ...body, dryRun: true });
        preview = result;
        clear(previewHost);
        previewHost.append(this.buildBulkPreview(result));
        const changing = (result.changes || []).filter((c) => !c.skipped).length;
        applyButton.disabled = changing === 0;
        applyButton.dataset.tip = changing
          ? `Write these ${fmt.format(changing)} changes`
          : 'Nothing here would change';
      } catch (error) {
        preview = null;
        applyButton.disabled = true;
        clear(previewHost);
        previewHost.append(el('div', { class: 'sk-preview-empty', 'data-tone': 'bad' },
          icon('alert', { size: 15 }),
          el('span', { text: error.message })));
      }
    }));

    const { close } = modal({
      title: 'Bulk edit skills',
      subtitle: `${fmt.format(paths.length)} skill${paths.length === 1 ? '' : 's'} selected`,
      width: 'min(96vw, 940px)',
      body: el('div', { class: 'sk-bulk' },
        el('div', { class: 'sk-bulk-grid' },
          bakeField('Field', fieldSelect),
          bakeField('Where', levelInput,
            '0 is the common block — the formulas. Any other number is that level\'s stored node.'),
          bakeField('Change', opSelect),
          bakeField('Value', valueInput),
          bakeField('Rounding', roundSelect,
            'Multiply and percent produce fractions; a WZ integer cannot hold one.')),
        el('div', { class: 'sk-bulk-actions' }, previewButton, applyButton),
        previewHost,
        el('div', { class: 'tips' },
          el('b', { text: 'How this behaves' }),
          el('ul', {},
            el('li', { text: 'Preview runs against the server, so what you see is exactly what would be written.' }),
            el('li', { text: 'A value that is a formula over the level — "235+3*x" — is refused, not mangled. ' +
                             'Bake that skill first, or edit its formula directly.' }),
            el('li', { text: 'A skill without the field is skipped with the reason; it is never created here.' }),
            el('li', { text: 'A point (x, y) is skipped: it is two numbers, not one.' }),
            el('li', { text: 'The whole batch is one undo step, and nothing reaches disk until you save.' })))),
      actions: [{ label: 'Close' }],
    });

    applyButton.addEventListener('click', () => this.guard('bulk-apply', async () => {
      if (!preview) return;
      const changing = (preview.changes || []).filter((c) => !c.skipped);
      const where = Number(levelInput.value) > 0 ? `level ${levelInput.value}` : 'the common block';
      const ok = await confirmDialog({
        title: `Change ${fieldSelect.value} on ${fmt.format(changing.length)} skill${changing.length === 1 ? '' : 's'}?`,
        message: `${OPS.find(([v]) => v === opSelect.value)?.[1]} ${valueInput.value}, in ${where} — this is ` +
                 'the preview you are looking at. Nothing is written to disk until you save, and you can undo it.',
        confirmLabel: 'Apply the changes',
        danger: false,
      });
      if (!ok) return;

      try {
        const result = await api.skillBulk({ ...recipe(), dryRun: false });
        const skipped = (result.changes || []).filter((c) => c.skipped).length;
        close();
        toast(
          `Updated ${fmt.format(result.applied ?? 0)} skill${result.applied === 1 ? '' : 's'}` +
          (skipped ? `, skipped ${fmt.format(skipped)} — the preview says why.` : '.'),
          'success', {
            action: { label: 'Undo', run: async () => { await this.app.undo(); await this.refresh(); } },
          });
        this.app.markDirty();
        await this.load();
      } catch (error) {
        toastError(error, 'Could not apply those changes');
      }
    }));
  }

  buildBulkPreview(result) {
    const changes = result.changes || [];
    const skipped = changes.filter((c) => c.skipped).length;

    const body = el('tbody');
    for (const change of changes) {
      body.append(el('tr', { 'data-skipped': change.skipped ? 'true' : null },
        el('td', {},
          el('div', { class: 'sk-preview-name', text: change.name || `Skill ${change.skillId}` }),
          el('div', { class: 'sk-sub', text: String(change.skillId) })),
        el('td', { class: 'num', text: change.before ?? '—' }),
        el('td', { class: 'sk-preview-arrow', text: change.skipped ? '' : '→' }),
        el('td', { class: 'num', text: change.skipped ? '—' : (change.after ?? '—') }),
        el('td', { class: 'sk-preview-note', text: change.skipped ? (change.reason || 'Skipped') : '' })));
    }

    return el('div', {},
      el('div', { class: 'sk-preview-head' },
        el('span', { text: `${fmt.format(changes.length - skipped)} would change` +
                           (skipped ? `, ${fmt.format(skipped)} skipped` : '') }),
        result.truncated
          ? el('span', { class: 'sk-preview-trunc',
              text: 'The server stopped listing early — more skills are affected than are shown.' })
          : null),
      el('div', { class: 'sk-preview-scroll' },
        el('table', { class: 'sk-preview' },
          el('thead', {}, el('tr', {}, ['Skill', 'Before', '', 'After', ''].map((h) => el('th', { text: h })))),
          body)));
  }

  /** Right-click actions for the section itself; shared with the app-wide menu. */
  menuItems() {
    return [
      { icon: 'search', label: 'Focus the search box', run: () => this.focusSearch() },
      this.selection.size
        ? { icon: 'sliders', label: `Bulk edit the ${this.selection.size} selected skills…`,
            run: () => this.openBulkDialog() }
        : null,
      this.selection.size
        ? { icon: 'close', label: 'Clear the selection', run: () => { this.selection.clear(); this.render(); } }
        : null,
      this.bookId
        ? { icon: 'layers', label: 'Browse every book', run: () => this.setBook(null) }
        : null,
      { icon: 'refresh', label: 'Reload the skill list', run: () => this.load() },
    ].filter(Boolean);
  }
}

/**
 * The skill's own icon out of the open client, or the section's glyph when
 * there is not one.
 *
 * `<path>/icon` rather than the bare skill path: /api/thumb walks down to the
 * first canvas beneath whatever it is handed, and naming the node skips that
 * search -- measured at 2ms against 15ms, which is the difference between a
 * sixty-card page painting at once and painting in waves. The bare path is kept
 * as the fallback for a skill that keeps its art somewhere else.
 *
 * Both requests go through the shared loader, so a page of sixty asks for the
 * dozen on screen, a few at a time, and paging away cancels the rest. `signal`
 * is the grid's repaint scope; the detail head passes none, because it is one
 * icon inside a dialog with its own paint schedule.
 */
function skillIcon(path, box = 40, signal) {
  const thumb = el('div', { class: 'sk-icon', style: `width:${box}px;height:${box}px` });
  const img = el('img', { alt: '', decoding: 'async' });
  let walked = false;

  const glyph = () => {
    img.remove();
    if (!thumb.querySelector('.placeholder')) {
      thumb.append(el('span', { class: 'placeholder' }, icon('star', { size: Math.round(box * 0.45) })));
    }
  };

  // Both listeners are attached before src is set, always -- which is what the
  // loader guarantees, since it assigns the src itself. A cached image fires
  // load synchronously from the assignment, so a listener added after it never
  // runs and the icon stays at its natural size for ever.
  img.addEventListener('load', () => {
    // 204 means the client has no art for this skill. Most browsers report it
    // as an error, but a zero-sized decode is the same fact and must fall back
    // rather than leave an empty box.
    if (!img.naturalWidth) { glyph(); return; }
    scalePixelArt(img, box - 6, box - 6);
  });
  img.addEventListener('error', () => {
    if (walked) { glyph(); return; }
    walked = true;
    // Still queued and still deduped: the fallback is the expensive one, since
    // it is the walk that naming `/icon` was avoiding.
    lazySprite(img, thumbUrl(path), { signal });
  });

  thumb.append(img);
  lazySprite(img, thumbUrl(`${path}/icon`), { signal });
  return thumb;
}

/** One labelled control in the bake or bulk recipe. */
function bakeField(label, control, hint) {
  return el('div', { class: 'sk-bulk-field' },
    el('label', { text: label }), control,
    hint ? el('div', { class: 'sk-hint', text: hint }) : null);
}

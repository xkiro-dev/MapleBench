/**
 * The sectioned workspace: one list of day-to-day editors down the left, one
 * stage on the right, and the Explorer left exactly as it was beside it.
 *
 * This is a shell, not a fourth editor. Mobs, Cash Shop and Database are the
 * same instances the app has always held -- they are handed their stage as their
 * host at construction and shown from here, so there is one Mobs mode with one
 * selection and one dirty state, however you reach it.
 *
 * Skills, NPCs and Strings have no backend yet. They are listed, disabled, with
 * the archive they will need and a line saying what they will do. Hiding them
 * would make the shell look finished and leave the user guessing what is
 * planned; faking them would be worse (rule 12).
 */

import { api } from './api.js';
import { el, clear, toast, busyPanel, replayMotion } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';

const STORE_KEY = 'mb.section';
const NAV_STORE_KEY = 'mb.editorsNavCollapsed';

/**
 * The sections, in the order they are worth reaching for.
 *
 * `probe` answers "is this section's archive open?". Where a capabilities
 * endpoint answers that question it is used; where it does not, the note says
 * so rather than inventing an answer -- see cashShopReady().
 */
const SECTIONS = [
  {
    id: 'mobs',
    label: 'Mobs',
    icon: 'layers',
    archive: 'Mob.wz',
    blurb: 'Stats and flags for every mob, with a previewed bulk edit.',
  },
  {
    id: 'cashshop',
    label: 'Cash Shop',
    icon: 'cart',
    archive: 'Etc.wz',
    blurb: 'The Commodity.img list: prices, serials and what is on sale.',
  },
  {
    id: 'database',
    label: 'Game Data Search',
    icon: 'search',
    archive: 'String.wz',
    blurb: 'Search every item, mob, skill, NPC and map the client names.',
  },
  {
    id: 'skills',
    label: 'Skills',
    icon: 'star',
    archive: 'Skill.wz',
    blurb: 'Per-level values, with the common/ formula trap called out before you edit into it.',
  },
  {
    id: 'npcs',
    label: 'NPCs',
    icon: 'info',
    archive: 'Npc.wz',
    blurb: 'Shop stock, dialogue scripts and the maps an NPC stands on.',
  },
  {
    id: 'strings',
    label: 'Strings',
    icon: 'type',
    archive: 'String.wz',
    blurb: 'Edit the names every other section reads, without hunting through the tree.',
  },
  {
    id: 'mapedit',
    label: 'Map Editor',
    icon: 'image',
    archive: 'Map002.wz',
    blurb: 'Open a map, see it drawn — backgrounds, tiles, objects, overlays — make small safe ' +
           'edits, and save through the byte-identical round-trip model.',
  },
];

const REAL = SECTIONS.filter((section) => !section.soon).map((section) => section.id);

export class Sections {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    /**
     * One stage element per real section, handed to Mobs/CashShop/Database at
     * construction and shown one at a time.
     *
     * Not one shared element, for two reasons. Each mode rewrites its host's
     * className on every render, so the element cannot carry the shell's own
     * layout class. And each mode renders whenever its own load resolves --
     * switching away from Mobs while its list is still in the air would
     * otherwise have that list paint itself over whichever section you switched
     * to. One element each also means a section keeps its scroll position, its
     * selection and its search box when you come back to it.
     */
    this.stages = new Map(REAL.map((id) => [id, el('div', { class: 'stage-body' })]));

    this.listHost = el('nav', { class: 'sec-list', 'aria-label': 'Editors' });
    this.stageHost = el('div', { class: 'sec-main' }, ...this.stages.values());
    this.overview = el('div', { class: 'editors-empty' }, emptyState(
      'folderOpen', 'Open a client to use Editors',
      'Editors become available from the archives in the client you open.',
      el('button', { class: 'btn btn-primary', onclick: () => this.app.openFilePicker() },
        icon('folderOpen', { size: 15 }), 'Open client')));
    this.stageHost.prepend(this.overview);
    this.entries = new Map();
    this.foldButton = null;
    this.listBuilt = false;
    this.navCollapsed = localStorage.getItem(NAV_STORE_KEY) === 'true';

    const remembered = localStorage.getItem(STORE_KEY);
    // Old builds could remember one of the removed specialist workflows.
    // Falling back keeps an upgrade from reopening a section that no longer
    // belongs to the public app.
    this.active = REAL.includes(remembered) ? remembered : REAL[0];

    /** id -> { state: 'unknown' | 'ready' | 'missing' | 'error', note } */
    this.status = new Map();
  }

  /**
   * Shows the workspace at a section, mounting it if it is not already there.
   *
   * Re-entering the same section deliberately re-opens it: coming back from the
   * Explorer after editing a mob should show the edit, and every section's
   * open() is the cheap path (the expensive parse is cached server-side against
   * the session generation).
   */
  async open(id) {
    const previous = this.active;
    let section = SECTIONS.find((s) => s.id === id) ?? SECTIONS.find((s) => s.id === this.active);

    // The list entry for an unbuilt section is a disabled button, so this is
    // only reachable by asking for one by name. It says why and falls back to
    // the section the user was in, rather than leaving the workspace on screen
    // with nothing mounted in it.
    if (section?.soon) {
      toast(`${section.label} is not built yet — ${section.blurb}`, 'info', { title: 'Coming soon' });
      section = SECTIONS.find((s) => s.id === this.active);
    }

    this.active = section?.id ?? REAL[0];
    localStorage.setItem(STORE_KEY, this.active);

    this.render({ animate: previous !== this.active });
    if (!this.app.files.length) return;
    // Capabilities are asked once per visit rather than per keystroke: they
    // change only when an archive is opened or closed.
    this.refreshStatus();
    await this.mount();
  }

  /** The element a section renders into; the App hands these out at construction. */
  stageFor(id) {
    return this.stages.get(id);
  }

  render({ animate = false } = {}) {
    // Explorer and Editors share this host, so it may have been repainted while
    // the stable section shell was detached. Reattach the same list, buttons,
    // chevron and stage elements; section-to-section navigation never rebuilds
    // them and therefore keeps focus, scroll, and CSS transition timelines.
    if (this.listHost.parentElement !== this.host || this.stageHost.parentElement !== this.host) {
      clear(this.host);
      this.host.append(this.listHost, this.stageHost);
    }
    this.host.className = 'stage-body sections';
    this.host.dataset.navCollapsed = this.navCollapsed ? 'true' : 'false';

    this.paintList();

    const empty = !this.app.files.length;
    this.stageHost.classList.toggle('editors-empty', empty);
    this.overview.hidden = !empty;
    for (const [id, stage] of this.stages) stage.hidden = empty || id !== this.active;
    if (animate && !empty) replayMotion(this.stages.get(this.active), 'section-enter');
  }

  paintList() {
    if (!this.listBuilt) {
      this.foldButton = el('button', {
        class: 'btn btn-icon btn-ghost sec-list-fold',
        'data-tip-placement': 'right',
        onclick: () => this.toggleNavigation(),
      }, icon('chevronRight', { size: 15 }));
      this.listHost.append(el('div', { class: 'sec-list-head' },
        el('div', { class: 'sec-list-title', text: 'Editors' }),
        this.foldButton));

      for (const section of SECTIONS) {
        const state = el('span', { class: 'sec-state' });
        const entry = el('button', {
          class: 'sec-item',
          'aria-label': section.label,
          'data-tip-placement': 'right',
          onclick: section.soon ? null : () => this.open(section.id),
        },
          el('span', { class: 'sec-glyph' }, icon(section.icon, { size: 16 })),
          el('span', { class: 'sec-body' },
            el('span', { class: 'sec-head' },
              el('span', { class: 'sec-label', text: section.label }),
              section.soon ? el('span', { class: 'sec-chip', text: 'Coming soon' }) : null,
              state)));
        this.entries.set(section.id, { entry, state });
        this.listHost.append(entry);
      }
      this.listBuilt = true;
    }

    const hasClient = this.app.files.length > 0;

    for (const section of SECTIONS) {
      const status = this.status.get(section.id) ?? { state: 'unknown' };

      // What this list is for, on the second visit and every visit after it, is
      // getting to a section in one glance. It used to spend three lines per
      // entry -- name, a sentence of prose, and a sentence about which archive
      // is open -- so six sections filled 700px and the blurb you had read on
      // day one was still there on day two hundred, between you and the name
      // you were aiming for. The blurb is now the tooltip: still discoverable,
      // no longer in the way.
      //
      // The archive note stays only when it is news. "String.wz open" under
      // every one of six ready sections is six lines saying nothing is wrong;
      // the green dot says that already, and when something IS wrong the
      // sentence is the only one in the list and impossible to miss.
      const ready = status.state === 'ready' && !section.soon;

      const refs = this.entries.get(section.id);
      const active = hasClient && section.id === this.active && !section.soon;
      refs.entry.dataset.active = active ? 'true' : 'false';
      refs.entry.setAttribute('aria-current', active ? 'true' : 'false');
      refs.entry.setAttribute('data-tip', [
        section.blurb,
        hasClient ? this.noteFor(section, status) : `Requires ${section.archive}`,
      ].filter(Boolean).join(' · '));
      refs.entry.disabled = !!section.soon;
      clear(refs.state);
      if (ready) refs.state.append(el('span', { class: 'sec-dot', 'data-state': 'ready' }));
      else if (status.state === 'error') refs.state.append(el('span', { class: 'sec-dot', 'data-state': 'error' }));
    }

    this.foldButton.setAttribute('aria-label', this.navCollapsed ? 'Expand Editors navigation' : 'Collapse Editors navigation');
    this.foldButton.setAttribute('aria-expanded', this.navCollapsed ? 'false' : 'true');
    this.foldButton.setAttribute('data-tip', this.navCollapsed ? 'Expand Editors' : 'Collapse Editors');
  }
  toggleNavigation() {
    this.navCollapsed = !this.navCollapsed;
    localStorage.setItem(NAV_STORE_KEY, this.navCollapsed ? 'true' : 'false');
    this.host.dataset.navCollapsed = this.navCollapsed ? 'true' : 'false';
    this.paintList();
  }

  /** The one line under a section saying which archive it needs and whether it is open. */
  noteFor(section, status) {
    if (section.soon) return `will need ${section.archive}`;
    switch (status.state) {
      case 'ready': return status.note ?? `${section.archive} open`;
      case 'missing': return status.note ?? `needs ${section.archive} — not open`;
      case 'error': return `could not check — ${status.note}`;
      default: return `needs ${section.archive} — checking…`;
    }
  }

  /**
   * Asks each real section whether it can work right now.
   *
   * Mobs and Database have a capabilities endpoint that answers exactly this.
   * The cash shop's reports its icon and name sources rather than whether
   * Commodity.img is open, so that one is answered by the same file test the
   * mode itself uses to decide it can open -- reading `icons: false` as "Etc.wz
   * is not open" would be a different question with a plausible wrong answer.
   */
  async refreshStatus() {
    const set = (id, state, note) => this.status.set(id, { state, note });

    const [mobs, database, skills, npcs, strings, mapedit, shop] = await Promise.allSettled([
      api.mobCapabilities(),
      api.dbCapabilities(),
      api.skillCapabilities(),
      api.npcCapabilities(),
      api.stringCapabilities(),
      api.mapeditCapabilities(),
      api.shopCapabilities(),
    ]);

    if (mobs.status === 'fulfilled') {
      const named = this.openArchive(/^mob/i);
      set('mobs', mobs.value.available ? 'ready' : 'missing',
        mobs.value.available
          ? `${named} open${mobs.value.names ? '' : ' · String.wz is not, so mobs have no names'}`
          : undefined);
    } else {
      set('mobs', 'error', mobs.reason?.message ?? 'no answer');
    }

    if (database.status === 'fulfilled') {
      set('database', database.value.available ? 'ready' : 'missing',
        database.value.available ? `${this.openArchive(/^string/i)} open` : undefined);
    } else {
      set('database', 'error', database.reason?.message ?? 'no answer');
    }

    // Each of these answers exactly "can this section work right now?", so the
    // note repeats the archive's real filename rather than the one the section
    // asks for -- a split String001.wz should not be reported as "String.wz".
    const capability = (id, settled, pattern, extra) => {
      if (settled.status !== 'fulfilled') {
        set(id, 'error', settled.reason?.message ?? 'no answer');
        return;
      }
      const available = settled.value?.available === true;
      const source = id === 'mapedit' && settled.value?.mapArchives?.length
        ? (settled.value.mapArchives.length === 1
          ? settled.value.mapArchives[0]
          : `${settled.value.mapArchives.length} archives`)
        : this.openArchive(pattern);
      set(id, available ? 'ready' : 'missing',
        available ? `${source} open${extra?.(settled.value) ?? ''}` : undefined);
    };

    capability('skills', skills, /^skill/i,
      // Skill names live in String.wz, so a skill list without it is numbers.
      (value) => (value.names ? '' : ' · String.wz is not, so skills have no names'));
    capability('npcs', npcs, /^npc/i,
      (value) => (value.names ? '' : ' · String.wz is not, so NPCs have no names'));
    capability('strings', strings, /^string/i);
    capability('mapedit', mapedit, /^map/i,
      // Map names live in String.wz; the map picker shows bare ids without it.
      (value) => (value.names ? '' : ' · String.wz is not, so maps have no names'));

    if (shop.status === 'fulfilled') {
      this.cashShopSourceId = shop.value.source?.id ?? null;
      const source = this.cashShopFile();
      set('cashshop', source ? 'ready' : 'missing', source ? `${source.name} open` : undefined);
    } else {
      this.cashShopSourceId = null;
      set('cashshop', 'error', shop.reason?.message ?? 'no answer');
    }

    this.paintList();
  }

  /**
   * What to call the archive a section is reading.
   *
   * The real filename, not the family name: a split client opens String_000.wz
   * and a repack opens Mob001.wz, and "String.wz open" beside neither of them is
   * the kind of small lie that makes a user doubt the rest of the screen.
   */
  openArchive(pattern) {
    const matches = this.app.files.filter((file) => pattern.test(file.name));
    if (!matches.length) return 'it';
    return matches.length === 1 ? matches[0].name : `${matches.length} archives`;
  }

  /** The archive Cash Shop mode would read, by the same test the mode uses. */
  cashShopFile() {
    return this.app.files.find((file) => file.id === this.cashShopSourceId) ?? null;
  }

  /* ============================================================
     MOUNTING
     ============================================================ */

  async mount() {
    if (this.active === 'mobs') {
      await this.app.mobs.open(this.app.mobs.fileId);
      return;
    }

    if (this.active === 'database') {
      await this.app.db.open();
      return;
    }

    // Each of these owns its own stage element, so a slow first build cannot
    // paint itself over whichever section the user switched to meanwhile.
    if (this.active === 'skills') {
      await this.app.skills.open();
      return;
    }

    if (this.active === 'npcs') {
      await this.app.npcs.open();
      return;
    }

    if (this.active === 'strings') {
      await this.app.strings.open();
      return;
    }

    if (this.active === 'mapedit') {
      const stage = this.stageFor('mapedit');
      if (!this.app.mapedit) {
        clear(stage);
        stage.className = 'stage-body';
        stage.setAttribute('aria-busy', 'true');
        stage.append(busyPanel({
          title: 'Opening Map Editor',
          note: 'Preparing the canvas and asset catalog.',
          rows: 4,
        }));
      }
      try {
        const mapedit = await this.app.ensureMapEditor();
        stage.removeAttribute('aria-busy');
        await mapedit.open();
      } catch (error) {
        stage.removeAttribute('aria-busy');
        clear(stage);
        stage.className = 'stage-body';
        stage.append(emptyState(
          'alert', 'Map Editor did not load',
          error?.message ?? 'The editor files could not be read.',
          el('button', { class: 'btn btn-primary', onclick: () => this.open('mapedit') },
            icon('refresh', { size: 15 }), 'Try again')));
      }
      return;
    }

    if (this.active === 'cashshop') {
      const candidate = this.cashShopFile() ?? this.app.files[0];
      if (!candidate) {
        const stage = this.stageFor('cashshop');
        clear(stage);
        stage.className = 'stage-body';
        stage.append(emptyState(
          'cart', 'Open Etc.wz first',
          'Cash shop editing reads Commodity.img from Etc.wz. You can also open Commodity.img on its own.',
          el('button', { class: 'btn btn-primary', onclick: () => this.app.openFilePicker() },
            icon('folderOpen', { size: 15 }), 'Open a file')));
        return;
      }
      await this.app.shop.open(candidate.id);
    }
  }

  /** Re-reads whatever is on screen after an edit somewhere else changed it. */
  reload() {
    if (this.active === 'mobs') return this.app.mobs.load();
    if (this.active === 'cashshop') return this.app.shop.load();
    if (this.active === 'database') return this.app.db.load();
    if (this.active === 'skills') return this.app.skills.refresh();
    if (this.active === 'npcs') return this.app.npcs.refresh();
    if (this.active === 'strings') return this.app.strings.refresh();
    // The map editor deliberately does NOT reload on outside edits: its
    // document is its own unsaved state, and re-fetching would not lose it but
    // would repaint mid-drag. Its own edits drive its own refreshes.
    return undefined;
  }

  /** Right-click actions for the section list itself. */
  menuItems() {
    return [
      ...REAL.map((id) => ({
        icon: SECTIONS.find((s) => s.id === id).icon,
        label: `Open ${SECTIONS.find((s) => s.id === id).label}`,
        run: () => this.open(id),
      })),
      { icon: 'refresh', label: 'Check which archives are open', run: () => this.refreshStatus() },
    ];
  }
}

/** The section ids the palette and the shortcuts can address. */
export const SECTION_IDS = REAL;

/** Label lookup, for palette entries built outside this module. */
export const sectionLabel = (id) => SECTIONS.find((s) => s.id === id)?.label ?? id;

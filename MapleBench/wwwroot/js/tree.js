/**
 * Virtualized WZ tree.
 *
 * Only the rows in view exist in the DOM, which is what keeps a directory of
 * 40,000 siblings responsive. Two consequences drive the design:
 *
 *  - Row height is FIXED (--row-h). Index maths is then `scrollTop / rowHeight`
 *    instead of a measured height map, and nothing may make a row grow.
 *  - Nested `role="group"` DOM is impossible when 40 of 40,000 rows are real,
 *    so this is a flat list carrying aria-level / aria-posinset / aria-setsize,
 *    with a single `aria-activedescendant` on the container rather than a
 *    tabindex on every row.
 */

import { api } from './api.js';
import { el, clear, sortNodes, archiveQualifiers } from './ui.js';
import { icon, nodeIcon } from './icons.js';

const ROW_H = 28;
const OVERSCAN = 8;

/**
 * Ceiling on cached node DTOs.
 *
 * Every child read is remembered here and, until now, nothing ever dropped one:
 * invalidate() cleared the children lists and left the nodes behind, and there
 * was no delete anywhere else. Walking Character.wz for an afternoon is
 * hundreds of thousands of entries at a few hundred bytes each, which is most
 * of why a day-long session ends up sluggish.
 *
 * Above the cap, everything that is not on screen or on the way to it is
 * dropped in one sweep -- see prune(). The cost of being wrong is one
 * /api/children when a collapsed folder is opened again; the cost of never
 * being wrong is unbounded.
 */
const MAX_NODES = 40000;

/* ============================================================
   DRAG TUNING

   WZ has no undo beyond this app's own, so the cost of a drag that fires when
   the user meant to click is a structural edit to an archive nobody asked for.
   Every number here exists to make that harder.
   ============================================================ */

/**
 * How far the pointer must travel before a press becomes a drag.
 *
 * A click on a tree row commonly moves one or two pixels between down and up,
 * and a press that starts a scroll flick moves a few more. Below this the
 * gesture stays a click; above it nothing is committed either, only an
 * indicator is drawn -- the edit happens on release and only on release.
 */
const DRAG_THRESHOLD = 6;

/** How close to a pane edge starts auto-scrolling, and the fastest it goes. */
const AUTOSCROLL_EDGE = 30;
const AUTOSCROLL_MAX = 20;

/** How long the pointer must rest over a closed folder before it opens. */
const HOVER_EXPAND_MS = 650;

/**
 * The band at each end of a row that means "between these two rows" rather
 * than "inside this one".
 *
 * Wide enough to hit on a 28px row without care, narrow enough that the middle
 * -- the reparenting gesture, which is the destructive one -- is still the
 * larger target and cannot be reached by aiming at a boundary.
 */
const EDGE_BAND = 0.3;

/**
 * Property types that hold an ordered child list.
 *
 * Mirrors WzNodeFactory.GetPropertyCollection: WzImage, WzCanvasProperty,
 * WzSubProperty, WzConvexProperty and nothing else. Anything not listed here
 * answers null there, and Transfer refuses it -- so refusing during the drag is
 * the same rule stated early, where the user can still change their mind.
 */
const PROPERTY_CONTAINERS = new Set(['SubProperty', 'Canvas', 'Convex']);

/**
 * The WzExtended property types, by their DTO type name.
 *
 * A Convex may hold only these. It is not a style rule: WzConvexProperty
 * writes a child count of just its extended children and then the bodies of
 * all of them, so a scalar inside one is written and then skipped by the
 * reader -- gone after the next save, with nothing reporting it. WzEditService
 * refuses it on both the Add and the Transfer path; this is the same list, so
 * the drag can say no before the drop instead of after.
 */
const EXTENDED_TYPES = new Set(['SubProperty', 'Canvas', 'Vector', 'Convex', 'Sound', 'Raw', 'UOL']);

export class TreeView {
  /**
   * @param checkable  Give every row a tick box. Opt-in and off in the Explorer,
   *                   which selects rather than ticks. The import picker turns it
   *                   on because "which of these do you want" is a different
   *                   question from "which one are you looking at", and because a
   *                   selection that survives scrolling has to live in a Set here
   *                   rather than in the recycled row pool -- forty row elements
   *                   are shared between 12,000 children, so a checked box drawn
   *                   into one of them would reappear on an unrelated row four
   *                   screens down.
   * @param onCheck    Called after any tick change, with the Set of checked paths.
   * @param draggable  Whether this tree runs its OWN drag gesture -- the move /
   *                   reorder pair below. On everywhere by default. The two-pane
   *                   import view turns it off on both of its trees because the
   *                   gesture it wants crosses between them: one pane's rows are
   *                   the thing being dragged and the other pane's rows are the
   *                   drop targets, and a per-tree drag can only ever hit-test
   *                   its own sizer. Left on, both trees would arm a second
   *                   gesture underneath that one and draw a second ghost saying
   *                   something different about the same pointer -- which is the
   *                   one thing a drag indicator must never do.
   * @param onCheckToggle
   *                   Intercepts one tick before it lands, with the path, its node
   *                   and the box's new state. The import picker sets it so that
   *                   ticking a folder ticks what is inside it -- which can mean
   *                   fetching children that were never opened, so it may return a
   *                   promise. Null everywhere else, where a tick is one row and
   *                   nothing has to be loaded to know that.
   */
  constructor({ scroller, rows, sizer, onSelect, onActivate, onContext, onDrop,
                checkable = false, onCheck = null, onCheckToggle = null,
                draggable = true }) {
    this.scroller = scroller;
    this.rowsHost = rows;
    this.sizer = sizer;
    this.onSelect = onSelect;
    this.onActivate = onActivate;
    this.onContext = onContext;
    this.onDrop = onDrop;
    this.draggable = draggable;
    this.checkable = checkable;
    this.onCheck = onCheck;
    this.onCheckToggle = onCheckToggle;
    /** Ticked paths. Survives scrolling, filtering and collapsing, unlike a DOM state. */
    this.checked = new Set();

    /** path -> node DTO */
    this.nodes = new Map();
    /** path -> array of child paths (present once loaded) */
    this.children = new Map();
    /** paths currently expanded */
    this.expanded = new Set();
    /** paths currently loading children */
    this.loading = new Set();
    /** paths whose last load failed; stops a refetch loop on a broken node */
    this.failed = new Set();
    /** root paths, in order */
    this.roots = [];

    this.selection = new Set();
    this.focusPath = null;
    /** flattened visible rows, rebuilt whenever the shape changes */
    this.flat = [];

    this.typeFilter = new Set();
    this.nameFilter = '';
    this.sortMode = localStorage.getItem('mb.sort') || 'name';

    this.pool = [];
    this.typeAhead = { buffer: '', at: 0 };

    /** Pointer is down on a row but has not travelled far enough to be a drag. */
    this.press = null;
    /** The armed drag, once DRAG_THRESHOLD is crossed. Null the rest of the time. */
    this.drag = null;
    /** Set on release so the click that follows a drag does not also select. */
    this.dragEnded = false;

    this.scroller.addEventListener('scroll', () => this.scheduleRender(), { passive: true });
    this.rowsHost.addEventListener('click', (e) => this.onClick(e));
    this.rowsHost.addEventListener('dblclick', (e) => this.onDoubleClick(e));
    this.rowsHost.addEventListener('contextmenu', (e) => this.onRightClick(e));
    this.rowsHost.addEventListener('keydown', (e) => this.onKeyDown(e));

    // Pointer events rather than HTML5 drag-and-drop, for two reasons that both
    // matter here. HTML5 fires dragstart on the browser's own threshold, which
    // is one or two pixels and cannot be raised. And its drag image is taken
    // from a DOM element assumed to stay put, while this list recycles a pool of
    // forty rows -- the element under the cursor is a different node a scroll
    // later, so anything holding on to one is holding the wrong thing.
    //
    // Move and up are on the window, not the row host: a drag that leaves the
    // pane still has to be tracked, and one that ends outside it still has to
    // be cancelled rather than left armed.
    this.rowsHost.addEventListener('pointerdown', (e) => this.onPointerDown(e));
    window.addEventListener('pointermove', (e) => this.onPointerMove(e));
    window.addEventListener('pointerup', (e) => this.onPointerUp(e));
    window.addEventListener('pointercancel', () => this.cancelDrag());
    // Escape is the way out of a drag that turned out to be a mistake, and it
    // has to work while the pointer is still down.
    window.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && this.drag) { e.preventDefault(); this.cancelDrag(); }
    });

    new ResizeObserver(() => this.scheduleRender()).observe(this.scroller);
  }

  /* ============================================================
     DATA
     ============================================================ */

  /**
   * The archive rows at the top of the tree.
   *
   * @param files     Every open archive. This list is always complete, whether
   *                  or not a file is drawn as its own root: saving, the dirty
   *                  count and the read-only check all read it, and hiding a
   *                  merged file from *them* would be hiding it from the things
   *                  that keep its edits safe.
   * @param families  Merged families. Each becomes one root and swallows its
   *                  members' rows -- four Skill archives are one tree, which is
   *                  the entire point -- but the members stay in `files` and
   *                  every node inside still carries its own file's real path.
   */
  setRoots(files, families = []) {
    // Two clients open at once puts two rows called String.wz in this list, one
    // above the other, with nothing on either to say which is which -- and one
    // of them is the one about to be edited and saved. The folder that tells
    // them apart is written onto the row as its alias, in the slot an id row
    // uses for its resolved name, and it appears only for the names that really
    // do collide.
    const qualifiers = archiveQualifiers(files);

    /** Every open file by id, merged or not -- see fileFor(). */
    this.filesById = new Map(files.map((file) => [file.id, file]));
    /** file id -> the family that has swallowed its root row. */
    this.memberOf = new Map();
    /** family id -> family DTO, for the row and its menu. */
    this.familiesById = new Map();

    for (const family of families) {
      this.familiesById.set(family.id, family);
      for (const member of family.members) this.memberOf.set(member.fileId, family.id);
    }

    const roots = [];

    for (const family of families) {
      this.nodes.set(family.id, {
        path: family.id,
        name: family.name,
        kind: 'File',
        hasChildren: true,
        childCount: -1,
        // A family is never dirty itself; its members are, and the dot has to
        // follow them or a merged tree would hide unsaved work.
        dirty: family.members.some((m) => m.dirty),
        displayName: `${family.members.length} files`,
        familyInfo: family,
      });
      roots.push(family.id);
    }

    for (const file of files) {
      if (this.memberOf.has(file.id)) continue;
      const path = file.id;
      this.nodes.set(path, {
        path,
        name: file.name,
        kind: 'File',
        hasChildren: true,
        childCount: -1,
        dirty: file.dirty,
        displayName: qualifiers.get(file.id) || undefined,
        fileInfo: file,
      });
      roots.push(path);
    }

    this.roots = roots;
    this.rebuild();
  }

  getNode(path) { return this.nodes.get(path); }

  async loadChildren(path, { force = false } = {}) {
    if (!force && this.children.has(path)) return this.children.get(path);
    if (this.loading.has(path)) return this.children.get(path) ?? [];

    this.loading.add(path);
    this.scheduleRender();
    try {
      const list = await api.children(path);
      const paths = list.map((node) => {
        this.nodes.set(node.path, node);
        return node.path;
      });
      this.children.set(path, paths);
      this.failed.delete(path);
      // A ticked row includes what is inside it, and the inside arrives late.
      //
      // The subtree is fetched a level at a time, so 'everything under this' is
      // not something that can be applied once when the box is clicked -- most
      // of the subtree does not exist yet at that moment. Applying it here
      // instead makes it hold for good: whenever a row appears under something
      // already ticked, it is ticked too, however deep and however much later.
      // Without this, opening a ticked folder showed a screen of empty boxes
      // inside a ticked one.
      if (this.checked.has(path)) this.checkAll(paths);
      return paths;
    } catch (error) {
      // Remember the failure. Without this the empty-children branch in
      // rebuild() would ask again on every render, forever.
      this.failed.add(path);
      this.children.set(path, []);
      throw error;
    } finally {
      this.loading.delete(path);
      this.prune();
      this.rebuild();
    }
  }

  /**
   * Drops everything the visible tree does not need.
   *
   * Runs only above MAX_NODES, and keeps what is on screen or on the way to it:
   * the roots, every expanded path and its ancestors, and the children lists of
   * those -- which is every row that can be rendered -- plus the selection.
   * Everything else is a collapsed subtree somebody looked at once.
   */
  prune() {
    if (this.nodes.size <= MAX_NODES) return;

    const keep = new Set(this.roots);
    for (const open of this.expanded) {
      let running = open;
      for (;;) {
        keep.add(running);
        const cut = running.lastIndexOf('/');
        if (cut < 0) break;
        running = running.slice(0, cut);
      }
    }
    for (const path of this.selection) keep.add(path);
    // Ticked rows too. A prune that dropped one would not untick it -- the Set
    // is the truth -- but it would drop the node DTO the tray reads the name off,
    // so the import list would go blank while the paths were still armed.
    for (const path of this.checked) keep.add(path);
    if (this.focusPath) keep.add(this.focusPath);

    // A children list is only worth keeping if its parent can still be walked
    // to; failed/loading markers for the rest go with them.
    for (const parent of [...this.children.keys()]) {
      if (!keep.has(parent)) {
        this.children.delete(parent);
        this.failed.delete(parent);
      }
    }

    // A node DTO survives if it is kept, or if it is a child of something kept.
    const alive = new Set(keep);
    for (const kids of this.children.values()) {
      for (const kid of kids) alive.add(kid);
    }
    for (const path of [...this.nodes.keys()]) {
      if (!alive.has(path)) this.nodes.delete(path);
    }
  }

  /** Drops cached children so the next expand re-reads from the backend. */
  invalidate(path) {
    this.failed.delete(path);
    this.children.delete(path);
    for (const key of [...this.children.keys()]) {
      if (key.startsWith(path + '/')) this.children.delete(key);
    }
    // And the DTOs underneath them. They are about to be re-read, so keeping
    // them is keeping a stale copy -- this is the delete that was missing, and
    // the reason a session that opened a lot of images never gave the memory
    // back. The node itself stays: rebuild() walks through it.
    const prefix = path + '/';
    for (const key of [...this.nodes.keys()]) {
      if (key.startsWith(prefix)) this.nodes.delete(key);
    }
    this.rebuild();
  }

  invalidateAll() {
    this.failed.clear();
    this.children.clear();
    // Roots are handed to us by setRoots(), not read from the backend, so they
    // are the one thing that cannot simply be re-fetched here.
    const roots = new Set(this.roots);
    for (const key of [...this.nodes.keys()]) {
      if (!roots.has(key)) this.nodes.delete(key);
    }
    this.rebuild();
  }

  async expand(path) {
    if (this.expanded.has(path)) return;
    this.expanded.add(path);
    await this.loadChildren(path);
    this.rebuild();
  }

  collapse(path) {
    this.expanded.delete(path);
    this.rebuild();
  }

  async toggle(path) {
    if (this.expanded.has(path)) this.collapse(path);
    else await this.expand(path);
  }

  /**
   * Expands a whole subtree, breadth-first and capped.
   *
   * Uncapped this would parse an entire archive -- Character.wz alone is
   * hundreds of thousands of nodes -- so it stops at a row budget and reports
   * whether it ran out, letting the caller say so rather than appearing to hang.
   */
  async expandAll(path, { maxRows = 2000, maxDepth = 6 } = {}) {
    let queue = [[path, 0]];
    let opened = 0;
    let truncated = false;

    while (queue.length) {
      const [current, depth] = queue.shift();
      if (depth > maxDepth) { truncated = true; continue; }
      if (this.flat.length + opened > maxRows) { truncated = true; break; }

      const node = this.nodes.get(current);
      if (!node?.hasChildren) continue;

      this.expanded.add(current);
      const kids = await this.loadChildren(current);
      opened += kids.length;

      for (const kid of kids) {
        if (this.nodes.get(kid)?.hasChildren) queue.push([kid, depth + 1]);
      }
    }

    this.rebuild();
    return { truncated, opened };
  }

  /** Collapses a node and everything beneath it. */
  collapseAll(path) {
    for (const open of [...this.expanded]) {
      if (open === path || open.startsWith(path + '/')) this.expanded.delete(open);
    }
    this.rebuild();
  }

  /** Collapses every root back to a clean slate. */
  collapseEverything() {
    this.expanded.clear();
    this.rebuild();
  }

  /** Expands every ancestor of a path and scrolls it into view. */
  async reveal(path) {
    // In a merged family a node's ancestors are NOT its path's prefixes: the
    // folders above it are addressed by the family ("g1/Dragon") and the node
    // itself by its own file ("f2/Dragon/2214.img"). Only the server knows where
    // that boundary is, because it is wherever the folders stop -- so it is
    // asked, once, and only for paths that are in a family at all.
    if (this.memberOf?.has(path.split('/')[0])) {
      try {
        const chain = await api.familyLocate(path);
        if (chain?.length) {
          for (let i = 0; i < chain.length - 1; i++) await this.expand(chain[i]);
          this.rebuild();
          this.select(chain[chain.length - 1]);
          this.scrollTo(chain[chain.length - 1]);
          return;
        }
      } catch {
        // Fall through to the ordinary walk. A reveal that lands on the right
        // node the slow way is better than one that throws.
      }
    }

    const parts = path.split('/');
    let running = parts[0];
    await this.expand(running);
    for (let i = 1; i < parts.length; i++) {
      running += '/' + parts[i];
      if (i < parts.length - 1) await this.expand(running);
    }
    this.rebuild();
    this.select(path);
    this.scrollTo(path);
  }

  scrollTo(path) {
    // The scroller is shared with the Pinned, Unsaved-changes and Results
    // panels, which take it over while the tree is hidden. Scrolling it then
    // moves whichever list the user is actually reading -- clicking a search
    // result jumped the results list to an unrelated offset, losing the place
    // in the very list the panel exists to preserve. The tree still expands and
    // selects; only the scroll waits until the tree is the thing on screen,
    // where setPanel restores it from panelScroll anyway.
    if (this.sizer?.hidden) return;

    const index = this.flat.findIndex((row) => row.path === path);
    if (index < 0) return;
    const top = index * ROW_H;
    const { scrollTop, clientHeight } = this.scroller;
    if (top < scrollTop || top + ROW_H > scrollTop + clientHeight) {
      // Instant, never smooth: smooth-scrolling a virtual list fires a render
      // per frame for the whole distance.
      this.scroller.scrollTo({ top: top - clientHeight / 3, behavior: 'instant' });
    }
    this.scheduleRender();
  }

  /* ============================================================
     FLATTEN
     ============================================================ */

  rebuild() {
    const flat = [];
    // Paths whose children vanished and need re-reading. Collected during the
    // walk and fetched after it: loadChildren() calls rebuild() when it
    // finishes, so starting one mid-walk would re-enter this function.
    const refetch = [];
    const walk = (path, level) => {
      const node = this.nodes.get(path);
      if (!node) return;
      flat.push({ path, level, node });
      if (!this.expanded.has(path)) return;

      const kids = this.children.get(path);
      if (!kids) {
        // Expanded, but the children are gone -- invalidate() after a save or a
        // structural edit drops them. Note it for a refetch rather than
        // rendering nothing, which is what used to blank the tree under every
        // open node. The fetch happens after this walk, never during it.
        if (!this.loading.has(path) && !this.failed.has(path)) refetch.push(path);
        for (let i = 0; i < 3; i++) flat.push({ path: `${path}::skel${i}`, level: level + 1, skeleton: true });
        return;
      }
      const visible = this.order(this.applyFilter(kids));
      visible.forEach((child) => walk(child, level + 1));
    };

    this.roots.forEach((root) => walk(root, 0));
    this.flat = flat;
    this.sizer.style.height = `${flat.length * ROW_H}px`;
    this.scheduleRender();

    for (const path of refetch) this.loadChildren(path);
  }

  /** Sibling filter: name substring and/or property type chips. */
  applyFilter(paths) {
    if (!this.nameFilter && this.typeFilter.size === 0) return paths;
    const needle = this.nameFilter.toLowerCase();
    return paths.filter((path) => {
      const node = this.nodes.get(path);
      if (!node) return false;
      if (this.typeFilter.size && !(node.type && this.typeFilter.has(node.type))) return false;
      if (needle && !node.name.toLowerCase().includes(needle)) return false;
      return true;
    });
  }

  /** Applies the current sort to a list of child paths. */
  order(paths) {
    if (this.sortMode === 'wz') return paths;
    const nodes = paths.map((path) => this.nodes.get(path)).filter(Boolean);
    return sortNodes(nodes, this.sortMode).map((node) => node.path);
  }

  setSortMode(mode) {
    this.sortMode = mode;
    localStorage.setItem('mb.sort', mode);
    this.rebuild();
  }

  setNameFilter(value) { this.nameFilter = value.trim(); this.rebuild(); }

  toggleTypeFilter(type) {
    if (this.typeFilter.has(type)) this.typeFilter.delete(type);
    else this.typeFilter.add(type);
    this.rebuild();
    return this.typeFilter.has(type);
  }

  /* ============================================================
     RENDER
     ============================================================ */

  scheduleRender() {
    if (this._frame) return;
    this._frame = requestAnimationFrame(() => {
      this._frame = null;
      this.render();
    });
  }

  render() {
    const { scrollTop, clientHeight } = this.scroller;
    // `first` comes from where the pane is scrolled to and `last` from how much
    // content there is, and the two can disagree: the scroller is SHARED with
    // the Pinned, Unsaved-changes and Results panels, so it can be sitting at
    // scrollTop 8,000 for a list this tree knows nothing about while this.flat
    // is empty. That made `first` 277 and `last` 0, so `needed` went negative,
    // pop() returned undefined, and the loop below threw on every scroll frame
    // for as long as the other panel was open.
    //
    // Clamping `first` to the content is the fix rather than clamping `needed`,
    // because the same mismatch also decides the row host's transform and the
    // index every row is painted from -- a negative count was the loudest
    // symptom, not the fault.
    const maxFirst = Math.max(0, this.flat.length - 1);
    const first = Math.min(maxFirst, Math.max(0, Math.floor(scrollTop / ROW_H) - OVERSCAN));
    const count = Math.ceil(clientHeight / ROW_H) + OVERSCAN * 2;
    const last = Math.min(this.flat.length, first + count);

    this.rowsHost.style.transform = `translateY(${first * ROW_H}px)`;

    const needed = Math.max(0, last - first);
    // Recycle row elements rather than recreating them every frame.
    while (this.pool.length < needed) {
      const row = this.buildRow();
      this.pool.push(row);
      this.rowsHost.append(row);
    }
    while (this.pool.length > needed) {
      this.pool.pop().remove();
    }

    // The rows host is *borrowed*: the Pinned and Unsaved-changes panels render
    // their own content into it and empty it on the way in. The pool survives
    // that, so a row can be live in the pool and detached from the DOM at the
    // same time — and because the pool is then already the right length, the
    // loops above add nothing and every paint below lands on an element that is
    // not on the page. Switching to another side tab and back left the tree
    // blank, with the borrowing panel's rows still showing underneath.
    //
    // Checked here rather than fixed at the call site because the host has three
    // borrowers and counting; one of them will forget.
    if (this.pool.length > 0 && this.pool[0].parentNode !== this.rowsHost) {
      this.rowsHost.replaceChildren(...this.pool);
    }

    for (let i = 0; i < needed; i++) {
      this.paintRow(this.pool[i], this.flat[first + i], first + i);
    }
  }

  buildRow() {
    const row = el('div', { class: 'tree-row', role: 'treeitem' });
    const twisty = el('button', { class: 'tree-twisty', tabindex: '-1', 'aria-hidden': 'true' });
    twisty.append(icon('chevronRight', { size: 13 }));
    row.append(
      twisty,
      el('span', { class: 'tree-icon' }),
      el('span', { class: 'tree-label' }),
      // The human name behind an id -- "Snail" beside 0100100.img -- when the
      // backend could resolve one. Decoration only: it is styled inline rather
      // than through a class because it belongs to this row builder and to
      // nothing else, and it must never grow the row, which the virtualizer
      // pins to --row-h.
      el('span', {
        class: 'tree-alias',
        style: 'flex:none;color:var(--text-3);font-size:var(--fs-11);' +
               'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:38%',
      }),
      // Which physical file this row's node lives in, drawn only inside a merged
      // family -- where the row's ancestry no longer answers it. It is the one
      // thing a merge must not cost the reader: four archives shown as one tree
      // are still four files, and every edit and every save lands in exactly
      // one of them.
      el('span', { class: 'tree-source' }),
      el('span', { class: 'tree-value' }),
    );
    // Appended last and moved to the front with `order:-1` in CSS, rather than
    // inserted first. paintRow reads this row's children by position, and every
    // other caller of buildRow is the Explorer, where there is no tick box --
    // shifting the indices for one caller would be a silent off-by-one in the
    // other. The visual order is a layout question, so CSS answers it.
    if (this.checkable) {
      row.append(el('input', {
        type: 'checkbox', class: 'tree-check', tabindex: '-1',
        'aria-label': 'Include this in the import',
      }));
    }
    return row;
  }

  paintRow(row, entry, index) {
    if (!entry) { row.style.display = 'none'; return; }
    row.style.display = '';

    const [twisty, iconSlot, label, alias, source, value, check] = row.children;

    if (entry.skeleton) {
      if (check) { check.checked = false; check.hidden = true; }
      row.dataset.path = '';
      row.removeAttribute('id');
      row.setAttribute('aria-hidden', 'true');
      row.style.setProperty('--lvl', String(entry.level));
      twisty.dataset.leaf = 'true';
      clear(iconSlot);
      label.className = 'tree-label skeleton';
      label.style.width = `${45 + ((index * 37) % 40)}%`;
      label.style.height = '12px';
      label.textContent = '';
      alias.textContent = '';
      source.textContent = '';
      source.dataset.shadowed = 'false';
      value.textContent = '';
      return;
    }

    const node = entry.node;
    label.className = 'tree-label';
    label.style.width = '';
    label.style.height = '';

    // Repainted from the Set on every frame, never left as the pool found it. A
    // recycled row carries the last node's tick until something overwrites it,
    // and a tick on the wrong row is a promise to import something the user did
    // not ask for.
    if (check) {
      check.hidden = false;
      check.checked = this.checked.has(node.path);
    }

    row.dataset.path = node.path;
    row.dataset.kind = node.kind || '';
    row.dataset.type = node.type || '';
    row.id = `tv-${cssId(node.path)}`;
    row.removeAttribute('aria-hidden');
    // One custom property instead of an inline padding: CSS derives both the
    // indent and the vertical guides from it, so they can never drift apart.
    row.style.setProperty('--lvl', String(entry.level));
    row.setAttribute('aria-level', String(entry.level + 1));
    row.setAttribute('aria-selected', this.selection.has(node.path) ? 'true' : 'false');
    row.dataset.dirty = node.dirty ? 'true' : 'false';

    const expandable = node.hasChildren;
    twisty.dataset.leaf = expandable ? 'false' : 'true';
    twisty.dataset.expanded = this.expanded.has(node.path) ? 'true' : 'false';
    if (expandable) row.setAttribute('aria-expanded', this.expanded.has(node.path) ? 'true' : 'false');
    else row.removeAttribute('aria-expanded');
    row.setAttribute('aria-busy', this.loading.has(node.path) ? 'true' : 'false');

    // Swap the icon only when the node behind this recycled row changed;
    // rebuilding an SVG on every scroll frame is pure waste.
    const signature = `${node.kind}:${node.type ?? ''}`;
    if (iconSlot.dataset.sig !== signature) {
      iconSlot.dataset.sig = signature;
      clear(iconSlot);
      iconSlot.append(nodeIcon(node, 14));
    }

    label.textContent = node.name || '(unnamed)';

    // displayName is optional and absent far more often than present, and it is
    // never identity: the row is still addressed by node.path, type-ahead and
    // the sibling filter still match node.name only. Repeating the name itself
    // would just be noise.
    const displayName = node.displayName && node.displayName !== node.name ? node.displayName : '';
    alias.textContent = displayName;

    // Set from the DTO on every paint, never left as the recycled row found it:
    // a stale "Skill003.wz" under a node that is in Skill.wz is worse than no
    // badge at all, because it is an answer.
    source.textContent = node.source ?? '';
    source.dataset.shadowed = node.shadowed ? 'true' : 'false';
    source.title = node.shadowed
      ? `Also in ${(node.sources ?? []).slice(1).join(', ')}. Only this copy is shown; `
        + 'which one wins is decided by mount order alone.'
      : (node.source ? `In ${node.source}` : '');

    row.dataset.tip = [
      node.name || '',
      displayName,
      node.type || '',
      node.shadowed ? `in ${node.source} — also in ${(node.sources ?? []).slice(1).join(', ')}` : '',
    ].filter(Boolean).join(' · ');

    value.textContent = node.kind === 'Property' && node.value != null ? node.value : '';
  }

  /* ============================================================
     INPUT
     ============================================================ */

  rowFrom(event) {
    const row = event.target.closest('.tree-row');
    return row?.dataset.path ? row.dataset.path : null;
  }

  onClick(event) {
    // A drag ends with a pointerup over a row, and the browser follows that
    // with a click. Without this, every drop also re-selected whatever the
    // pointer happened to be over when it was released.
    if (this.dragEnded) { this.dragEnded = false; return; }

    const path = this.rowFrom(event);
    if (!path) return;

    if (event.target.closest('.tree-twisty')) {
      this.toggle(path);
      return;
    }
    // The tick box is its own gesture. Clicking it must not also select, or
    // ticking three rows would leave the third one looking like the selection
    // and the other two looking like nothing happened.
    if (this.checkable && event.target.classList.contains('tree-check')) {
      this.toggleCheck(path, event.target.checked);
      return;
    }
    this.select(path, { additive: event.ctrlKey || event.metaKey, range: event.shiftKey });
  }

  /** Ticks or unticks one path, and tells the owner. */
  setChecked(path, on) {
    if (on) this.checked.add(path);
    else this.checked.delete(path);
    this.scheduleRender();
    this.onCheck?.(this.checked);
  }

  /**
   * The gesture, as opposed to the state change.
   *
   * Every tick arrives here -- mouse and Space alike -- so an owner that wants
   * a folder to carry its contents only has to say so once. Without the hook
   * this is exactly setChecked, which is what the Explorer wants.
   */
  toggleCheck(path, on) {
    if (!this.onCheckToggle) { this.setChecked(path, on); return; }
    // The box was painted by the browser the instant it was clicked, and the
    // Set behind it may not agree until a fetch comes back. Repainting on
    // both ends puts it back where the Set says it is meanwhile, so a folder
    // that turns out to be empty does not leave a tick that means nothing.
    this.scheduleRender();
    Promise.resolve(this.onCheckToggle(path, this.nodes.get(path), on))
      // Last resort only -- the owner is expected to report its own failure.
      // This is here so a throw can never become an unhandled rejection that
      // leaves the box lying about what is ticked.
      .catch((error) => console.error('tick failed', path, error))
      .finally(() => this.scheduleRender());
  }

  /**
   * Ticks or unticks a batch as one gesture: one repaint, one notification.
   *
   * A folder tick can move hundreds of rows, and setChecked per row would be
   * hundreds of renders and hundreds of rebuilds of the owner's own model --
   * which is where the tray's labels come from, so it is not free.
   */
  /**
   * Ticks these, and everything already loaded beneath them.
   *
   * The loaded part only: what is not loaded yet is caught by loadChildren as
   * it arrives, which is the same rule applied at the other end.
   */
  checkAll(paths) {
    const spread = [];
    const walk = (at) => {
      spread.push(at);
      for (const child of this.children.get(at) ?? []) walk(child);
    };
    for (const path of paths) walk(path);
    this.setCheckedMany(spread, true);
  }

  setCheckedMany(paths, on) {
    let moved = false;
    for (const path of paths) {
      if (on) { if (!this.checked.has(path)) { this.checked.add(path); moved = true; } }
      else if (this.checked.delete(path)) moved = true;
    }
    if (!moved) return;
    this.scheduleRender();
    this.onCheck?.(this.checked);
  }

  /**
   * Everything ticked at or inside `path`.
   *
   * Unticking needs no fetch, which is the whole reason this exists: what is
   * ticked is already known, and asking the server to enumerate a folder in
   * order to untick it would make undoing the gesture slower than making it.
   */
  checkedUnder(path) {
    const prefix = `${path}/`;
    return [...this.checked].filter((at) => at === path || at.startsWith(prefix));
  }

  clearChecked() {
    this.checked.clear();
    this.scheduleRender();
    this.onCheck?.(this.checked);
  }

  onDoubleClick(event) {
    const path = this.rowFrom(event);
    if (!path) return;
    const node = this.nodes.get(path);
    if (node?.hasChildren) this.toggle(path);
    this.onActivate?.(path, node);
  }

  onRightClick(event) {
    const path = this.rowFrom(event);
    if (!path) return;
    event.preventDefault();
    if (!this.selection.has(path)) this.select(path);
    this.onContext?.(path, this.nodes.get(path), event);
  }

  select(path, { additive = false, range = false, notify = true } = {}) {
    if (range && this.focusPath) {
      const from = this.flat.findIndex((r) => r.path === this.focusPath);
      const to = this.flat.findIndex((r) => r.path === path);
      if (from >= 0 && to >= 0) {
        this.selection.clear();
        const [lo, hi] = from < to ? [from, to] : [to, from];
        for (let i = lo; i <= hi; i++) {
          if (this.flat[i].node) this.selection.add(this.flat[i].path);
        }
      }
    } else if (additive) {
      if (this.selection.has(path)) this.selection.delete(path);
      else this.selection.add(path);
    } else {
      this.selection.clear();
      this.selection.add(path);
    }

    this.focusPath = path;
    this.rowsHost.setAttribute('aria-activedescendant', `tv-${cssId(path)}`);
    this.scheduleRender();
    if (notify) this.onSelect?.(path, this.nodes.get(path), [...this.selection]);
  }

  moveFocus(delta) {
    const index = this.flat.findIndex((row) => row.path === this.focusPath);
    let next = index < 0 ? 0 : index + delta;
    next = Math.max(0, Math.min(this.flat.length - 1, next));
    while (this.flat[next] && this.flat[next].skeleton && next > 0 && next < this.flat.length - 1) {
      next += delta > 0 ? 1 : -1;
    }
    const target = this.flat[next];
    if (!target || target.skeleton) return;
    this.select(target.path);
    this.scrollTo(target.path);
  }

  onKeyDown(event) {
    const path = this.focusPath;
    const node = path ? this.nodes.get(path) : null;

    // Alt+arrow belongs to the app, not to the tree: Alt+Left/Right are Back
    // and Forward, Alt+Up/Down reorder the focused node. Falling through to the
    // switch below would run both -- Alt+Left collapsed the node AND navigated
    // away from it, which is why the collapse never appeared to work.
    if (event.altKey) return;

    switch (event.key) {
      case 'ArrowDown': event.preventDefault(); this.moveFocus(1); return;
      case 'ArrowUp': event.preventDefault(); this.moveFocus(-1); return;
      case 'Home': event.preventDefault(); this.moveFocus(-this.flat.length); return;
      case 'End': event.preventDefault(); this.moveFocus(this.flat.length); return;
      case 'PageDown': event.preventDefault(); this.moveFocus(Math.floor(this.scroller.clientHeight / ROW_H)); return;
      case 'PageUp': event.preventDefault(); this.moveFocus(-Math.floor(this.scroller.clientHeight / ROW_H)); return;

      case 'ArrowRight':
        event.preventDefault();
        if (!node) return;
        if (node.hasChildren && !this.expanded.has(path)) this.expand(path);
        else this.moveFocus(1);
        return;

      case 'ArrowLeft': {
        event.preventDefault();
        if (!node) return;
        if (node.hasChildren && this.expanded.has(path)) { this.collapse(path); return; }
        const parent = path.slice(0, path.lastIndexOf('/'));
        if (parent && this.nodes.has(parent)) { this.select(parent); this.scrollTo(parent); }
        return;
      }

      case 'Enter':
        event.preventDefault();
        if (node) this.onActivate?.(path, node);
        return;

      // Space ticks the focused row when there are boxes to tick. Without it the
      // whole picker is mouse-only, because the boxes are tabindex="-1" -- one
      // tab stop per row through 12,000 rows is not a keyboard path either.
      case ' ':
        if (!this.checkable || !node) return;
        event.preventDefault();
        this.toggleCheck(path, !this.checked.has(path));
        return;

      default: break;
    }

    // Type-ahead: printable characters jump to the next matching sibling.
    if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
      const now = Date.now();
      this.typeAhead.buffer = now - this.typeAhead.at > 1000 ? event.key : this.typeAhead.buffer + event.key;
      this.typeAhead.at = now;
      const needle = this.typeAhead.buffer.toLowerCase();

      const start = this.flat.findIndex((row) => row.path === this.focusPath);
      for (let i = 1; i <= this.flat.length; i++) {
        const entry = this.flat[(start + i) % this.flat.length];
        if (entry?.node?.name?.toLowerCase().startsWith(needle)) {
          this.select(entry.path);
          this.scrollTo(entry.path);
          break;
        }
      }
    }
  }

  /* ============================================================
     DRAG

     Two gestures that must never be mistaken for each other:

       reorder -- drop BETWEEN two rows that share the dragged node's parent,
                  changing its index among its siblings and nothing else;
       move    -- drop ONTO a row, making that row the node's new parent.

     Everything below is arranged so that neither can happen without the user
     having seen, before releasing, exactly which one they are about to get and
     which node it lands next to. Nothing is sent to the server until pointerup,
     and what is sent is recomputed from a fresh read at that moment -- see
     app.js applyTreeDrop for why a path captured here cannot be trusted.
     ============================================================ */

  onPointerDown(event) {
    // Somebody else owns the gesture on this tree. Nothing is armed and nothing
    // is drawn -- clicking, ticking and the keyboard are untouched.
    if (!this.draggable) return;
    // Left button only; a middle-click drag is a scroll gesture in most apps
    // and a right-click drag is on its way to the context menu.
    if (event.button !== 0) return;
    // Touch is deliberately excluded. A finger drag with a 6px threshold fights
    // the scroller for every flick through a 3,000-child folder, and losing
    // that fight means moving a node instead of scrolling past it. Touch users
    // keep the context menu and the keyboard shortcuts, which do the same work.
    if (event.pointerType === 'touch') return;
    // The twisty is a button; pressing it means expand, never drag.
    if (event.target.closest('.tree-twisty')) return;

    const path = this.rowFrom(event);
    if (!path || !this.nodes.has(path)) return;

    this.press = { pointerId: event.pointerId, path, x: event.clientX, y: event.clientY };
  }

  onPointerMove(event) {
    if (this.drag) {
      if (event.pointerId !== this.drag.pointerId) return;
      this.drag.x = event.clientX;
      this.drag.y = event.clientY;
      // Re-planned here and not only in the animation frame below. A drag can
      // end in the same frame it moved in -- a fast flick, or a synthetic
      // pointer sequence -- and a plan that is one frame behind at that moment
      // is either nothing (the drop silently does not happen) or, worse, the
      // previous position's answer.
      this.updateDrag();
      // Stops the browser turning the gesture into a text selection across the
      // rows the pointer sweeps over.
      event.preventDefault();
      return;
    }
    if (!this.press || event.pointerId !== this.press.pointerId) return;
    if (Math.hypot(event.clientX - this.press.x, event.clientY - this.press.y) < DRAG_THRESHOLD) return;
    this.beginDrag(event);
  }

  onPointerUp(event) {
    if (!this.drag) { this.press = null; return; }
    if (event.pointerId !== this.drag.pointerId) return;

    const plan = this.drag.plan;
    this.cancelDrag();
    // Set whether or not the drop was legal: the click is coming either way.
    this.dragEnded = true;
    if (plan?.ok) this.onDrop?.(plan);
  }

  /**
   * Arms the drag. Draws an indicator and nothing else -- no request is made
   * and no state is changed until the pointer comes up on a legal target.
   */
  beginDrag(event) {
    const { path } = this.press;
    this.press = null;

    // A drag that starts on a row inside a multi-selection carries the whole
    // selection; one that starts outside it carries only the row grabbed, and
    // deliberately does not change the selection -- a drag that silently
    // reselects would make the next Delete act on something else.
    const paths = this.selection.has(path) && this.selection.size > 1
      ? this.flat.filter((row) => row.node && this.selection.has(row.path)).map((row) => row.path)
      : [path];

    // Snapshotted here and checked again at drop time. These are the fields
    // that identify a node well enough to notice it has been swapped for
    // another of the same name -- see applyTreeDrop.
    const nodes = paths
      .map((p) => this.nodes.get(p))
      .filter(Boolean)
      .map((node) => ({
        path: node.path, name: node.name, kind: node.kind, type: node.type ?? null,
      }));

    this.drag = {
      pointerId: event.pointerId,
      paths, nodes,
      x: event.clientX, y: event.clientY,
      plan: null,
      hoverPath: null, hoverAt: 0,
      // Refusals that no drop position can fix. Computed once so the badge says
      // the same honest thing wherever the pointer goes, rather than blaming
      // whichever row happens to be underneath.
      blocked: this.refuseDragOutright(nodes),
      // Whether "drop between two rows" is on the table at all. Settled once,
      // here, because it depends on the drag and on how the tree is being
      // displayed -- never on which row the pointer is over.
      canOrder: nodes.length === 1 && nodes[0].kind === 'Property' && this.showingTrueOrder(),
    };

    // Best-effort: capture keeps the gesture alive when the pointer leaves the
    // window, but a pointer that has already been released -- or a synthetic
    // one -- makes this throw, and losing the capture is not a reason to lose
    // the drag. The window listeners work either way.
    try { this.rowsHost.setPointerCapture(event.pointerId); } catch { /* no capture; carry on */ }
    this.scroller.dataset.dragging = 'true';
    this.updateDrag();
    this.dragTick();
  }

  /**
   * The refusals that depend only on what is being dragged.
   *
   * The directory case is not a policy choice: WzEditService.Transfer has no
   * branch for a WzDirectory at all -- it accepts a property into a property
   * container, or an image into a directory, and throws for anything else. A
   * drag that offered folders would be offering an operation the backend
   * cannot perform, and the user would only find out after the drop.
   */
  refuseDragOutright(nodes) {
    if (!nodes.length) return 'Nothing to move.';
    for (const node of nodes) {
      if (node.kind === 'File')
        return 'An archive is not inside anything, so it cannot be moved. Close it instead.';
      if (node.kind === 'Directory')
        return `Folders cannot be moved between archives or into other folders, so ${node.name} stays where it is.`;
      const file = this.fileFor(node.path);
      if (file?.readOnly)
        return `${file.name} is open for reference only, so nothing in it can be moved.`;
    }
    return null;
  }

  /**
   * The open-file record an absolute path belongs to, for the read-only check.
   *
   * Read from the id map rather than from the root row, because a merged
   * family's members have no root row: their nodes are drawn under the family
   * and their paths still start with their own file id. Looking it up in the
   * tree meant the read-only pre-check went quiet for exactly the archives most
   * likely to be somebody else's client.
   */
  fileFor(path) {
    return this.filesById?.get(path.split('/')[0])
        ?? this.nodes.get(path.split('/')[0])?.fileInfo
        ?? null;
  }

  /**
   * One frame of a live drag: scroll if we are near an edge, re-hit-test,
   * repaint.
   *
   * Driven off requestAnimationFrame rather than off pointermove because both
   * of the other things that move the target do so without the pointer moving
   * at all -- the auto-scroll below, and a folder opening under the cursor. A
   * stale indicator during either is exactly the "I dropped it somewhere else"
   * bug this whole file is trying to avoid.
   */
  dragTick() {
    if (!this.drag) return;
    this.drag.frame = requestAnimationFrame(() => this.dragTick());

    this.autoScroll();
    this.updateDrag();
  }

  /** Re-answers "what would release do?" and redraws the answer. */
  updateDrag() {
    if (!this.drag) return;
    const hit = this.hitTest(this.drag.y);
    this.drag.plan = this.planDrop(hit);
    this.considerHoverExpand(hit);
    this.paintDrag(hit);
  }

  /**
   * Scrolls the pane when the pointer nears its top or bottom edge.
   *
   * Nothing here touches the virtualizer: setting scrollTop fires the scroller's
   * own listener, which schedules the same render a wheel would. A folder with
   * 3,331 children is 93,000 pixels tall, so without this a move to the far end
   * means dropping the node somewhere, scrolling, and dragging it again -- two
   * structural edits where the user wanted one.
   */
  autoScroll() {
    const rect = this.scroller.getBoundingClientRect();
    const above = rect.top + AUTOSCROLL_EDGE - this.drag.y;
    const below = this.drag.y - (rect.bottom - AUTOSCROLL_EDGE);

    let delta = 0;
    if (above > 0) delta = -Math.min(AUTOSCROLL_MAX, (above / AUTOSCROLL_EDGE) * AUTOSCROLL_MAX);
    else if (below > 0) delta = Math.min(AUTOSCROLL_MAX, (below / AUTOSCROLL_EDGE) * AUTOSCROLL_MAX);
    if (delta) this.scroller.scrollTop += delta;
  }

  /**
   * Which row is under the pointer, and how far down it.
   *
   * Measured against the sizer rather than read off the element under the
   * cursor. The sizer is one undisplaced box spanning the whole virtual list,
   * so `(y - top) / ROW_H` is the index into this.flat directly -- no lookup
   * through a pooled row whose dataset.path belongs to whatever it was recycled
   * for last. It is also correct mid-scroll, which elementFromPoint is not:
   * the transform on the row host is applied a frame behind the scroll.
   */
  hitTest(clientY) {
    if (!this.flat.length) return null;
    const rect = this.sizer.getBoundingClientRect();
    // Zero height means the pane is showing Pinned or Unsaved changes instead
    // of the tree; there is nothing here to drop on.
    if (!rect.height) return null;

    const raw = (clientY - rect.top) / ROW_H;
    const index = Math.min(this.flat.length - 1, Math.max(0, Math.floor(raw)));
    const entry = this.flat[index];
    return { index, entry, fraction: Math.min(1, Math.max(0, raw - index)) };
  }

  /**
   * Turns a pointer position into the operation that release would perform,
   * or into the reason there isn't one.
   *
   * Every branch returns a sentence. A drag that simply shows a "no" cursor
   * teaches nothing, and the user's next move is to try the same thing harder.
   */
  planDrop(hit) {
    const drag = this.drag;
    if (drag.blocked) return { ok: false, reason: drag.blocked };
    if (!hit || !hit.entry || hit.entry.skeleton) return { ok: false, reason: 'Drop onto a node.' };

    const target = hit.entry.node;
    const names = drag.nodes.length === 1 ? drag.nodes[0].name : `${drag.nodes.length} nodes`;

    // Where in the row are we? A drag that can never be ordered -- several
    // nodes, an image, or a tree sorted by name -- gets no edge bands at all,
    // so the whole row means "into" and there is no strip along the boundary
    // that refuses everything.
    let zone = 'into';
    if (drag.canOrder) {
      if (hit.fraction < EDGE_BAND) zone = 'before';
      else if (hit.fraction > 1 - EDGE_BAND) zone = 'after';
      else if (!this.accepts(target, drag.nodes).ok) zone = hit.fraction < 0.5 ? 'before' : 'after';
    }

    // Advice, not a refusal: the drop below is still legal, and this only says
    // why the other gesture is unavailable.
    const hint = !drag.canOrder && drag.nodes.length === 1 && drag.nodes[0].kind === 'Property'
      ? this.whyNoOrdering()
      : null;

    const plan = zone === 'into'
      ? this.planReparent(target, names)
      : this.planOrder(target, zone, names);

    // Attached to refusals as well as to accepted drops. Someone aiming at the
    // gap between two rows and getting "Int cannot hold children" has been told
    // what went wrong with the drop they did not mean to make; the hint is the
    // only thing that answers the one they did.
    if (hint) plan.hint = hint;
    return plan;
  }

  /** "Drop onto this row": the row becomes the dragged nodes' parent. */
  planReparent(target, names) {
    const drag = this.drag;

    for (const node of drag.nodes) {
      if (target.path === node.path)
        return { ok: false, reason: `${node.name} cannot be dropped on itself.` };
      // The check the backend also makes. Worth repeating here because the
      // failure it prevents -- a subtree adopted by its own child -- is one the
      // user is most likely to attempt while the branch is expanded in front of
      // them, and an error afterwards would leave them guessing.
      if (target.path.startsWith(node.path + '/'))
        return { ok: false, reason: `${target.name} is inside ${node.name}, so it cannot become its parent.` };
      // Transfer adds the copy before removing the original, so a node dropped
      // into the parent it already has collides with itself: it is auto-renamed
      // to "name copy" at the end of the list and the original is deleted.
      // Nothing about the gesture says that would happen, so it is refused.
      if (parentOf(node.path) === target.path) {
        return {
          ok: false,
          reason: drag.canOrder
            ? `${node.name} is already in ${target.name} — drop between two of its siblings to reorder it.`
            : `${node.name} is already in ${target.name}.`,
        };
      }
    }

    const verdict = this.accepts(target, drag.nodes);
    if (!verdict.ok) return verdict;

    const file = this.fileFor(target.path);
    if (file?.readOnly)
      return { ok: false, reason: `${file.name} is open for reference only, so nothing can be moved into it.` };

    const crossFile = drag.nodes.some((node) => node.path.split('/')[0] !== target.path.split('/')[0]);
    return {
      ok: true, kind: 'move', zone: 'into',
      paths: drag.paths, nodes: drag.nodes,
      targetPath: target.path, targetName: target.name,
      anchorPath: null, anchor: null, position: null,
      crossFile,
      label: `Move ${names} into ${target.name}`,
    };
  }

  /** "Drop between two rows": the dragged node lands beside the row aimed at. */
  planOrder(anchor, zone, names) {
    const drag = this.drag;
    const node = drag.nodes[0];
    const anchorParent = parentOf(anchor.path);
    const sourceParent = parentOf(node.path);

    if (!anchorParent)
      return { ok: false, reason: 'An archive root has no siblings to sit between.' };
    if (anchor.path === node.path)
      return { ok: false, reason: 'Already there.' };
    if (anchor.path.startsWith(node.path + '/'))
      return { ok: false, reason: `${anchor.name} is inside ${node.name}.` };

    // Only properties have an index. WzEditService.Reorder says so outright, and
    // a directory's images are appended by AddImage with no insert-at, so there
    // is nothing to place them between.
    if (anchor.kind !== 'Property')
      return { ok: false, reason: `${anchor.name} is not a property, so nothing can be ordered against it.` };

    const parentNode = this.nodes.get(anchorParent);
    const verdict = this.accepts(parentNode, drag.nodes);
    if (!verdict.ok) return verdict;

    const file = this.fileFor(anchorParent);
    if (file?.readOnly)
      return { ok: false, reason: `${file.name} is open for reference only.` };

    if (sourceParent === anchorParent) {
      // Same parent: a pure reorder. Dropping straight above or below where it
      // already sits changes nothing, and an undo entry for a no-op is worse
      // than no entry at all.
      const siblings = this.children.get(anchorParent) ?? [];
      const from = siblings.indexOf(node.path);
      const at = siblings.indexOf(anchor.path);
      if (from >= 0 && at >= 0 && (zone === 'before' ? at - 1 === from : at + 1 === from))
        return { ok: false, reason: 'Already there.' };

      return {
        ok: true, kind: 'reorder', zone,
        paths: [node.path], nodes: drag.nodes,
        targetPath: anchorParent, targetName: parentNode?.name ?? '',
        anchorPath: anchor.path, anchor, position: zone,
        crossFile: false,
        label: `Move ${node.name} ${zone} ${anchor.name}`,
      };
    }

    return {
      ok: true, kind: 'move', zone,
      paths: [node.path], nodes: drag.nodes,
      targetPath: anchorParent, targetName: parentNode?.name ?? '',
      anchorPath: anchor.path, anchor, position: zone,
      crossFile: node.path.split('/')[0] !== anchor.path.split('/')[0],
      label: `Move ${names} ${zone} ${anchor.name}`,
    };
  }

  /**
   * Whether the rows on screen are in the archive's real order.
   *
   * "Drop between these two rows" only means anything if the gap between them
   * is a real gap in the file. Under a name sort it is not: the node would be
   * placed where the user asked and then drawn somewhere else entirely, so the
   * file changed and the screen did not.
   *
   * A filter is the same failure wearing different clothes. With siblings
   * hidden, dropping "just above delay#4" puts the node above whatever is
   * really above delay#4 -- and if that is one of the hidden rows, the visible
   * order is identical afterwards. An edit the user cannot see is an edit they
   * cannot check, so ordering is offered only when the list is whole and
   * untouched.
   */
  showingTrueOrder() {
    return this.sortMode === 'wz' && !this.nameFilter && this.typeFilter.size === 0;
  }

  /** What to change so that dropping between rows becomes available. */
  whyNoOrdering() {
    if (this.sortMode !== 'wz')
      return 'Sort by File order to drop between rows and change the order.';
    if (this.nameFilter || this.typeFilter.size)
      return 'Clear the filter to drop between rows: hidden siblings make the position ambiguous.';
    return 'Drag one property at a time to drop between rows.';
  }

  /**
   * Whether a node could hold these children -- the client's copy of the rule
   * WzEditService.Transfer enforces.
   *
   * Stated twice on purpose. The backend's copy is the one that protects the
   * archive; this one exists so the answer arrives while the pointer is still
   * down, which is the only moment the user can act on it.
   */
  accepts(target, nodes) {
    if (!target) return { ok: false, reason: 'That node is not loaded yet.' };
    const folder = target.kind === 'Directory' || target.kind === 'File';

    for (const node of nodes) {
      if (node.kind === 'Property') {
        if (target.kind === 'Image') continue;
        if (target.kind === 'Property' && PROPERTY_CONTAINERS.has(target.type)) {
          if (target.type === 'Convex' && !EXTENDED_TYPES.has(node.type)) {
            return {
              ok: false,
              reason: `A Convex holds only Vector, Canvas, Sound, SubProperty, Convex and UOL children, ` +
                      `and ${node.name} is ${node.type}. It would be lost on the next save.`,
            };
          }
          continue;
        }
        return {
          ok: false,
          reason: folder
            ? `${target.name} is a folder, and a folder holds images and folders — not properties.`
            : `${target.name} is ${target.type ?? 'a leaf'}, which cannot hold children.`,
        };
      }

      if (node.kind === 'Image') {
        if (folder) continue;
        return { ok: false, reason: `An image can only go in a folder, and ${target.name} is not one.` };
      }

      return { ok: false, reason: `${node.name} cannot be moved.` };
    }
    return { ok: true };
  }

  /**
   * Opens a closed folder the pointer has rested on.
   *
   * Without it the only reachable drop targets are the ones already expanded,
   * and the user has to abandon the drag to open the folder they were aiming
   * at. Expanding is a read, so the worst case of getting this wrong is a
   * folder left open.
   */
  considerHoverExpand(hit) {
    const drag = this.drag;
    const plan = drag.plan;
    const path = plan?.ok && plan.kind === 'move' && plan.zone === 'into' ? plan.targetPath : null;

    if (path !== drag.hoverPath) {
      drag.hoverPath = path;
      drag.hoverAt = performance.now();
      return;
    }
    if (!path || this.expanded.has(path)) return;
    if (performance.now() - drag.hoverAt < HOVER_EXPAND_MS) return;

    drag.hoverAt = Infinity;   // one attempt per hover, however long it takes
    const node = this.nodes.get(path);
    if (node?.hasChildren) this.expand(path);
  }

  /* ---------- drag chrome ---------- */

  /**
   * Draws the two things the user reads before releasing: where the node will
   * land, and what will happen when it does.
   *
   * Both live outside the row pool. A class on a pooled row would be repainted
   * away by the next paintRow -- which happens on every scroll frame, and
   * auto-scroll means most drags are scrolling.
   */
  paintDrag(hit) {
    const plan = this.drag.plan;
    const ghost = this.dragGhost();

    ghost.style.transform = `translate(${this.drag.x + 14}px, ${this.drag.y + 16}px)`;
    ghost.dataset.ok = plan?.ok ? 'true' : 'false';
    ghost.firstChild.textContent = plan?.ok ? plan.label : (plan?.reason ?? '');
    ghost.lastChild.textContent = plan?.hint ?? '';
    ghost.lastChild.hidden = !ghost.lastChild.textContent;
    this.scroller.dataset.drop = plan?.ok ? 'ok' : 'no';

    const line = this.dropLine();
    const box = this.dropBox();
    if (!plan?.ok || !hit) { line.hidden = true; box.hidden = true; return; }

    if (plan.zone === 'into') {
      line.hidden = true;
      box.hidden = false;
      box.style.transform = `translateY(${hit.index * ROW_H}px)`;
      box.style.setProperty('--lvl', String(hit.entry.level));
      return;
    }

    box.hidden = true;
    line.hidden = false;
    line.style.transform = `translateY(${(hit.index + (plan.zone === 'after' ? 1 : 0)) * ROW_H}px)`;
    line.style.setProperty('--lvl', String(hit.entry.level));
  }

  dragGhost() {
    if (!this._ghost) {
      // On the body, not in the pane: it follows the cursor past the pane's
      // edge, and a pane with overflow:auto would clip it there.
      this._ghost = el('div', { class: 'drag-ghost', role: 'status', 'aria-live': 'polite' },
        el('strong', {}), el('span', { class: 'drag-ghost-hint' }));
      document.body.append(this._ghost);
    }
    return this._ghost;
  }

  dropLine() {
    if (!this._dropLine) {
      this._dropLine = el('div', { class: 'drop-line', 'aria-hidden': 'true' });
      this.sizer.append(this._dropLine);
    }
    return this._dropLine;
  }

  dropBox() {
    if (!this._dropBox) {
      this._dropBox = el('div', { class: 'drop-box', 'aria-hidden': 'true' });
      this.sizer.append(this._dropBox);
    }
    return this._dropBox;
  }

  /** Tears the gesture down. Safe to call at any point, including twice. */
  cancelDrag() {
    this.press = null;
    if (!this.drag) return;

    cancelAnimationFrame(this.drag.frame);
    try { this.rowsHost.releasePointerCapture(this.drag.pointerId); } catch { /* already gone */ }
    this.drag = null;

    delete this.scroller.dataset.dragging;
    delete this.scroller.dataset.drop;
    if (this._ghost) this._ghost.remove();
    this._ghost = null;
    if (this._dropLine) this._dropLine.hidden = true;
    if (this._dropBox) this._dropBox.hidden = true;
  }
}

/** The parent of a session path, or '' for an archive root. */
function parentOf(path) {
  const cut = path.lastIndexOf('/');
  return cut <= 0 ? '' : path.slice(0, cut);
}

/** aria-activedescendant needs an id, and WZ names contain almost anything. */
function cssId(path) {
  return path.replace(/[^a-zA-Z0-9_-]/g, (c) => '_' + c.charCodeAt(0).toString(16));
}

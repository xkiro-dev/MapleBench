/**
 * Search results as a place you stay, rather than a list you get one click out of.
 *
 * What this replaces and why. The search dialog rendered every hit as a button
 * whose only behaviour was `dialog.close(); navigate(hit.path)`. Three
 * consequences, all measured against the classic client with String.wz, Mob.wz
 * and Skill.wz open:
 *
 *   1. Visiting a second hit costs a whole second search. "maxHP" scans until it
 *      hits its 300-result ceiling; timed here at 6.8 s cold. Working through
 *      ten hits therefore cost ten searches — over a minute of waiting to look
 *      at ten nodes.
 *   2. A hit showed its name and an ancestor chain with the archive deliberately
 *      stripped, so with three archives open you could not tell where a hit came
 *      from until you had already jumped there and lost the list.
 *   3. A hit never showed the value it matched on. Searching a number gave a
 *      screen of nodes with no indication of which had which value.
 *
 * So results live in the side panel now. They survive navigation, they say which
 * archive each hit is in, they show the value, and the value is editable in
 * place — which is what turns "find the 40 mobs that need retuning" into work
 * you can actually do rather than a list you transcribe.
 *
 * Selection exists for one reason: to hand a set to the bulk-math dialog. The
 * app could already *find* a set and could not *act* on one.
 *
 * No stylesheet of its own. The compact result-only rules stay inline, matching
 * the lightweight panels in qol.js.
 */

import { api } from './api.js';
import { el, $, clear, toast, toastError, debounce, fmt, humanContext, withoutFileId } from './ui.js';
import { emptyState } from './inspector.js';

/** Hits kept per search. The server's own ceiling is 5000; this is the display cap. */
const MAX_HITS = 2000;

/**
 * Rows kept in the document at once.
 *
 * Measured before this existed: 2,000 hits built 18,018 elements, 3,670 of them
 * `<input>`. That is the whole list, every row, whether or not the panel is even
 * tall enough to show ten of them -- and the panel's own ceiling is 2,000 while
 * the scopes people actually search return 40,000+. The list is now windowed
 * against #tree-scroll, so the document holds what fits plus this much overscan
 * however many hits there are.
 */
const OVERSCAN = 8;

/** Height assumed for a row before one has been measured. Corrected on first paint. */
const ROW_HEIGHT_GUESS = 38;

export class ResultsPanel {
  constructor(app) {
    this.app = app;
    this.query = '';
    this.scope = null;          // path to search under, or null for everything
    this.hits = [];
    this.selected = new Set();  // paths
    this.truncated = false;
    this.running = false;
    this.matchValues = true;
    this.regex = false;
    this.controller = null;
    this.lastRunMs = 0;
    this.scanned = 0;
    this.rowHeight = ROW_HEIGHT_GUESS;
    this.detachList = null;
  }

  /**
   * Looked up per use rather than captured in the constructor.
   *
   * App builds its sub-controllers before it has finished installing chrome, so
   * a reference taken at construction is a reference to whatever was in the
   * document at that instant — which is how a panel ends up silently painting
   * into a detached element after any future reshuffle of boot order.
   */
  get host() { return $('#panel-results'); }

  /** Opens the panel, optionally seeding and running a query. */
  async open({ query = null, scope = undefined } = {}) {
    if (scope !== undefined) this.scope = scope;

    // setPanel treats a repeat request for the panel already showing as "put it
    // away", which is right for the rail button and wrong for Ctrl Shift F --
    // pressing the search shortcut must never be what hides the search. When it
    // is already the chosen panel we only un-collapse the pane.
    if (this.app.panel === 'results') {
      delete $('#workspace').dataset.panel;
      $('#rail-search')?.setAttribute('aria-pressed', 'true');
    } else {
      this.app.setPanel('results');
    }
    if (query !== null) {
      this.query = query;
      await this.run();
    } else {
      this.render();
      // Focusing the box rather than re-running: reopening the panel to look at
      // results you already have must not silently spend another 7 seconds.
      this.input?.focus();
      this.input?.select();
    }
  }

  async run() {
    const query = this.query.trim();
    if (!query) { this.hits = []; this.selected.clear(); this.render(); return; }

    // Each run cancels the last. Without this a slow answer for a short query
    // could land after a quick one for a longer query and repaint the list with
    // results for something the box no longer says.
    this.controller?.abort();
    const mine = new AbortController();
    this.controller = mine;

    this.running = true;
    this.cancelled = false;
    this.error = null;
    this.startedAt = performance.now();
    this.render();

    const started = performance.now();
    try {
      const found = await api.search({
        query,
        path: this.scope || null,
        matchNames: true,
        matchValues: this.matchValues,
        regex: this.regex,
        limit: MAX_HITS,
      }, { signal: mine.signal });

      if (mine.signal.aborted) return;
      this.lastRunMs = Math.round(performance.now() - started);
      this.hits = found.hits;
      this.truncated = found.truncated;
      this.scanned = found.scanned ?? 0;
      // Dropped rather than kept: a selection carried across a new search would
      // let a bulk edit act on nodes that are no longer on screen.
      this.selected.clear();
    } catch (error) {
      if (mine.signal.aborted) return;
      this.hits = [];
      this.error = error.message;
    } finally {
      if (!mine.signal.aborted) {
        this.running = false;
        this.render();
      }
    }
  }

  /**
   * Stops the run in flight and says so.
   *
   * A search over a whole client is seconds of work and there was no way to take
   * it back except to wait for it: the box accepted a new query, but the old
   * request kept the session gate the tree and the inspector queue behind, so
   * "I meant to type more" cost the whole scan anyway.
   */
  cancel() {
    if (!this.running) return;
    this.controller?.abort();
    this.controller = null;
    this.running = false;
    this.hits = [];
    this.cancelled = true;
    this.render();
  }

  /* ---------------- selection ---------------- */

  toggle(path, on) {
    if (on) this.selected.add(path); else this.selected.delete(path);
    this.paintSelectionBar();
    const row = this.host?.querySelector(`[data-hit="${CSS.escape(path)}"]`);
    if (row) row.style.background = on ? 'var(--bg-hover)' : 'transparent';
  }

  /**
   * Select-all covers the *editable* hits only.
   *
   * Selecting a Canvas or a directory would put rows into the bulk dialog that
   * can only ever be reported as skipped, which turns a clean "312 changed" into
   * a confusing "312 changed, 488 skipped" for no gain.
   */
  selectAll(on) {
    this.selected.clear();
    if (on) for (const hit of this.hits) if (hit.editable) this.selected.add(hit.path);
    this.render();
  }

  selectedPaths() { return [...this.selected]; }

  /* ---------------- rendering ---------------- */

  render() {
    if (!this.host) return;
    // Whatever the previous render hung on the shared scroller goes with it, or
    // a stale listener keeps repainting rows into a detached element.
    this.detachList?.();
    this.detachList = null;
    clear(this.host);

    this.host.append(this.buildSearchBar());

    if (this.running) {
      this.host.append(this.buildRunning());
      return;
    }

    if (this.error) {
      // Kept on the instance: paintSelectionBar and the scroll handler both
      // re-enter render(), and clearing it here meant the second pass painted an
      // empty panel over the message explaining why it was empty.
      this.host.append(emptyState('alert', 'Search failed', this.error));
      return;
    }

    if (this.cancelled) {
      this.host.append(emptyState('search', 'Search stopped',
        'Nothing was changed. Press Enter to run it again, or narrow the query first.'));
      return;
    }

    if (!this.query.trim()) {
      this.host.append(emptyState('search', 'Search every open archive',
        'Try name:maxHP to match the property rather than every description that mentions it, ' +
        'or name:maxHP >10000 to narrow it to the big ones.'));
      return;
    }

    if (!this.hits.length) {
      this.host.append(this.buildNoMatches());
      return;
    }

    this.host.append(this.buildSummary());
    this.host.append(this.selectionBar = this.buildSelectionBar());
    this.paintSelectionBar();
    this.host.append(this.buildVirtualList());
  }

  /**
   * The wait, with the two things a wait needs: what it is doing, and a way out.
   *
   * The elapsed second-counter is not decoration. A value search parses every
   * image it visits, so the honest range is "instant" to "most of a minute", and
   * a static skeleton makes those two look identical.
   */
  buildRunning() {
    const scopeText = this.scope
      ? withoutFileId(this.scope) || 'the selected node'
      : `${this.app.files?.length ?? 0} open archive${(this.app.files?.length ?? 0) === 1 ? '' : 's'}`;

    const elapsed = el('span', { class: 'num', text: '0.0s' });
    const tick = setInterval(() => {
      if (!elapsed.isConnected) { clearInterval(tick); return; }
      elapsed.textContent = `${((performance.now() - this.startedAt) / 1000).toFixed(1)}s`;
    }, 100);

    return el('div', { class: 'busy-host', style: 'padding:12px 10px' },
      el('div', { class: 'busy-bar' }),
      el('div', {
        style: 'display:flex;align-items:baseline;gap:8px;font-size:var(--fs-12);color:var(--text-2)',
      },
        el('span', { text: `Searching ${scopeText}` }),
        elapsed,
        el('span', { style: 'flex:1' }),
        el('button', {
          class: 'btn btn-sm', text: 'Stop',
          title: 'Stop the search. Nothing is changed by searching.',
          onclick: () => this.cancel(),
        })),
      this.matchValues
        ? el('div', {
            style: 'margin-top:6px;font-size:var(--fs-11);color:var(--text-3)',
            text: 'Values is on, so every image it visits has to be parsed. Turning it off answers name questions in milliseconds.',
          })
        : null,
      el('div', { style: 'margin-top:10px' },
        el('div', { class: 'skeleton skeleton-row', style: 'width:80%' }),
        el('div', { class: 'skeleton skeleton-row', style: 'width:60%;margin-top:6px' })));
  }

  /**
   * "No matches" that says what to try, because the two reasons a search over a
   * WZ client comes back empty are both fixable from this panel and neither is
   * guessable: the text is only in values and Values is off, or the scope is
   * still pinned to a node from an hour ago.
   */
  buildNoMatches() {
    const fixes = [];
    if (!this.matchValues) fixes.push('Values is off, so only names were compared — turn it on to search inside properties.');
    if (this.scope) fixes.push(`Only ${withoutFileId(this.scope) || 'one node'} was searched — clear the scope to search every open archive.`);
    if (this.regex) fixes.push('Regex is on, so the text is read as a pattern and the name:/value: prefixes are ignored.');
    if (!fixes.length) fixes.push(`${fmt.format(this.scanned)} nodes were examined and none matched. Check the spelling, or try a shorter fragment.`);

    return emptyState('search', 'No matches',
      `Nothing matches "${this.query}". ` + fixes.join(' '));
  }

  buildSearchBar() {
    const input = el('input', {
      class: 'field-input',
      type: 'search',
      placeholder: 'name:maxHP >10000',
      value: this.query,
      // The examples are in the placeholder and the empty state, not in a help
      // panel: the grammar is two prefixes, and anything that needs a manual to
      // explain two prefixes is the wrong grammar.
      title: 'name:foo · value:foo · >N <N >=N =N !=N · plain text searches both, as before',
      style: 'width:100%',
    });
    this.input = input;

    // Debounced live search on names is fine; a value search parses images and
    // costs seconds, so that one waits for Enter.
    const live = debounce(() => {
      this.query = input.value;
      if (!this.matchValues) this.run();
    }, 220);

    input.addEventListener('input', live);
    input.addEventListener('keydown', (event) => {
      if (event.key !== 'Enter') return;
      event.preventDefault();
      this.query = input.value;
      this.run();
    });

    const scopeLabel = this.scope
      ? decodeURIComponent(this.scope.split('/').slice(1).join('/')) || 'this archive'
      : null;

    const check = (labelText, field, tip) => {
      const box = el('input', {
        type: 'checkbox',
        checked: this[field],
        onchange: (event) => { this[field] = event.currentTarget.checked; this.run(); },
      });
      return el('label', { class: 'check-row', title: tip }, box, labelText);
    };

    return el('div', { style: 'padding:8px 10px;display:flex;flex-direction:column;gap:8px' },
      el('div', { class: 'input-wrap' }, input),
      el('div', { style: 'display:flex;gap:12px;align-items:center;flex-wrap:wrap;font-size:var(--fs-11)' },
        check('Values', 'matchValues', 'Also match property values. Slower: it has to parse every image it visits.'),
        check('Regex', 'regex', 'Treat the text as a regular expression. Prefixes are off in this mode.')),
      // Scope is shown only when it is not "everything", because a line that
      // always says the same thing is a line nobody reads.
      scopeLabel
        ? el('div', {
            style: 'display:flex;align-items:center;gap:6px;font-size:var(--fs-11);color:var(--text-3)',
          },
            el('span', { text: `under ${scopeLabel}`, style: 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap' }),
            el('button', {
              class: 'btn btn-sm btn-ghost', text: 'Clear',
              onclick: () => { this.scope = null; this.run(); },
            }))
        : null);
  }

  /**
   * What the search found, and — when it stopped early — what it therefore did
   * not look at.
   *
   * The old line read "2000 results · stopped early, narrow the search for more",
   * under a heading that says "Search every open archive". Both halves were
   * misleading at once. Measured on a client with eight archives open: every one
   * of the 2,000 hits came from Etc.wz, because the cap was reached inside the
   * first archive and the other seven were never opened — and narrowing the
   * query is advice that returns *fewer* results, not the missing ones. So this
   * now names the archives the hits actually came from, names the open archives
   * that were never reached, and offers the two things that do help: scope the
   * search, or search names only.
   */
  buildSummary() {
    const line = el('div', { style: 'padding:2px 10px 4px;font-size:var(--fs-11);color:var(--text-3)' },
      el('b', { style: 'color:var(--text-2);font-weight:600', text: `${fmt.format(this.hits.length)} result${this.hits.length === 1 ? '' : 's'}` }),
      el('span', {
        // Only when it is a number worth reading. "in 0.0s" is noise, and it is
        // also the case where the elapsed time was never the question.
        text: (this.lastRunMs >= 100 ? ` in ${(this.lastRunMs / 1000).toFixed(1)}s` : '') +
              (this.scanned ? ` · ${fmt.format(this.scanned)} examined` : ''),
      }));

    if (!this.truncated) return line;

    // Which open archives are represented, and which are simply absent from a
    // list that stopped early? An archive with no hits and an archive never
    // opened look identical in the rows; only this can tell them apart.
    const reached = new Set(this.hits.map((hit) => hit.file).filter(Boolean));
    const missed = this.scope
      ? []
      : (this.app.files ?? []).map((file) => file.name).filter((name) => !reached.has(name));

    const notice = el('div', {
      class: 'result-cap',
      style: 'margin:0 10px 6px;padding:7px 9px;border-radius:var(--r-sm);' +
             'border:1px solid var(--warn-border,var(--border));background:var(--warn-bg,var(--bg-subtle));' +
             'font-size:var(--fs-11);line-height:1.45;color:var(--text-2)',
    },
      el('b', { text: `Stopped at the ${fmt.format(MAX_HITS)}-result cap.` }),
      ' ',
      missed.length
        ? el('span', {}, 'These are all from ',
            el('b', { text: [...reached].join(', ') || 'one archive' }),
            `. ${missed.length === 1 ? 'This archive was' : `These ${missed.length} archives were`} never reached: `,
            el('b', { text: missed.join(', ') }),
            '.')
        : el('span', { text: 'There are more matches than are shown.' }));

    // The actions that actually recover the missing archives, offered rather
    // than described -- but as compact chips, because this panel is ~270px wide
    // and five full-width buttons push the results themselves off the screen.
    const actions = el('div', { style: 'display:flex;gap:4px;margin-top:7px;flex-wrap:wrap;align-items:center' });
    if (missed.length) {
      actions.append(el('span', { style: 'font-size:var(--fs-10);color:var(--text-3)', text: 'Search only:' }));
      for (const name of missed.slice(0, 4)) {
        const file = (this.app.files ?? []).find((f) => f.name === name);
        actions.append(el('button', {
          class: 'btn btn-sm',
          style: 'padding:1px 7px;font-size:var(--fs-10)',
          text: name.replace(/\.wz$/i, ''),
          title: `Run the same query with the scope set to ${name}, so the 2,000 are spent there.`,
          onclick: () => { this.scope = file?.id ?? null; this.run(); },
        }));
      }
      if (missed.length > 4) actions.append(el('span', { style: 'font-size:var(--fs-10);color:var(--text-3)', text: `+${missed.length - 4} more` }));
    }
    if (this.matchValues) {
      actions.append(el('button', {
        class: 'btn btn-sm btn-ghost',
        style: 'padding:1px 7px;font-size:var(--fs-10)',
        text: 'Names only',
        title: 'Much faster, and usually what a truncated search was really asking.',
        onclick: () => { this.matchValues = false; this.run(); },
      }));
    }
    if (actions.childElementCount) notice.append(actions);

    return el('div', {}, line, notice);
  }

  buildSelectionBar() {
    const editable = this.hits.filter((h) => h.editable).length;

    const all = el('input', {
      type: 'checkbox',
      disabled: editable === 0,
      onchange: (event) => this.selectAll(event.currentTarget.checked),
    });

    const count = el('span', { style: 'font-size:var(--fs-11);color:var(--text-2);font-weight:600' });

    const bulk = el('button', {
      class: 'btn btn-sm btn-primary',
      text: 'Set value…',
      // Ctrl E from the panel does the same thing; both routes go through
      // app.bulkEdit so there is one dialog and one code path to the write.
      title: 'Apply one operation to every selected value · Ctrl E',
      onclick: () => this.app.bulkEdit(this.selectedPaths()),
    });

    const copy = el('button', {
      class: 'btn btn-sm', text: 'Copy paths',
      onclick: async () => {
        const text = this.selectedPaths().map(decodeURIComponent).join('\n');
        try {
          await navigator.clipboard.writeText(text);
          toast(`Copied ${this.selected.size} path${this.selected.size === 1 ? '' : 's'}.`, 'success');
        } catch (error) { toastError(error, 'Could not copy'); }
      },
    });

    const clearSel = el('button', {
      class: 'btn btn-sm btn-ghost', text: 'Clear',
      onclick: () => this.selectAll(false),
    });

    const hint = el('span', {
      style: 'font-size:var(--fs-10);color:var(--text-3);white-space:nowrap',
      hidden: true,
    });

    this.selectionParts = { all, count, bulk, copy, clearSel, hint, editable };

    for (const button of [bulk, copy, clearSel]) button.style.cssText += ';padding:2px 8px;font-size:var(--fs-11)';

    // Two explicit rows rather than one that wraps: at panel width the single
    // flex row put "Set value…" on its own line with a stretched gap above it,
    // which read as a floating button belonging to nothing.
    return el('div', {
      style: 'display:flex;flex-direction:column;gap:5px;padding:6px 10px;' +
             'border-top:1px solid var(--border-subtle);border-bottom:1px solid var(--border-subtle)',
    },
      el('div', { style: 'display:flex;align-items:center;gap:8px' },
        // The tick box had no label at all, which made the one control that
        // unlocks this bar the least visible thing on it.
        el('label', {
          class: 'check-row',
          style: 'font-size:var(--fs-11);gap:6px',
          title: editable ? `Select all ${fmt.format(editable)} editable results` : 'No result here holds an editable value',
        }, all, el('span', { text: 'All' })),
        count,
        el('span', { style: 'flex:1' }),
        hint),
      el('div', { style: 'display:flex;align-items:center;gap:5px;flex-wrap:wrap' }, bulk, copy, clearSel));
  }

  /** Keeps the bar honest without rebuilding 2,000 rows on every checkbox. */
  paintSelectionBar() {
    const parts = this.selectionParts;
    if (!parts) return;
    const n = this.selected.size;
    parts.count.textContent = n ? `${fmt.format(n)} selected` : `${fmt.format(parts.editable)} editable`;
    parts.all.checked = n > 0 && n === parts.editable;
    parts.all.indeterminate = n > 0 && n < parts.editable;
    for (const button of [parts.bulk, parts.copy, parts.clearSel]) button.disabled = n === 0;

    // A dead button with its ordinary label is the app saying no and not saying
    // why: the bar read "286 editable" next to a greyed "Set value…", and the
    // thing that enables it was an unlabelled tick box two inches to the left.
    // So the button says what it is waiting for instead.
    const reason = parts.editable === 0
      ? 'Nothing here holds an editable value'
      : 'Tick rows to act on them';
    parts.bulk.textContent = n ? `Set value on ${fmt.format(n)}…` : 'Set value…';
    parts.bulk.title = n
      ? `Apply one operation to all ${fmt.format(n)} selected values · Ctrl E`
      : reason;
    parts.copy.title = n ? `Copy ${fmt.format(n)} paths to the clipboard` : reason;
    parts.hint.textContent = n ? '' : reason;
    parts.hint.hidden = n > 0 || parts.editable === 0;
  }

  /**
   * The hit list, windowed against the side panel's scroller.
   *
   * Every row carries a checkbox and, where the node is editable, a text input,
   * so the whole-list build was 9 elements per hit: 2,000 hits measured at
   * 18,018 elements and 3,670 inputs, for a panel that shows about twenty of
   * them. The panel already refuses to keep more than MAX_HITS, but that cap was
   * chosen to protect this render rather than because 2,000 is the interesting
   * number of results — and it is not the ceiling that matters anyway, since a
   * scoped query over a real client returns 40,000.
   *
   * The sizer carries the full height so the scrollbar tells the truth about how
   * much list there is; only the visible slice plus OVERSCAN exists as elements.
   * Rows are one line of name and one of path, so they are uniform — the height
   * is measured from the first one painted rather than assumed, and a
   * disagreement with the guess re-lays out once.
   */
  buildVirtualList() {
    const sizer = el('div', { style: 'position:relative' });
    const window_ = el('div', { style: 'position:absolute;left:0;right:0;top:0' });
    sizer.append(window_);

    const scroller = $('#tree-scroll');
    let first = -1;
    let last = -1;

    const layout = () => {
      sizer.style.height = `${this.hits.length * this.rowHeight}px`;
    };

    const paint = () => {
      if (!sizer.isConnected) return;
      const viewTop = scroller ? scroller.scrollTop : 0;
      const viewHeight = scroller ? scroller.clientHeight : 600;
      // Where the list starts inside the scrolled content, so the header block
      // above it (search bar, summary, selection bar) is accounted for without
      // this having to know anything about it. Walked rather than read off
      // sizer.offsetTop directly, because that is measured from the nearest
      // *positioned* ancestor and a stylesheet is free to make one of the panels
      // in between position:relative without telling this file.
      let listTop = 0;
      for (let node = sizer; node && node !== scroller; node = node.offsetParent) listTop += node.offsetTop;
      const start = Math.max(0, Math.floor((viewTop - listTop) / this.rowHeight) - OVERSCAN);
      const count = Math.ceil(viewHeight / this.rowHeight) + OVERSCAN * 2;
      const end = Math.min(this.hits.length, start + count);
      if (start === first && end === last) return;
      first = start; last = end;

      clear(window_);
      window_.style.transform = `translateY(${start * this.rowHeight}px)`;
      for (let i = start; i < end; i++) window_.append(this.buildRow(this.hits[i]));

      // Correct the assumed height from reality, once. A row that is taller than
      // the guess would otherwise leave a growing gap at the bottom of the list.
      const probe = window_.firstElementChild;
      if (probe) {
        const real = Math.round(probe.getBoundingClientRect().height);
        if (real > 0 && Math.abs(real - this.rowHeight) > 1) {
          this.rowHeight = real;
          layout();
          first = last = -1;
          paint();
        }
      }
    };

    layout();
    // requestAnimationFrame so the first paint measures a row that is actually
    // laid out; offsetTop is 0 until this subtree is in the document.
    requestAnimationFrame(paint);
    scroller?.addEventListener('scroll', paint, { passive: true });
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(paint) : null;
    if (scroller) observer?.observe(scroller);
    this.detachList = () => {
      scroller?.removeEventListener('scroll', paint);
      observer?.disconnect();
    };

    return sizer;
  }

  buildRow(hit) {
    const selectable = Boolean(hit.editable);

    const box = el('input', {
      type: 'checkbox',
      disabled: !selectable,
      checked: this.selected.has(hit.path),
      // Stopped so ticking a box does not also navigate away from the list you
      // are ticking boxes in.
      onclick: (event) => event.stopPropagation(),
      onchange: (event) => this.toggle(hit.path, event.currentTarget.checked),
      style: selectable ? '' : 'visibility:hidden',
    });

    const title = el('div', {
      style: 'display:flex;align-items:baseline;gap:6px;overflow:hidden',
    },
      el('span', { style: 'font-weight:600;font-size:var(--fs-12)', text: hit.name }),
      // The String.wz alias, on the same row as the name, exactly as the tree
      // renders it. The search was the last place you still had to know the id.
      hit.displayName
        ? el('span', {
            style: 'font-size:var(--fs-11);color:var(--text-3);overflow:hidden;' +
                   'text-overflow:ellipsis;white-space:nowrap',
            text: hit.displayName,
          })
        : null);

    const where = el('div', {
      style: 'font-size:var(--fs-10);color:var(--text-3);font-family:var(--font-mono);' +
             'overflow:hidden;text-overflow:ellipsis;white-space:nowrap',
      title: decodeURIComponent(hit.path),
    },
      // Archive first and emphasised: with several archives open this is the
      // field that tells you whether a hit is even the one you meant. The
      // session id is dropped -- it is the handle for the archive named right
      // beside it, so printing both said the same thing twice, once unreadably.
      hit.file ? el('b', { text: hit.file, style: 'color:var(--text-2)' }) : null,
      el('span', { text: (hit.file ? ' ' : '') + humanContext(hit.context) }));

    const row = el('div', {
      'data-hit': hit.path,
      style: 'display:flex;align-items:center;gap:8px;padding:5px 10px;border-radius:var(--r-sm);' +
             (this.selected.has(hit.path) ? 'background:var(--bg-hover)' : 'background:transparent'),
      onmouseenter: (e) => { if (!this.selected.has(hit.path)) e.currentTarget.style.background = 'var(--bg-hover)'; },
      onmouseleave: (e) => { if (!this.selected.has(hit.path)) e.currentTarget.style.background = 'transparent'; },
    },
      box,
      el('button', {
        style: 'flex:1;min-width:0;text-align:left;border:0;background:transparent;padding:0',
        title: 'Show this node in the tree',
        // Navigating no longer destroys the list, which is the entire point of
        // this file. The panel stays; the tree and inspector move.
        onclick: () => this.app.navigate(hit.path),
      }, title, where),
      this.buildValueCell(hit));

    return row;
  }

  /**
   * The matched value, editable where the node allows it.
   *
   * Editing here rather than after navigating is worth more than it looks: the
   * common shape of this work is "find the twelve that are wrong and fix them",
   * and every one of those twelve used to cost a navigation, a table scroll and
   * a click into a cell before any typing happened.
   */
  buildValueCell(hit) {
    if (hit.value == null) {
      return el('span', {
        style: 'font-size:var(--fs-11);color:var(--text-3);flex:0 0 auto',
        text: hit.type ?? '',
      });
    }

    if (!hit.editable) {
      return el('span', {
        style: 'font-size:var(--fs-11);color:var(--text-3);font-family:var(--font-mono);' +
               'max-width:96px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:0 0 auto',
        title: hit.value,
        text: hit.value,
      });
    }

    let committed = hit.value;
    const input = el('input', {
      value: hit.value,
      title: `${hit.type ?? ''} · Enter to save, Escape to revert`,
      style: 'width:88px;flex:0 0 auto;height:24px;padding:0 6px;font-family:var(--font-mono);' +
             'font-size:var(--fs-11);border:1px solid var(--border);border-radius:var(--r-sm);' +
             'background:var(--bg-field);color:var(--text-1)',
      onclick: (event) => event.stopPropagation(),
    });

    const commit = async () => {
      const next = input.value;
      if (next === committed) return;
      try {
        await api.setValue(hit.path, next);
        committed = next;
        hit.value = next;
        input.dataset.dirty = 'true';
        this.app.markDirty();
        // The tree and the table may be showing this same node. Refreshing the
        // inspector only when it is the node on screen keeps a run of edits in
        // the panel from re-fetching a node nobody is looking at.
        if (this.app.currentPath === hit.path) this.app.inspector.refresh();
      } catch (error) {
        input.value = committed;
        toastError(error, 'Could not set that value');
      }
    };

    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); input.blur(); }
      else if (event.key === 'Escape') { event.preventDefault(); input.value = committed; input.blur(); }
      // Swallowed so the panel's own list keys do not fight the caret.
      event.stopPropagation();
    });
    input.addEventListener('blur', commit);

    return input;
  }

  /** Repaints values from the server, e.g. after a bulk edit or an undo. */
  async refreshValues() {
    if (!this.hits.length) return;
    // One request per hit would be 2,000 requests. Re-running the search is one,
    // and it is also the only thing that can notice a hit that no longer matches.
    await this.run();
  }
}

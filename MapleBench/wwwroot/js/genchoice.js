/**
 * Generation chooser: the per-skill answer to the donor restore's 261
 * conflicted cases.
 *
 * The judgement it presents, exactly as the scan measured it: restoring one of
 * these skills makes its dangling links draw again AND puts the donor's older
 * art beside newer art the client still has for the same skill. Rejecting
 * keeps the skill's look consistent AND leaves its canvases silent. Neither is
 * a repair; both are looks. So each skill shows BOTH — the donor art that
 * would be restored, and the surviving newer-generation art it would land
 * beside — animated the way the client draws them (frames at the shared
 * origin, each frame's own delay; the dumper's composition rule, served by the
 * same backend that would export the GIF).
 *
 * What this screen never does:
 *   - decide for you: undecided is undecided, visibly, and the build dialog
 *     says what undecided will mean (not restored, still reported) before
 *     anything is written;
 *   - lose a half-made choice: decisions persist per client folder in
 *     localStorage and export as JSON, so a reload or another machine can
 *     carry on;
 *   - install anything: the build writes new archives beside the source and
 *     hands back the backup-first install command as text to copy.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt, modal } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';

const STORE_KEY = 'mb.genchoice';
const POLL_MS = 700;

/** Largest edge a preview box takes on screen; art scales down, never up. */
const PREVIEW_EDGE = 200;

export class GenChoiceSection {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    this.s = this.load() ?? {
      folder: '',
      donors: '',
      family: 'Skill',
      output: '',
      // Preselected because the recorded history of THIS client is that other
      // whole-archive repairs were already built from the same pristine input;
      // the ledger's refusal text is shown verbatim if it fires anyway.
      acceptSeparateRepairs: true,
    };

    /** skillId -> 'accept' | 'reject'. Keyed per client folder. */
    this.decisions = {};

    this.report = null;
    this.progress = null;
    this.result = null;
    this.mode = null;       // 'prepare' | 'build' — what the last started run was
    this.timer = null;
    /** Cancel functions for running frame animations, cleared on each render. */
    this.players = [];

    this.loadDecisions();
  }

  async open() {
    this.render();
    await this.refreshAll();
  }

  refresh() { return this.open(); }

  /* ============================================================
     STATE
     ============================================================ */

  load() {
    try { return JSON.parse(localStorage.getItem(STORE_KEY)); } catch { return null; }
  }

  save() {
    try { localStorage.setItem(STORE_KEY, JSON.stringify(this.s)); } catch { /* cosmetic */ }
  }

  decisionsKey() { return `${STORE_KEY}.decisions|${(this.s.folder || '').toLowerCase()}`; }

  loadDecisions() {
    try { this.decisions = JSON.parse(localStorage.getItem(this.decisionsKey())) ?? {}; }
    catch { this.decisions = {}; }
  }

  saveDecisions() {
    try { localStorage.setItem(this.decisionsKey(), JSON.stringify(this.decisions)); }
    catch { /* cosmetic */ }
  }

  donorList() {
    return (this.s.donors || '').split('\n').map((line) => line.trim()).filter(Boolean);
  }

  counts() {
    const groups = this.report?.groups ?? [];
    let accepted = 0; let rejected = 0;
    for (const group of groups) {
      const d = this.decisions[group.skillId];
      if (d === 'accept') accepted++;
      else if (d === 'reject') rejected++;
    }
    return { accepted, rejected, undecided: groups.length - accepted - rejected, total: groups.length };
  }

  /* ============================================================
     SERVER
     ============================================================ */

  async refreshAll() {
    try { this.report = await api.genchoiceReport(); } catch { /* 404 until prepared */ }
    try { this.result = await api.genchoiceResult(); } catch { /* 404 until built */ }
    await this.refreshProgress();
  }

  async prepare() {
    const donors = this.donorList();
    if (!this.s.folder) { toast('Name the client folder first — a folder of COPIES.', 'warn'); return; }
    if (!donors.length) {
      toast('Name at least one donor archive — without one there is no older generation to show.', 'warn');
      return;
    }
    try {
      this.mode = 'prepare';
      this.report = null;
      this.progress = await api.genchoicePrepare({
        folder: this.s.folder,
        donors,
        family: this.s.family || 'Skill',
      });
      this.loadDecisions();
      this.poll();
    } catch (error) {
      toastError(error, 'Could not start the prepare');
    }
    this.render();
  }

  async build() {
    const { accepted, rejected, undecided, total } = this.counts();
    const acceptedIds = Object.entries(this.decisions)
      .filter(([, d]) => d === 'accept').map(([id]) => id);

    const go = await new Promise((resolve) => {
      let settled = false;
      const finish = (answer) => { if (!settled) { settled = true; resolve(answer); } };
      const { dialog } = modal({
        title: 'Build the archive from this choice',
        subtitle: 'Two passes, in the recorded order: donor restore with the accepted skills, then the '
                + 'canvas-format repair over its output. New files beside the source; nothing is installed.',
        body: el('div', { class: 'gc-confirm' },
          el('p', { text: `${accepted} of ${total} conflicted skill(s) accepted — their donor art is `
                        + 'restored and those skills will mix generations, which is the choice being made.' }),
          el('p', { text: `${rejected} rejected and ${undecided} undecided — treated the same way: NOT `
                        + 'restored. Their links stay dangling and stay reported by the auditor; rejecting '
                        + 'never deletes a link.' }),
          el('p', { text: 'Every clean restore (no generation disagreement) is always included, and so is '
                        + 'every canvas-format repair.' })),
        actions: [
          { label: 'Cancel', run: () => finish(false) },
          { label: `Build with ${accepted} accepted`, class: 'btn-primary', run: () => finish(true) },
        ],
      });
      // Esc and the corner close both land here; without it the promise hangs.
      dialog.addEventListener('close', () => finish(false), { once: true });
    });
    if (!go) return;

    try {
      this.mode = 'build';
      this.result = null;
      this.progress = await api.genchoiceBuild({
        folder: this.s.folder,
        donors: this.donorList(),
        family: this.s.family || 'Skill',
        acceptedSkillIds: acceptedIds,
        output: this.s.output || null,
        acceptSeparateRepairs: !!this.s.acceptSeparateRepairs,
        confirm: true,
      });
      this.poll();
    } catch (error) {
      toastError(error, 'Could not start the build');
    }
    this.render();
  }

  async cancel() {
    try { await api.genchoiceCancel(); } catch { /* the poll re-reads it */ }
    await this.refreshProgress();
  }

  poll() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.refreshProgress(), POLL_MS);
  }

  running() {
    const state = this.progress?.chooser?.state;
    return state === 'preparing' || state === 'building';
  }

  async refreshProgress() {
    try {
      this.progress = await api.genchoiceProgress();
    } catch {
      return; // the server going away is the app's problem to report
    }
    if (this.running()) { this.poll(); this.render(); return; }

    const state = this.progress?.chooser?.state;
    if (state === 'done') {
      if (this.mode === 'prepare' && !this.report) {
        try { this.report = await api.genchoiceReport(); } catch { /* stays a 404 */ }
      }
      if (this.mode === 'build' && !this.result) {
        try { this.result = await api.genchoiceResult(); } catch { /* stays a 404 */ }
      }
      // Re-entering the section with runs long finished: pick up whatever exists.
      if (!this.mode) {
        if (!this.report) { try { this.report = await api.genchoiceReport(); } catch { /* none */ } }
        if (!this.result) { try { this.result = await api.genchoiceResult(); } catch { /* none */ } }
      }
    }
    this.render();
  }

  /* ============================================================
     DECISIONS
     ============================================================ */

  decide(skillId, decision) {
    if (this.decisions[skillId] === decision) delete this.decisions[skillId];
    else this.decisions[skillId] = decision;
    this.saveDecisions();
    this.render();
  }

  decideAll(decision) {
    for (const group of this.report?.groups ?? []) this.decisions[group.skillId] = decision;
    this.saveDecisions();
    this.render();
  }

  clearAll() {
    this.decisions = {};
    this.saveDecisions();
    this.render();
  }

  exportDecisions() {
    const payload = {
      schema: 'maplebench/genchoice-decisions@1',
      folder: this.s.folder,
      donors: this.donorList(),
      exportedUtc: new Date().toISOString(),
      decisions: this.decisions,
      accepted: Object.entries(this.decisions).filter(([, d]) => d === 'accept').map(([id]) => id),
      rejected: Object.entries(this.decisions).filter(([, d]) => d === 'reject').map(([id]) => id),
    };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
    const link = el('a', {
      href: URL.createObjectURL(blob),
      download: 'genchoice-decisions.json',
    });
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
  }

  importDecisions(json) {
    const read = JSON.parse(json);
    if (read.schema !== 'maplebench/genchoice-decisions@1') {
      throw new Error(`'${read.schema ?? '(no schema)'}' is not a decisions export this screen understands.`);
    }
    this.decisions = read.decisions ?? {};
    this.saveDecisions();
  }

  /* ============================================================
     RENDER
     ============================================================ */

  render() {
    // Stop every animation the previous render started; their elements are gone.
    for (const stop of this.players) stop();
    this.players = [];

    clear(this.host);
    this.host.className = 'stage-body genchoice';
    this.host.append(this.head());

    if (this.running()) {
      this.host.append(this.runningCard());
      return;
    }

    const state = this.progress?.chooser?.state;
    if (state === 'failed') this.host.append(this.failureCard());
    if (state === 'cancelled') {
      this.host.append(el('div', { class: 'gc-card gc-note' },
        el('p', { text: 'The last run was cancelled. Nothing it had not finished was written.' })));
    }

    if (this.result) this.host.append(this.resultCard());

    this.host.append(this.setupCard());

    if (this.report) {
      this.host.append(this.choiceBar());
      for (const group of this.report.groups) this.host.append(this.groupCard(group));
      this.host.append(this.reportNotes());
    } else if (!this.result && state !== 'failed') {
      this.host.append(emptyState(
        'replace', 'Nothing prepared yet',
        'Point this at a folder of COPIES of the Skill family plus its donors, and Prepare will decode '
        + 'both generations of every conflicted skill so you can choose between them per skill.'));
    }
  }

  head() {
    return el('div', { class: 'gc-head' },
      el('div', {},
        el('h2', { text: 'Choose between generations' }),
        el('p', {
          class: 'gc-sub',
          text: 'Restored donor art that would sit beside newer surviving art, one skill at a time: '
              + 'see both animate, accept or reject each, and build the archive from exactly that choice. '
              + 'Rejected and undecided skills keep their dangling links — reported, never deleted.',
        })));
  }

  /* ---- setup ---------------------------------------------------------- */

  setupCard() {
    const running = this.running();
    return el('div', { class: 'gc-card' },
      el('h3', { text: this.report ? 'What this choice reads' : '1 · What to read' }),
      el('div', { class: 'gc-controls' },
        el('label', { class: 'gc-label', text: 'Client folder (copies of the Skill family, read-only)' }),
        el('input', {
          class: 'input', type: 'text', value: this.s.folder, spellcheck: 'false',
          placeholder: 'C:\\MapleStory\\_genchoice_work\\client',
          oninput: (event) => {
            this.s.folder = event.target.value.trim();
            this.save();
            this.loadDecisions();
          },
        }),
        el('label', { class: 'gc-label', text: 'Donor archives, one per line, in preference order' }),
        el('textarea', {
          class: 'input gc-donors', rows: '2', spellcheck: 'false',
          placeholder: 'C:\\MapleStory\\_genchoice_work\\donors\\Skill_old.wz',
          oninput: (event) => { this.s.donors = event.target.value; this.save(); },
        }, this.s.donors || ''),
        el('div', { class: 'gc-row' },
          el('label', { class: 'gc-label', text: 'Family' }),
          el('input', {
            class: 'input gc-family', type: 'text', value: this.s.family, spellcheck: 'false',
            oninput: (event) => { this.s.family = event.target.value.trim(); this.save(); },
          }),
          el('label', { class: 'gc-label', text: 'Final output (empty = beside the source)' }),
          el('input', {
            class: 'input gc-output', type: 'text', value: this.s.output, spellcheck: 'false',
            placeholder: 'C:\\MapleStory\\_genchoice_work\\client\\Skill.genchoice.wz',
            oninput: (event) => { this.s.output = event.target.value.trim(); this.save(); },
          })),
        el('label', {
          class: 'gc-check',
          'data-tip': 'Other whole-archive repairs were already built from this client\'s pristine '
                    + 'Skill.wz, and the ledger refuses a second kind of output from the same input '
                    + 'unless this says a separate variant is wanted on purpose. Only one variant can '
                    + 'ever be installed.',
        },
          el('input', {
            type: 'checkbox',
            checked: this.s.acceptSeparateRepairs ? 'checked' : null,
            onchange: (event) => { this.s.acceptSeparateRepairs = event.target.checked; this.save(); },
          }),
          el('span', { text: 'Allow a separate variant when the ledger says this input was already repaired' })),
        el('div', { class: 'gc-actions' },
          el('button', {
            class: 'btn btn-primary', disabled: running ? 'disabled' : null,
            onclick: () => this.prepare(),
          }, icon('search', { size: 15 }), this.report ? 'Prepare again' : 'Prepare the choice'),
          el('span', {
            class: 'gc-hint',
            text: 'Read-only: the scan plus decoding both generations. Minutes over a 5 GB family.',
          }))));
  }

  /* ---- running / failed ----------------------------------------------- */

  runningCard() {
    const chooser = this.progress?.chooser ?? {};
    const inner = chooser.state === 'building'
      ? (this.progress?.restore?.state !== 'idle' && this.progress?.restore?.state !== 'done'
          ? this.progress?.restore : this.progress?.format)
      : this.progress?.restore;
    const innerLine = inner && inner.state !== 'idle'
      ? `${inner.state}${inner.phase ? ` — ${inner.phase}` : ''}`
        + (inner.imagesDone ? ` · ${fmt.format(inner.imagesDone)} images` : '')
        + (inner.canvasesDone ? ` · ${fmt.format(inner.canvasesDone)} canvases` : '')
      : '';
    return el('div', { class: 'gc-card' },
      el('h3', { text: chooser.state === 'building' ? 'Building from the choice…' : 'Preparing the choice…' }),
      el('p', { class: 'gc-sub', text: chooser.phase || 'starting' }),
      chooser.groupsTotal
        ? el('p', { class: 'gc-sub', text: `${chooser.groupsDone} of ${chooser.groupsTotal} skills decoded` })
        : null,
      innerLine ? el('p', { class: 'gc-sub gc-inner', text: innerLine }) : null,
      el('p', { class: 'gc-sub', text: `${(chooser.seconds ?? 0).toFixed(0)}s` }),
      el('button', { class: 'btn', onclick: () => this.cancel() }, 'Cancel'));
  }

  failureCard() {
    return el('div', { class: 'gc-card gc-failed' },
      el('h3', { text: 'The last run failed' }),
      el('p', { text: this.progress?.chooser?.error ?? 'No reason was reported.' }));
  }

  /* ---- the choice bar -------------------------------------------------- */

  choiceBar() {
    const { accepted, rejected, undecided, total } = this.counts();
    return el('div', { class: 'gc-bar' },
      el('div', { class: 'gc-bar-counts' },
        el('span', { class: 'gc-chip gc-chip-accept', text: `${accepted} accepted` }),
        el('span', { class: 'gc-chip gc-chip-reject', text: `${rejected} rejected` }),
        el('span', { class: 'gc-chip', text: `${undecided} undecided of ${total}` })),
      el('div', { class: 'gc-bar-actions' },
        el('button', { class: 'btn', onclick: () => this.decideAll('accept') }, 'Accept all'),
        el('button', { class: 'btn', onclick: () => this.decideAll('reject') }, 'Reject all'),
        el('button', { class: 'btn', onclick: () => this.clearAll() }, 'Clear'),
        el('button', {
          class: 'btn', onclick: () => this.exportDecisions(),
          'data-tip': 'Download the decision set as JSON — the same set localStorage carries between reloads.',
        }, icon('download', { size: 14 }), 'Export'),
        el('button', {
          class: 'btn btn-primary', disabled: this.running() ? 'disabled' : null,
          onclick: () => this.build(),
          'data-tip': 'Donor restore with the accepted skills, then the canvas-format repair over its '
                    + 'output. Writes new files beside the source; installs nothing.',
        }, icon('save', { size: 14 }), 'Build archive')));
  }

  /* ---- one skill ------------------------------------------------------- */

  groupCard(group) {
    const decision = this.decisions[group.skillId];
    return el('div', { class: 'gc-group', 'data-decision': decision ?? 'none' },
      el('div', { class: 'gc-group-head' },
        el('div', { class: 'gc-group-title' },
          el('span', { class: 'gc-skill-id', text: group.skillId }),
          group.skillName ? el('span', { class: 'gc-skill-name', text: group.skillName }) : null,
          el('span', {
            class: 'gc-skill-meta',
            text: `${group.links} link(s) · ${fmt.format(group.canvases)} silent canvas(es) · lands in `
                + `${group.targetArchive}.wz/${group.imagePath} under ${group.landsUnder} · donor `
                + `${group.donors.join(', ')}`,
          })),
        el('div', { class: 'gc-group-buttons' },
          el('button', {
            class: 'btn gc-accept', 'data-on': decision === 'accept' ? 'true' : 'false',
            onclick: () => this.decide(group.skillId, 'accept'),
            'data-tip': 'Restore this skill\'s donor art. Its links draw again; the skill mixes the two '
                      + 'generations shown here.',
          }, icon('check', { size: 14 }), 'Accept'),
          el('button', {
            class: 'btn gc-reject', 'data-on': decision === 'reject' ? 'true' : 'false',
            onclick: () => this.decide(group.skillId, 'reject'),
            'data-tip': 'Leave this skill alone. Its look stays consistent; these links stay dangling '
                      + 'and stay reported.',
          }, icon('close', { size: 14 }), 'Reject'))),
      el('div', { class: 'gc-panes' },
        this.pane('Would be restored — donor (older generation)',
          'The art the dangling links were written for. Restoring it is what Accept means.',
          group.donorSets),
        this.pane('Already there — live (newer generation)',
          'Survived the port and stays either way. This is what the restored art would sit beside.',
          group.liveSets)),
      group.notes?.length
        ? el('div', { class: 'gc-group-notes' },
            ...group.notes.map((note) => el('p', { text: note })))
        : null);
  }

  pane(title, sub, sets) {
    return el('div', { class: 'gc-pane' },
      el('h4', { text: title }),
      el('p', { class: 'gc-pane-sub', text: sub }),
      sets.length
        ? el('div', { class: 'gc-sets' }, ...sets.map((set) => this.setView(set)))
        : el('p', { class: 'gc-pane-empty', text: 'Nothing decoded for this side — the notes below say why.' }));
  }

  /**
   * One composed node: the frames absolutely positioned at their shared-origin
   * offsets inside the composed box, shown one at a time on each frame's own
   * delay. The placement numbers come from the server — the dumper's rule —
   * so this element only obeys them.
   */
  setView(set) {
    const scale = Math.min(1, PREVIEW_EDGE / Math.max(set.width, 1), PREVIEW_EDGE / Math.max(set.height, 1));
    const px = (n) => `${Math.round(n * scale)}px`;

    const frames = set.frames.map((frame) => {
      if (frame.id < 0) {
        return el('div', {
          class: 'gc-frame gc-frame-missing',
          style: `left:${px(frame.offsetX)};top:${px(frame.offsetY)};`
               + `width:${px(frame.width)};height:${px(frame.height)}`,
          'data-tip': frame.note || 'this frame did not decode',
        });
      }
      return el('img', {
        class: 'gc-frame',
        src: `/api/repair/genchoice/frame/${frame.id}`,
        alt: `${set.path}/${frame.name}`,
        style: `left:${px(frame.offsetX)};top:${px(frame.offsetY)};width:${px(frame.width)}`,
        loading: 'lazy',
      });
    });

    const box = el('div', {
      class: 'gc-anim',
      style: `width:${px(set.width)};height:${px(set.height)}`,
    }, ...frames);

    if (frames.length > 1) {
      let index = 0; let timer = null; let stopped = false;
      const show = () => {
        frames.forEach((node, i) => { node.style.visibility = i === index ? 'visible' : 'hidden'; });
        const delay = Math.max(set.frames[index].delay || 100, 20);
        index = (index + 1) % frames.length;
        if (!stopped) timer = setTimeout(show, delay);
      };
      show();
      this.players.push(() => { stopped = true; clearTimeout(timer); });
    }

    const formats = [...new Set(set.frames.map((frame) => frame.format))].join('/');
    const leaf = set.path.split('/').slice(-2).join('/');
    return el('figure', { class: 'gc-set' },
      box,
      el('figcaption', {
        class: 'gc-caption',
        'data-tip': set.path,
      },
        el('span', { class: 'gc-caption-name', text: leaf }),
        el('span', {
          text: `${set.frames.length} frame(s) · ${set.width}×${set.height} · format ${formats} · `
              + `${set.totalMs} ms${set.truncated ? ` · ${set.note}` : ''}`,
        })));
  }

  reportNotes() {
    return el('div', { class: 'gc-card gc-note' },
      el('h3', { text: 'What this choice rests on' }),
      ...(this.report.notes ?? []).map((note) => el('p', { text: note })),
      el('p', {
        text: `Beyond the choice: ${this.report.restorable} clean restore(s) are always included, `
            + `${this.report.unrestorable} link(s) no donor holds are reported and left exactly as they `
            + 'are, and every canvas-format repair is always applied.',
      }));
  }

  /* ---- the result ------------------------------------------------------ */

  resultCard() {
    const r = this.result;
    const restore = r.restore ?? {};
    const format = r.format;
    return el('div', { class: 'gc-card gc-result' },
      el('h3', { text: 'Built — verified on the saved and reopened archives, installed nowhere' }),
      el('div', { class: 'gc-result-grid' },
        this.stat('Accepted skills', `${r.acceptedSkillIds?.length ?? 0}`),
        this.stat('Rejected/undecided', `${r.rejectedSkillIds?.length ?? 0}`),
        this.stat('Links restored', `${restore.written ?? 0} (identity held ${restore.identityHeld ?? 0})`),
        this.stat('Dangling', `${restore.danglingBefore ?? 0} → ${restore.danglingAfter ?? 0}`),
        this.stat('Silent canvases', `${fmt.format(restore.danglingCanvasesBefore ?? 0)} → `
                                   + `${fmt.format(restore.danglingCanvasesAfter ?? 0)}`),
        this.stat('Format repairs', format ? `${format.repaired} (decoded ${format.decoded})` : 'pass not reached'),
        this.stat('Bystanders decoded', `${fmt.format((restore.bystandersDecoded ?? 0) + (format?.bystandersDecoded ?? 0))}`
                                      + `, failed ${(restore.bystandersFailed ?? 0) + (format?.bystandersFailed ?? 0)}`),
        this.stat('Bytes', `${fmt.format(restore.sourceBytes ?? 0)} → ${fmt.format(r.finalBytes ?? 0)}`)),
      el('p', { class: 'gc-sub', text: `Final archive: ${r.finalOutput || '(nothing was written)'}` }),
      r.installCommand ? el('div', { class: 'gc-install' },
        el('p', { class: 'gc-sub', text: 'To install — backup first, then copy. Run it yourself; this tool will not.' }),
        el('code', { text: r.installCommand }),
        el('button', {
          class: 'btn',
          onclick: () => navigator.clipboard.writeText(r.installCommand)
            .then(() => toast('Copied.', 'info'))
            .catch(() => toast('Could not reach the clipboard — select the text instead.', 'warn')),
        }, icon('copy', { size: 14 }), 'Copy')) : null,
      (restore.stillDangling?.length ?? 0) > 0 ? el('details', { class: 'gc-still' },
        el('summary', { text: `${restore.stillDangling.length} link(s) still dangling — rejected, undecided, or `
                            + 'unrestorable. Reported, never deleted.' }),
        ...restore.stillDangling.slice(0, 100).map((c) => el('p', { class: 'gc-mono', text: c.link }))) : null,
      (restore.failures?.length ?? 0) + (format?.failures?.length ?? 0) > 0
        ? el('div', { class: 'gc-failures' },
            el('h4', { text: 'Failures' }),
            ...[...(restore.failures ?? []), ...(format?.failures ?? [])].map((f) => el('p', { text: f })))
        : null,
      el('details', {},
        el('summary', { text: 'The full record — every note both passes wrote' }),
        ...(r.notes ?? []).map((note) => el('p', { class: 'gc-sub', text: note })),
        ...(restore.notes ?? []).map((note) => el('p', { class: 'gc-sub', text: note })),
        ...((format?.notes) ?? []).map((note) => el('p', { class: 'gc-sub', text: note }))));
  }

  stat(label, value) {
    return el('div', { class: 'gc-stat' },
      el('span', { class: 'gc-stat-label', text: label }),
      el('span', { class: 'gc-stat-value', text: value }));
  }
}

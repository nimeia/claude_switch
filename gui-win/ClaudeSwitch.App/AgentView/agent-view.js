// Supervised-run transcript.
//
// Drawn entirely from the lines the app posts (see TranscriptView.cs): the page
// keeps no state of its own that a reload could lose, because on load it asks for
// everything again. Model output is escaped before any markup is added, so a
// reply can format text but never inject it.
(() => {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  const feed = document.getElementById('feed');
  const emptyHint = document.getElementById('empty');
  const jump = document.getElementById('jump');
  const root = document.documentElement;
  const scroller = document.scrollingElement || root;

  const MAX_TERMINAL_ROWS = 18;
  const DIFF_LINE_LIMIT = 400;
  const TEXT_LIMIT = 20000;
  // Recent tool calls opened automatically stay open; older ones fold away so a
  // long run does not become a wall of output.
  const KEEP_OPEN = 3;

  let strings = {};
  let workDir = '';
  let busy = false;
  let applied = 0;
  let stick = true;
  let renderQueued = false;
  let pendingWrites = 0;

  const dirty = new Set();
  const terminals = new Set();
  const state = { stream: null, diag: null, plan: null, tools: new Map() };

  // ── bridge ─────────────────────────────────────────────────────────────
  const post = (message) => { if (host) host.postMessage(message); };

  function t(key, ...args) {
    let s = Object.prototype.hasOwnProperty.call(strings, key) ? strings[key] : key;
    args.forEach((a, i) => { s = s.split('{' + i + '}').join(String(a)); });
    return s;
  }

  // ── icons ──────────────────────────────────────────────────────────────
  const svg = (d) => `<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${d}</svg>`;
  const ICON = {
    execute: svg('<path d="M2.5 4.5 6 8l-3.5 3.5"/><path d="M8 12h5.5"/>'),
    read: svg('<path d="M4 1.75h5L12.25 5v9.25H4z"/><path d="M9 1.75V5h3.25"/>'),
    edit: svg('<path d="M10.5 2.5l3 3L6 13H3v-3z"/>'),
    delete: svg('<path d="M3 4.5h10M6.5 4.5V3h3v1.5M4.5 4.5l.5 9h6l.5-9"/>'),
    move: svg('<path d="M2.5 8h11M10 4.5 13.5 8 10 11.5"/>'),
    search: svg('<circle cx="7" cy="7" r="4.25"/><path d="m10.25 10.25 3.25 3.25"/>'),
    fetch: svg('<circle cx="8" cy="8" r="5.75"/><path d="M2.25 8h11.5M8 2.25c1.75 1.6 2.5 3.5 2.5 5.75s-.75 4.15-2.5 5.75C6.25 12.15 5.5 10.25 5.5 8S6.25 3.85 8 2.25"/>'),
    think: svg('<path d="M8 2.5V4M8 12v1.5M2.5 8H4M12 8h1.5M4.1 4.1l1 1M10.9 10.9l1 1M4.1 11.9l1-1M10.9 5.1l1-1"/><circle cx="8" cy="8" r="2"/>'),
    other: svg('<circle cx="8" cy="8" r="2.25"/>'),
    chev: svg('<path d="m6 3.5 4.5 4.5L6 12.5"/>'),
    notice: svg('<circle cx="8" cy="8" r="6"/><path d="M8 7.25v3.5M8 5.25v.01"/>'),
    warning: svg('<path d="M8 2 14.5 13.5h-13z"/><path d="M8 6.5v3M8 11.5v.01"/>'),
    error: svg('<circle cx="8" cy="8" r="6"/><path d="m5.75 5.75 4.5 4.5M10.25 5.75l-4.5 4.5"/>'),
  };

  // ── scrolling ──────────────────────────────────────────────────────────
  // Follow the newest output only while the reader is already at the bottom;
  // someone scrolled up to read must not be yanked away by the next chunk.
  const nearBottom = () => scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 60;
  window.addEventListener('scroll', () => { stick = nearBottom(); jump.hidden = stick; }, { passive: true });
  jump.addEventListener('click', () => { stick = true; scrollToEnd(); });
  function scrollToEnd() { scroller.scrollTop = scroller.scrollHeight; jump.hidden = true; }
  function settleScroll() { if (stick) scrollToEnd(); else if (feed.childElementCount) jump.hidden = false; }

  // ── markdown ───────────────────────────────────────────────────────────
  const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ESCAPES[c]);

  // Characters that would start emphasis or a link, spelled as entities inside
  // code spans so the passes over the whole line leave code alone.
  const CODE_ENTITIES = { '*': '&#42;', '_': '&#95;', '~': '&#126;', '[': '&#91;', ']': '&#93;' };

  function inline(src) {
    // Code spans first, emphasis second and over the whole line: bold wrapped
    // around a code span has to work, and nothing inside one may read as markup.
    const html = String(src).split(/(`[^`\n]+`)/).map((part, i) => (i % 2 === 1
      ? '<code>' + esc(part.slice(1, -1)).replace(/[*_~[\]]/g, (c) => CODE_ENTITIES[c]) + '</code>'
      : esc(part))).join('');
    return html
      .replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a data-url="$2">$1</a>')
      .replace(/\*\*([^*\n]+?)\*\*/g, '<strong>$1</strong>')
      .replace(/(^|[^*\w])\*([^*\s][^*\n]*?)\*(?!\w)/g, '$1<em>$2</em>')
      .replace(/~~([^~\n]+?)~~/g, '<del>$1</del>');
  }

  const inlineLines = (text) => text.split('\n').map(inline).join('<br>');
  const FENCE = /^\s{0,3}(`{3,}|~{3,})\s*([^\s`]*)/;
  const LIST = /^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/;
  const TABLE_SEPARATOR = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;

  function markdown(src) {
    const lines = String(src).replace(/\r\n?/g, '\n').split('\n');
    const out = [];
    let para = [];
    const flush = () => {
      if (para.length) out.push('<p>' + para.map(inline).join('<br>') + '</p>');
      para = [];
    };

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      let m;

      if ((m = FENCE.exec(line))) {
        flush();
        const close = new RegExp('^\\s{0,3}' + (m[1][0] === '`' ? '`' : '~') + '{' + m[1].length + ',}\\s*$');
        const body = [];
        for (i++; i < lines.length && !close.test(lines[i]); i++) body.push(lines[i]);
        // An unclosed fence is a reply still streaming: show it as code so far.
        out.push(codeBlock(body.join('\n'), m[2]));
        continue;
      }
      if (!line.trim()) { flush(); continue; }
      if ((m = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/.exec(line))) {
        flush();
        out.push(`<h${m[1].length}>${inline(m[2])}</h${m[1].length}>`);
        continue;
      }
      if (/^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(line)) { flush(); out.push('<hr>'); continue; }
      if (/^\s{0,3}>/.test(line)) {
        flush();
        const quote = [];
        for (; i < lines.length && /^\s{0,3}>/.test(lines[i]); i++) quote.push(lines[i].replace(/^\s{0,3}>\s?/, ''));
        i--;
        out.push('<blockquote>' + markdown(quote.join('\n')) + '</blockquote>');
        continue;
      }
      if (line.includes('|') && i + 1 < lines.length && TABLE_SEPARATOR.test(lines[i + 1])) {
        flush();
        const head = cells(line);
        const aligns = cells(lines[i + 1]).map((c) => (/^:-+:$/.test(c) ? 'center' : /-:$/.test(c) ? 'right' : ''));
        const rows = [];
        for (i += 2; i < lines.length && lines[i].includes('|') && lines[i].trim(); i++) rows.push(cells(lines[i]));
        i--;
        out.push(table(head, aligns, rows));
        continue;
      }
      if (LIST.test(line)) {
        flush();
        const items = [];
        for (; i < lines.length; i++) {
          const lm = LIST.exec(lines[i]);
          if (lm) {
            items.push({ indent: lm[1].replace(/\t/g, '    ').length, ordered: /\d/.test(lm[2]), start: parseInt(lm[2], 10), text: lm[3] });
          } else if (items.length && /^\s{2,}\S/.test(lines[i])) {
            items[items.length - 1].text += '\n' + lines[i].trim();
          } else {
            break;
          }
        }
        i--;
        out.push(list(items));
        continue;
      }
      para.push(line);
    }
    flush();
    return out.join('');
  }

  function cells(row) {
    let s = row.trim();
    if (s.startsWith('|')) s = s.slice(1);
    if (s.endsWith('|') && !s.endsWith('\\|')) s = s.slice(0, -1);
    return s.split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, '|'));
  }

  function table(head, aligns, rows) {
    const cell = (tag, text, i) => `<${tag}${aligns[i] ? ` style="text-align:${aligns[i]}"` : ''}>${inline(text)}</${tag}>`;
    return '<table><thead><tr>' + head.map((h, i) => cell('th', h, i)).join('') + '</tr></thead><tbody>'
      + rows.map((r) => '<tr>' + head.map((_, i) => cell('td', r[i] ?? '', i)).join('') + '</tr>').join('')
      + '</tbody></table>';
  }

  function list(items) {
    let html = '';
    const stack = [];
    for (const item of items) {
      while (stack.length && item.indent < stack[stack.length - 1].indent) html += '</li></' + stack.pop().tag + '>';
      const top = stack[stack.length - 1];
      if (!top || item.indent > top.indent) {
        const tag = item.ordered ? 'ol' : 'ul';
        stack.push({ indent: item.indent, tag });
        html += '<' + tag + (item.ordered && item.start > 1 ? ` start="${item.start}"` : '') + '>';
      } else {
        html += '</li>';
      }
      const task = /^\[( |x|X)\]\s+([\s\S]*)$/.exec(item.text);
      if (task) {
        const done = task[1] !== ' ';
        html += `<li class="task"><span class="box${done ? ' done' : ''}">${done ? '☑' : '☐'}</span>${inlineLines(task[2])}`;
      } else {
        html += '<li>' + inlineLines(item.text);
      }
    }
    while (stack.length) html += '</li></' + stack.pop().tag + '>';
    return html;
  }

  function codeBlock(code, lang) {
    return `<div class="codeblock"><div class="bar"><span>${esc(lang || '')}</span>`
      + `<button class="copy" type="button" data-copy>${esc(t('acp.view.copy'))}</button></div>`
      + `<pre><code>${esc(code)}</code></pre></div>`;
  }

  // ── rendering loop ─────────────────────────────────────────────────────
  function requestRender() {
    if (renderQueued) return;
    renderQueued = true;
    requestAnimationFrame(renderNow);
  }

  function renderNow() {
    renderQueued = false;
    for (const block of dirty) block.render();
    dirty.clear();
    settleScroll();
    reportSettled();
  }

  function reportSettled() {
    if (renderQueued || pendingWrites > 0) return;
    post({ type: 'settled', count: applied });
  }

  function append(el) {
    feed.appendChild(el);
    emptyHint.hidden = true;
  }

  // ── prompt, replies, thinking ──────────────────────────────────────────
  function addPrompt(text) {
    const el = document.createElement('div');
    el.className = 'prompt';
    el.textContent = text;
    append(el);
  }

  function streamText(kind, text, id) {
    let block = state.stream;
    if (!block || block.kind !== kind || (id && block.id && id !== block.id)) {
      block = state.stream = kind === 'thought' ? newThought(id) : newMessage(id);
    }
    if (!block.id && id) block.id = id;
    block.text += text;
    dirty.add(block);
  }

  function newMessage(id) {
    const el = document.createElement('div');
    el.className = 'msg md';
    el.hidden = true;
    append(el);
    return {
      kind: 'assistant', id, text: '', el,
      render() {
        // The separator a new message starts with is not content.
        el.hidden = !this.text.trim();
        el.innerHTML = markdown(this.text);
      },
    };
  }

  function newThought(id) {
    const el = document.createElement('details');
    el.className = 'thought';
    el.hidden = true;
    el.innerHTML = `<summary>${ICON.chev}<span class="label"></span><span class="preview"></span></summary><div class="body"></div>`;
    el.querySelector('.label').textContent = t('acp.view.thinking');
    const preview = el.querySelector('.preview');
    const body = el.querySelector('.body');
    append(el);
    return {
      kind: 'thought', id, text: '', el,
      render() {
        const flat = this.text.replace(/\s+/g, ' ').trim();
        el.hidden = !flat;
        preview.textContent = flat.length > 160 ? '…' + flat.slice(-160) : flat;
        body.textContent = this.text.trim();
      },
    };
  }

  // ── tool calls ─────────────────────────────────────────────────────────
  const cssToken = (s) => String(s || '').replace(/[^\w-]/g, '');
  const textOf = (item) => (item && item.content && typeof item.content.text === 'string' ? item.content.text : '');
  const isRunning = (tool) => tool.status === 'pending' || tool.status === 'in_progress';

  function upsertTool(d, created) {
    if (!d || !d.id) return;
    let tool = state.tools.get(d.id);
    if (!tool) {
      tool = createTool(d.id);
      state.tools.set(d.id, tool);
      created = true;
    }
    if (typeof d.title === 'string') tool.title = d.title;
    if (typeof d.kind === 'string') tool.kind = d.kind;
    if (typeof d.status === 'string') tool.status = d.status;
    if (Array.isArray(d.content)) tool.content = d.content;
    if (typeof d.terminalOutput === 'string') tool.output = (tool.output || '') + d.terminalOutput;
    if (d.exitCode !== undefined && d.exitCode !== null) tool.exitCode = d.exitCode;

    renderToolHead(tool);
    if (tool.open) renderToolBody(tool);
    else autoOpen(tool);
  }

  function createTool(id) {
    const el = document.createElement('section');
    el.className = 'tool';
    el.innerHTML = `<div class="head"><span class="icon"></span><span class="title"></span><span class="meta"></span><span class="state"></span><span class="chev">${ICON.chev}</span></div><div class="body"></div>`;
    const tool = {
      id, el, head: el.firstElementChild, body: el.lastElementChild,
      title: '', kind: 'other', status: 'pending', content: [], output: null, exitCode: null,
      open: false, userToggled: false, autoOpened: false, term: null,
    };
    tool.head.addEventListener('click', () => {
      if (!hasBody(tool)) return;
      tool.userToggled = true;
      setOpen(tool, !tool.open);
    });
    append(el);
    return tool;
  }

  function hasBody(tool) {
    if (tool.output !== null || tool.exitCode !== null) return true;
    return tool.content.some((c) => c && (c.type === 'diff' || (c.type === 'content' && textOf(c).trim())));
  }

  const hasDiff = (tool) => tool.content.some((c) => c && c.type === 'diff');

  // Opening order, not creation order, decides what folds. A model calling
  // tools in parallel announces every call before any finishes, so a command's
  // output usually arrives after newer calls exist; ranking by creation hid
  // exactly the output that had just landed.
  let autoOpenSeq = 0;

  function autoOpen(tool) {
    if (tool.userToggled || !hasBody(tool)) return;
    if (tool.status !== 'failed' && tool.output === null && !hasDiff(tool)) return;
    tool.autoOpened = true;
    tool.openedSeq = ++autoOpenSeq;
    setOpen(tool, true);
    // Failed calls stay open: they are what someone scrolling back looks for.
    const stale = [...state.tools.values()]
      .filter((t) => t.open && t.autoOpened && !t.userToggled && t.status === 'completed')
      .sort((a, b) => b.openedSeq - a.openedSeq)
      .slice(KEEP_OPEN);
    for (const t of stale) setOpen(t, false);
  }

  // Titles carry absolute paths the window header already names. Inside the
  // working directory they read as relative ones, like the adapter's Read titles.
  function shortenPaths(text) {
    const base = String(workDir || '').replace(/[\\/]+$/, '');
    const source = String(text || '');
    if (!base || !source) return source;
    // ASCII-only folding keeps indices aligned with the original string.
    const fold = (s) => s.replace(/\\/g, '/').replace(/[A-Z]/g, (c) => c.toLowerCase());
    const needle = fold(base) + '/';
    const haystack = fold(source);
    let out = '';
    let from = 0;
    for (let at = haystack.indexOf(needle); at !== -1; at = haystack.indexOf(needle, from)) {
      out += source.slice(from, at);
      from = at + needle.length;
    }
    return out + source.slice(from);
  }

  function renderToolHead(tool) {
    const { el, head } = tool;
    el.className = `tool kind-${cssToken(tool.kind)} st-${cssToken(tool.status)}`
      + (tool.open ? ' open' : '') + (hasBody(tool) ? ' expandable' : '');
    head.querySelector('.icon').innerHTML = ICON[tool.kind] || ICON.other;
    const title = head.querySelector('.title');
    const shown = shortenPaths(tool.title);
    title.innerHTML = tool.kind === 'execute' ? esc(shown) : inline(shown);
    title.title = tool.title;
    head.querySelector('.meta').textContent =
      tool.exitCode !== null && tool.exitCode !== 0 ? t('acp.view.exit', tool.exitCode) : '';

    const stateEl = head.querySelector('.state');
    const running = isRunning(tool);
    stateEl.innerHTML = (running ? '<i class="spin"></i>' : '<i class="dot"></i>') + '<span></span>';
    // A call still "running" after the run has ended never finished.
    stateEl.lastChild.textContent = running && !busy
      ? t('acp.view.status.stopped')
      : t('acp.view.status.' + cssToken(tool.status));
  }

  function setOpen(tool, open) {
    tool.open = open;
    tool.el.classList.toggle('open', open);
    if (open) {
      renderToolBody(tool);
    } else {
      disposeTerminal(tool);
      tool.body.textContent = '';
    }
  }

  function renderToolBody(tool) {
    disposeTerminal(tool);
    const body = tool.body;
    body.textContent = '';

    let terminal = false;
    for (const item of tool.content) {
      if (!item) continue;
      if (item.type === 'diff') {
        body.appendChild(diffView(item));
      } else if (item.type === 'content' && textOf(item).trim()) {
        const div = document.createElement('div');
        div.className = 'text md';
        const text = textOf(item);
        div.innerHTML = markdown(text.length > TEXT_LIMIT ? text.slice(0, TEXT_LIMIT) + '\n…' : text);
        body.appendChild(div);
      } else if (item.type === 'terminal') {
        terminal = true;
      }
    }

    if (tool.kind === 'execute' && tool.title && (terminal || tool.output !== null)) {
      const cmd = document.createElement('div');
      cmd.className = 'cmd';
      cmd.textContent = tool.title;
      body.appendChild(cmd);
    }
    if (tool.output !== null && tool.output.trim()) {
      const wrap = document.createElement('div');
      wrap.className = 'term';
      const inner = document.createElement('div');
      inner.className = 'inner';
      wrap.appendChild(inner);
      body.appendChild(wrap);
      openTerminal(tool, inner);
    } else if (tool.output !== null || (terminal && tool.exitCode !== null)) {
      const none = document.createElement('div');
      none.className = 'empty-out';
      none.textContent = t('acp.view.noOutput');
      body.appendChild(none);
    }
  }

  // ── terminals ──────────────────────────────────────────────────────────
  const ANSI_LIGHT = {
    black: '#1a1f1c', red: '#b8382c', green: '#2e7d4f', yellow: '#946500', blue: '#2f6aa8', magenta: '#8a3fa8', cyan: '#1d7682', white: '#6b7670',
    brightBlack: '#5a645e', brightRed: '#d24b3e', brightGreen: '#35925c', brightYellow: '#ad7d12', brightBlue: '#3b7fc4', brightMagenta: '#a152c2', brightCyan: '#23909e', brightWhite: '#8a9890',
  };
  const ANSI_DARK = {
    black: '#2a322e', red: '#e07a6a', green: '#6fb88a', yellow: '#e0b855', blue: '#7aa7d6', magenta: '#c39ad8', cyan: '#6cc4c9', white: '#c9d4ce',
    brightBlack: '#8a9890', brightRed: '#f0958a', brightGreen: '#8fd3a8', brightYellow: '#f0ce7a', brightBlue: '#9cc2ea', brightMagenta: '#d6b6e6', brightCyan: '#8fdadd', brightWhite: '#ecf2ee',
  };

  function terminalTheme() {
    const css = getComputedStyle(root);
    const background = css.getPropertyValue('--term-bg').trim() || '#f1f2f2';
    const dark = root.dataset.mode === 'dark';
    return Object.assign({
      background,
      foreground: css.getPropertyValue('--text').trim() || '#1a1f1c',
      cursor: background,
      cursorAccent: background,
      selectionBackground: dark ? 'rgba(122,150,176,0.45)' : 'rgba(122,143,168,0.32)',
    }, dark ? ANSI_DARK : ANSI_LIGHT);
  }

  const stripAnsi = (s) => s.replace(/\x1b\[[0-9;?]*[ -/]*[@-~]/g, '');

  function openTerminal(tool, container) {
    const text = tool.output.replace(/\n+$/, '');
    if (typeof Terminal === 'undefined' || typeof FitAddon === 'undefined') {
      const pre = document.createElement('pre');
      pre.textContent = stripAnsi(text);
      container.appendChild(pre);
      return;
    }
    const term = new Terminal({
      disableStdin: true,
      convertEol: true,
      cursorBlink: false,
      cursorStyle: 'bar',
      cursorInactiveStyle: 'none',
      fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Microsoft YaHei UI", monospace',
      fontSize: 12,
      lineHeight: 1.2,
      scrollback: 10000,
      rows: 1,
      cols: 80,
      theme: terminalTheme(),
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(container);
    const record = { term, fit, pending: true };
    tool.term = record;
    terminals.add(record);
    pendingWrites++;
    fitWidth(record);
    // Hide the cursor: this is a record of output, not a prompt.
    term.write('\x1b[?25l' + text, () => {
      if (record.pending) { record.pending = false; pendingWrites--; }
      fitHeight(record);
      settleScroll();
      reportSettled();
    });
  }

  function fitWidth(record) {
    const dims = record.fit.proposeDimensions();
    if (dims && dims.cols > 0 && dims.cols !== record.term.cols) record.term.resize(dims.cols, record.term.rows);
  }

  function fitHeight(record) {
    const buffer = record.term.buffer.active;
    let rows = buffer.length;
    while (rows > 1) {
      const line = buffer.getLine(rows - 1);
      if (line && line.translateToString(true).trim()) break;
      rows--;
    }
    record.term.resize(record.term.cols, Math.max(1, Math.min(rows, MAX_TERMINAL_ROWS)));
    if (rows > MAX_TERMINAL_ROWS) record.term.scrollToBottom();
  }

  function disposeTerminal(tool) {
    const record = tool.term;
    if (!record) return;
    tool.term = null;
    terminals.delete(record);
    if (record.pending) { record.pending = false; pendingWrites--; }
    try { record.term.dispose(); } catch (err) { console.error(err); }
  }

  let resizeTimer = 0;
  new ResizeObserver(() => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      for (const record of terminals) { fitWidth(record); fitHeight(record); }
    }, 80);
  }).observe(feed);

  // ── diffs ──────────────────────────────────────────────────────────────
  function displayPath(path) {
    const norm = (s) => String(s).replace(/\\/g, '/').replace(/\/+$/, '');
    const base = norm(workDir);
    const full = norm(path);
    if (base && full.toLowerCase().startsWith(base.toLowerCase() + '/')) return full.slice(base.length + 1);
    return String(path);
  }

  function lineDiff(a, b) {
    if (!a.length) return b.map((line) => ['+', line]);
    // Past this size the table costs more than the picture is worth.
    if (a.length * b.length > 250000) return [...a.map((l) => ['-', l]), ...b.map((l) => ['+', l])];
    const n = a.length, m = b.length;
    const lcs = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
      }
    }
    const ops = [];
    let i = 0, j = 0;
    while (i < n && j < m) {
      if (a[i] === b[j]) { ops.push([' ', a[i]]); i++; j++; }
      else if (lcs[i + 1][j] >= lcs[i][j + 1]) { ops.push(['-', a[i]]); i++; }
      else { ops.push(['+', b[j]]); j++; }
    }
    while (i < n) ops.push(['-', a[i++]]);
    while (j < m) ops.push(['+', b[j++]]);
    return ops;
  }

  function diffView(item) {
    const wrap = document.createElement('div');
    wrap.className = 'diff';
    const path = document.createElement('div');
    path.className = 'path';
    path.textContent = displayPath(item.path || '');
    wrap.appendChild(path);

    const before = item.oldText == null ? [] : String(item.oldText).split('\n');
    const after = String(item.newText ?? '').split('\n');
    const ops = lineDiff(before, after);
    const frag = document.createDocumentFragment();
    for (const [op, text] of ops.slice(0, DIFF_LINE_LIMIT)) {
      const line = document.createElement('div');
      line.className = 'ln' + (op === '+' ? ' add' : op === '-' ? ' del' : '');
      const gutter = document.createElement('span');
      gutter.className = 'g';
      gutter.textContent = op === ' ' ? '' : op;
      const code = document.createElement('span');
      code.className = 'c';
      code.textContent = text || ' ';
      line.append(gutter, code);
      frag.appendChild(line);
    }
    wrap.appendChild(frag);
    if (ops.length > DIFF_LINE_LIMIT) {
      const more = document.createElement('div');
      more.className = 'more';
      more.textContent = t('acp.view.more', ops.length - DIFF_LINE_LIMIT);
      wrap.appendChild(more);
    }
    return wrap;
  }

  // ── plan ───────────────────────────────────────────────────────────────
  function upsertPlan(d) {
    const entries = d && Array.isArray(d.entries) ? d.entries.filter(Boolean) : [];
    if (!state.plan) {
      const el = document.createElement('section');
      el.className = 'plan';
      state.plan = { el };
    }
    const el = state.plan.el;
    const done = entries.filter((e) => e.status === 'completed').length;
    const pct = entries.length ? Math.round((done * 100) / entries.length) : 0;
    el.innerHTML = `<div class="head"><span>${esc(t('acp.view.plan'))}</span><span class="count">${done}/${entries.length}</span>`
      + `<span class="bar"><i style="width:${pct}%"></i></span></div><ul></ul>`;
    const ul = el.querySelector('ul');
    for (const entry of entries) {
      const status = cssToken(entry.status || 'pending');
      const li = document.createElement('li');
      li.className = status;
      li.innerHTML = `<span class="mark">${status === 'completed' ? '✓' : status === 'in_progress' ? '●' : '○'}</span><span class="txt"></span>`;
      li.lastChild.textContent = typeof entry.content === 'string' ? entry.content : '';
      ul.appendChild(li);
    }
    // Re-appended on every change: the plan belongs next to the work it describes.
    append(el);
  }

  // ── supervisor lines ───────────────────────────────────────────────────
  function addSystem(kind, text) {
    const el = document.createElement('div');
    el.className = 'sys ' + kind;
    el.innerHTML = `<span class="ic">${ICON[kind] || ICON.notice}</span><span class="tx"></span>`;
    el.lastChild.textContent = String(text).trim();
    append(el);
  }

  function addDiagnostic(text) {
    let group = state.diag;
    if (!group) {
      const el = document.createElement('details');
      el.className = 'diag';
      el.innerHTML = '<summary></summary><pre></pre>';
      group = state.diag = { el, lines: [] };
      append(el);
    }
    group.lines.push(String(text).replace(/^\[adapter\]\s*/, ''));
    group.el.querySelector('summary').textContent = t('acp.view.diag', group.lines.length);
    group.el.querySelector('pre').textContent = group.lines.join('\n');
  }

  // ── events ─────────────────────────────────────────────────────────────
  function apply(ev) {
    const d = ev.d || null;
    switch (ev.k) {
      case 'prompt':
        state.stream = null; state.diag = null;
        addPrompt(d && typeof d.text === 'string' ? d.text : String(ev.t).replace(/^›\s?/, ''));
        break;
      case 'assistant':
      case 'thought':
        state.diag = null;
        streamText(ev.k, String(ev.t), d && d.messageId);
        break;
      case 'tool':
        state.stream = null; state.diag = null;
        upsertTool(d, true);
        break;
      case 'toolUpdate':
        state.diag = null;
        upsertTool(d, false);
        break;
      case 'plan':
        state.stream = null; state.diag = null;
        upsertPlan(d);
        break;
      case 'notice':
      case 'warning':
      case 'error':
        if (d && d.source === 'adapter') {
          addDiagnostic(ev.t);
        } else {
          state.stream = null; state.diag = null;
          addSystem(ev.k, ev.t);
        }
        break;
      default:
        break;
    }
  }

  function reset() {
    for (const tool of state.tools.values()) disposeTerminal(tool);
    feed.textContent = '';
    dirty.clear();
    state.stream = null;
    state.diag = null;
    state.plan = null;
    state.tools.clear();
    applied = 0;
  }

  function setBusy(value) {
    busy = value;
    document.body.classList.toggle('busy', value);
    for (const tool of state.tools.values()) if (isRunning(tool)) renderToolHead(tool);
  }

  // ── theme ──────────────────────────────────────────────────────────────
  function hexToRgb(hex) {
    const m = /^#?([0-9a-f]{6})$/i.exec(String(hex).trim());
    const n = m ? parseInt(m[1], 16) : 0;
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }

  function mix(a, b, weight) {
    const x = hexToRgb(a), y = hexToRgb(b);
    return '#' + x.map((c, i) => Math.round(c * (1 - weight) + y[i] * weight).toString(16).padStart(2, '0')).join('');
  }

  function applyTheme(theme) {
    if (!theme || !theme.colors) return;
    const c = theme.colors;
    const dark = theme.mode === 'dark';
    root.dataset.mode = dark ? 'dark' : 'light';
    const set = (name, value) => { if (value) root.style.setProperty(name, value); };
    set('--bg', c.bg); set('--bg-app', c.bgApp); set('--hover', c.hover); set('--selected', c.selected);
    set('--border', c.border); set('--border-soft', c.borderSoft);
    set('--text', c.text); set('--text-2', c.text2); set('--muted', c.muted);
    set('--primary', c.primary); set('--primary-soft', c.primarySoft); set('--on-primary', c.onPrimary);
    set('--success', c.success); set('--warning', c.warning); set('--warning-soft', c.warningSoft);
    set('--danger', c.danger); set('--danger-soft', c.dangerSoft); set('--focus', c.focus);
    // Computed here rather than with color-mix(): xterm.js needs plain colours.
    set('--code-bg', dark ? mix(c.bg, '#000000', 0.22) : mix(c.bg, c.text, 0.045));
    set('--card-bg', dark ? mix(c.bg, '#ffffff', 0.025) : mix(c.bg, c.text, 0.018));
    set('--term-bg', dark ? mix(c.bg, '#000000', 0.3) : mix(c.bg, c.text, 0.05));
    if (theme.font) set('--font', `"${theme.font}", "Segoe UI", system-ui, sans-serif`);
    for (const record of terminals) record.term.options.theme = terminalTheme();
  }

  // ── wiring ─────────────────────────────────────────────────────────────
  function onHostMessage(message) {
    if (!message || typeof message !== 'object') return;
    switch (message.type) {
      case 'init':
        strings = message.strings || {};
        workDir = message.workDir || '';
        applyTheme(message.theme);
        reset();
        setBusy(!!message.busy);
        emptyHint.textContent = t('acp.view.empty');
        emptyHint.hidden = feed.childElementCount > 0;
        jump.textContent = '↓ ' + t('acp.view.jump');
        requestRender();
        break;
      case 'events':
        for (const ev of message.events || []) {
          try { apply(ev); } catch (err) { console.error(err, ev); }
          applied++;
        }
        requestRender();
        break;
      case 'theme':
        applyTheme(message.theme);
        break;
      case 'busy':
        setBusy(!!message.busy);
        break;
      default:
        break;
    }
  }

  document.addEventListener('click', (e) => {
    const target = e.target instanceof Element ? e.target : null;
    if (!target) return;
    const link = target.closest('a[data-url]');
    if (link) {
      e.preventDefault();
      post({ type: 'openUrl', url: link.dataset.url });
      return;
    }
    const copy = target.closest('button[data-copy]');
    if (copy) {
      const code = copy.closest('.codeblock')?.querySelector('code');
      if (!code) return;
      post({ type: 'copy', text: code.textContent });
      copy.textContent = t('acp.view.copied');
      setTimeout(() => { copy.textContent = t('acp.view.copy'); }, 1200);
    }
  });

  if (host) host.addEventListener('message', (e) => onHostMessage(e.data));
  post({ type: 'ready' });
})();

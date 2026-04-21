/**
 * Pulse - Agent Chat (3-pane)
 * Pane 1: Conversations (localStorage sessions)
 * Pane 2: Chat (SignalR + Gemini streaming)
 * Pane 3: Files (code-block extraction per session)
 */

// ═══════════════════════════════════════════════════════════════════════
// SESSION STORE  (localStorage, namespaced per user, max 50 sessions)
// ═══════════════════════════════════════════════════════════════════════
const SessionStore = (() => {
    // Key is user-scoped so switching accounts never leaks session titles
    const userId = document.getElementById('sessionData')?.dataset.userId || 'guest';
    const KEY    = `bp_sessions_v3_${userId}`;

    function getAll() {
        try { return JSON.parse(localStorage.getItem(KEY) || '[]'); }
        catch { return []; }
    }
    function save(list) { localStorage.setItem(KEY, JSON.stringify(list.slice(0, 50))); }

    function upsert(id, patch = {}) {
        const all = getAll();
        const i   = all.findIndex(s => s.id === id);
        const now = new Date().toISOString();
        if (i >= 0) { Object.assign(all[i], patch, { lastAt: now }); }
        else        { all.unshift({ id, title: 'New conversation', createdAt: now, lastAt: now, ...patch }); }
        save(all);
    }
    function remove(id) { save(getAll().filter(s => s.id !== id)); }

    return { getAll, upsert, remove };
})();

// ═══════════════════════════════════════════════════════════════════════
// ATTACHMENT STORE  (pending files for the next message)
// ═══════════════════════════════════════════════════════════════════════
const AttachmentStore = (() => {
    let items = [];  // { id, name, size, content, loading, error }

    const TEXT_EXTS = new Set([
        'txt','md','csv','json','xml','html','htm','js','ts','jsx','tsx',
        'py','cs','sql','yaml','yml','log','ini','sh','bash','ps1','bat',
        'r','rb','go','rs','swift','kt','java','cpp','c','h','css','scss',
        'sass','less','php','env','toml','conf','cfg','vue','svelte','dart',
    ]);
    const SERVER_EXTS = new Set(['pdf','docx','xlsx','xls']);

    let _nextId = 0;

    function isTextFile(name) {
        return TEXT_EXTS.has(name.split('.').pop().toLowerCase());
    }
    function isServerFile(name) {
        return SERVER_EXTS.has(name.split('.').pop().toLowerCase());
    }
    function formatSize(bytes) {
        if (bytes < 1024)        return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
    }
    function extIcon(name) {
        const e = name.split('.').pop().toLowerCase();
        const m = { pdf:'📄', docx:'📝', xlsx:'📊', xls:'📊', csv:'📊', md:'📝', json:'📋', sql:'🗄️', py:'🐍', cs:'💻', js:'🟨', ts:'🔷' };
        return m[e] ?? '📎';
    }

    async function add(file) {
        if (file.size > 10 * 1024 * 1024) {
            alert(`"${file.name}" is too large — maximum file size is 10 MB.`);
            return;
        }
        const id = ++_nextId;

        if (isTextFile(file.name)) {
            const item = { id, name: file.name, size: file.size, content: '', loading: true, error: false };
            items.push(item);
            try {
                item.content = await readAsText(file);
                item.loading = false;
            } catch {
                item.error   = true;
                item.loading = false;
                item.content = `[Could not read file]`;
            }
        } else if (isServerFile(file.name)) {
            const item = { id, name: file.name, size: file.size, content: '', loading: true, error: false };
            items.push(item);
            try {
                const fd = new FormData();
                fd.append('file', file);
                const resp = await fetch('/api/agent/upload-file', { method: 'POST', body: fd });
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    throw new Error(err.error ?? 'Upload failed');
                }
                const data    = await resp.json();
                item.content  = data.content;
                item.loading  = false;
            } catch (err) {
                item.error   = true;
                item.loading = false;
                item.content = `[Extraction failed: ${err.message}]`;
            }
        } else {
            alert(`".${file.name.split('.').pop()}" files are not supported.\n\nSupported: text/code files, PDF, DOCX, XLSX.`);
            return;
        }

        return id;
    }

    function readAsText(file) {
        return new Promise((resolve, reject) => {
            const r = new FileReader();
            r.onload  = () => resolve(r.result);
            r.onerror = () => reject(r.error);
            r.readAsText(file, 'UTF-8');
        });
    }

    function remove(id)    { items = items.filter(i => i.id !== id); }
    function clear()       { items = []; }
    function getAll()      { return [...items]; }
    function hasAny()      { return items.length > 0; }
    function isLoading()   { return items.some(i => i.loading); }

    return { add, remove, clear, getAll, hasAny, isLoading, isTextFile, isServerFile, formatSize, extIcon };
})();

// ═══════════════════════════════════════════════════════════════════════
// FILE STORE  (in-memory, keyed by sessionId)
// ═══════════════════════════════════════════════════════════════════════
const FileStore = (() => {
    const map = new Map();   // sessionId -> FileEntry[]

    const LANG_META = {
        javascript: { icon: 'JS', ext: 'js'   },  js:   { icon: 'JS', ext: 'js'   },
        typescript: { icon: 'TS', ext: 'ts'   },  ts:   { icon: 'TS', ext: 'ts'   },
        python:     { icon: 'PY', ext: 'py'   },  py:   { icon: 'PY', ext: 'py'   },
        html:       { icon: 'HTML', ext: 'html' },
        css:        { icon: 'CSS', ext: 'css'  },
        json:       { icon: 'JSON', ext: 'json'},
        csv:        { icon: 'CSV', ext: 'csv'  },
        sql:        { icon: 'SQL', ext: 'sql'  },
        bash:       { icon: 'SH',  ext: 'sh'  },  shell:{ icon: 'SH', ext: 'sh'  },  sh: { icon: 'SH', ext: 'sh' },
        yaml:       { icon: 'YAML', ext: 'yaml'},  yml:  { icon: 'YAML', ext: 'yaml'},
        markdown:   { icon: 'MD',  ext: 'md'  },  md:   { icon: 'MD',  ext: 'md'  },
        xml:        { icon: 'XML', ext: 'xml'  },
        csharp:     { icon: 'CS',  ext: 'cs'  },  cs:   { icon: 'CS',  ext: 'cs'  },
        java:       { icon: 'JAVA', ext: 'java'},
        go:         { icon: 'GO',  ext: 'go'  },
        rust:       { icon: 'RS',  ext: 'rs'  },
    };

    const LANG_EMOJI = {
        JS: '🟨', TS: '🔷', PY: '🐍', HTML: '🌐', CSS: '🎨',
        JSON: '📋', CSV: '📊', SQL: '🗄️', SH: '⚙️', YAML: '📄',
        MD: '📝', XML: '📄', CS: '💻', JAVA: '☕', GO: '🐹', RS: '🦀'
    };

    function extractFromMarkdown(sid, markdown) {
        const existing = map.get(sid) || [];
        let added = 0;
        const nextId = () => existing.length + added + 1;

        // ── Code blocks ───────────────────────────────────────────────
        const codeRe = /```(\w+)?\n([\s\S]*?)```/g;
        let m;
        while ((m = codeRe.exec(markdown)) !== null) {
            const lang    = (m[1] || 'txt').toLowerCase();
            const content = m[2].trim();
            if (content.length < 30) continue;
            if (lang === 'chart') continue;

            const meta  = LANG_META[lang] || { icon: lang.toUpperCase().slice(0,4), ext: lang || 'txt' };
            const emoji = LANG_EMOJI[meta.icon] || '📄';
            const id    = nextId();
            const name  = `code-${id}.${meta.ext}`;

            if (!existing.some(f => f.content === content)) {
                existing.push({ id, name, lang, ext: meta.ext, icon: emoji, content, isGenerated: false });
                added++;
            }
        }

        // ── Generated file download links ─────────────────────────────
        // Matches: [Download filename.pdf](/api/pdf/download/abc123)
        const dlRe = /\[([^\]]+)\]\((\/api\/(?:pdf|word|excel)\/download\/([a-f0-9]{32}))\)/gi;
        let dlm;
        while ((dlm = dlRe.exec(markdown)) !== null) {
            const rawLabel = dlm[1];
            const url      = dlm[2];
            if (existing.some(f => f.url === url)) continue;

            const cleanName = rawLabel.replace(/^download\s+/i, '').trim();
            const ext       = cleanName.split('.').pop().toLowerCase();
            const iconMap   = { pdf: '📄', docx: '📝', xlsx: '📊' };
            const icon      = iconMap[ext] || '📎';

            existing.push({ id: nextId(), name: cleanName, url, icon, isGenerated: true });
            added++;
        }

        if (added > 0) map.set(sid, existing);
        return added;
    }

    function get(sid) { return map.get(sid) || []; }
    function clear(sid) { map.delete(sid); }

    return { extractFromMarkdown, get, clear };
})();

// ═══════════════════════════════════════════════════════════════════════
// CHART RENDERER  (marked.js custom renderer + Chart.js engine)
// ═══════════════════════════════════════════════════════════════════════

// ── marked.js: intercept ```chart fenced blocks ───────────────────────
if (typeof marked !== 'undefined') {
    marked.use({
        renderer: {
            code({ text, lang }) {
                if (lang !== 'chart') return false;   // default rendering for all other langs

                // During streaming the JSON may be incomplete → show a spinner
                let valid = false;
                try { JSON.parse(text); valid = true; } catch { /* partial */ }

                if (!valid) {
                    return '<div class="bp-chart-wrapper bp-chart-streaming">' +
                           '<div class="bp-chart-loading">' +
                           '<span class="spinner-border spinner-border-sm me-2" role="status"></span>' +
                           'Building chart\u2026</div></div>';
                }

                // Encode spec as base64 so it survives innerHTML round-trips
                const encoded = btoa(unescape(encodeURIComponent(text)));
                return `<div class="bp-chart-wrapper" data-chart-spec="${encoded}">` +
                       '<div class="bp-chart-loading">📊 Rendering chart\u2026</div>' +
                       '<canvas class="bp-chart-canvas" style="display:none"></canvas>' +
                       '</div>';
            }
        }
    });
}

// ── Initialise Chart.js on every pending canvas in a container ────────
function renderPendingCharts(container) {
    if (typeof Chart === 'undefined' || !container) return;
    container
        .querySelectorAll('.bp-chart-wrapper[data-chart-spec]:not([data-chart-rendered])')
        .forEach(wrapper => {
            const canvas = wrapper.querySelector('.bp-chart-canvas');
            if (!canvas) return;
            try {
                const existing = Chart.getChart(canvas);
                if (existing) existing.destroy();

                const spec = JSON.parse(decodeURIComponent(atob(wrapper.dataset.chartSpec)));
                wrapper.querySelectorAll('.bp-chart-loading').forEach(el => el.remove());
                canvas.style.display = '';
                new Chart(canvas, buildChartConfig(spec));
                wrapper.setAttribute('data-chart-rendered', '1');
            } catch (e) {
                wrapper.innerHTML =
                    `<div class="bp-chart-error">⚠ Chart error: ${escapeHtmlChart(e.message)}</div>`;
            }
        });
}

function escapeHtmlChart(t) {
    return String(t)
        .replace(/&/g,'&amp;').replace(/</g,'&lt;')
        .replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// ── Build a Chart.js config object from the agent spec ────────────────
function buildChartConfig(spec) {
    const PALETTE = [
        '#0d6efd','#6610f2','#198754','#fd7e14',
        '#dc3545','#0dcaf0','#ffc107','#6f42c1',
        '#20c997','#d63384',
    ];

    const rgba = (hex, a) => {
        const r = parseInt(hex.slice(1,3), 16);
        const g = parseInt(hex.slice(3,5), 16);
        const b = parseInt(hex.slice(5,7), 16);
        return `rgba(${r},${g},${b},${a})`;
    };

    const type    = (spec.type || 'bar').toLowerCase();
    const isPolar = ['pie','doughnut','polarArea'].includes(type);
    const isLine  = type === 'line';
    const isRadar = type === 'radar';

    const datasets = (spec.datasets || []).map((ds, i) => {
        const base = ds.color || PALETTE[i % PALETTE.length];
        return {
            label:            ds.label ?? `Series ${i + 1}`,
            data:             ds.data  ?? [],
            backgroundColor:  isPolar  ? PALETTE.map(c => rgba(c, 0.75))
                             : isLine && ds.fill ? rgba(base, 0.15)
                             : base,
            borderColor:      isPolar  ? PALETTE.map(c => rgba(c, 1))
                             : base,
            borderWidth:      isLine || isRadar ? 2 : 1,
            fill:             ds.fill  ?? false,
            tension:          isLine   ? 0.35 : 0,
            pointRadius:      isLine   ? 3 : undefined,
            pointHoverRadius: isLine   ? 5 : undefined,
            hoverOffset:      isPolar  ? 6 : undefined,
        };
    });

    return {
        type,
        data: { labels: spec.labels ?? [], datasets },
        options: {
            responsive:          true,
            maintainAspectRatio: true,
            aspectRatio:         spec.aspectRatio ?? (isPolar ? 1.6 : 2),
            animation:           { duration: 500 },
            plugins: {
                title: spec.title
                    ? { display: true, text: spec.title,
                        font: { size: 13, weight: 'bold' },
                        padding: { bottom: 10 } }
                    : { display: false },
                legend: {
                    position: isPolar ? 'right' : 'bottom',
                    labels: { boxWidth: 12, padding: 14, font: { size: 11 } },
                },
                tooltip: { mode: isPolar ? 'nearest' : 'index', intersect: false },
            },
            scales: (isPolar || isRadar) ? {} : {
                y: {
                    beginAtZero: true,
                    stacked:     spec.stacked ?? false,
                    grid:        { color: '#f0f0f0' },
                    ticks:       { font: { size: 11 } },
                },
                x: {
                    stacked: spec.stacked ?? false,
                    grid:    { display: false },
                    ticks:   { font: { size: 11 } },
                },
            },
        },
    };
}

// ═══════════════════════════════════════════════════════════════════════
// CHAT
// ═══════════════════════════════════════════════════════════════════════
const Chat = (() => {
    let connection             = null;
    let sessionId              = null;
    let currentAssistantBubble = null;
    let currentAssistantContent= '';
    let isBusy                 = false;
    let isFirstMessage         = true;
    let streamingCharts        = [];   // chart specs pushed via SignalR during current response

    // ── Sub-agent nested bubbles ──────────────────────────────────────
    let subAgentBubbles          = new Map();  // agentName → <details> element
    let subAgentContents         = new Map();  // agentName → accumulated raw text
    let currentSubAgentsContainer = null;

    let messagesEl, inputEl, sendBtn, statusBadge, typingEl;

    // ── Init ──────────────────────────────────────────────────────────
    function init(sid) {
        sessionId  = sid;
        messagesEl = document.getElementById('chatMessages');
        inputEl    = document.getElementById('messageInput');
        sendBtn    = document.getElementById('sendBtn');
        statusBadge= document.getElementById('statusBadge');
        typingEl   = document.getElementById('typingIndicator');

        initTheme();
        bindEvents();
        bindAttachmentEvents();
        setupPaneToggles();
        renderSessions();
        loadHistory();
        connectSignalR();
        renderFilesPane();
    }

    // ── Theme toggle ──────────────────────────────────────────────────
    function initTheme() {
        const saved = localStorage.getItem('bp-theme');
        if (saved === 'dark') applyTheme('dark');
        else                  applyTheme('light');

        document.getElementById('themeToggleBtn')
            ?.addEventListener('click', () => {
                const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
                applyTheme(isDark ? 'light' : 'dark');
            });
    }

    function applyTheme(theme) {
        if (theme === 'dark') {
            document.documentElement.setAttribute('data-theme', 'dark');
            document.getElementById('themeIconMoon').style.display = 'none';
            document.getElementById('themeIconSun').style.display  = '';
        } else {
            document.documentElement.removeAttribute('data-theme');
            document.getElementById('themeIconMoon').style.display = '';
            document.getElementById('themeIconSun').style.display  = 'none';
        }
        localStorage.setItem('bp-theme', theme);
    }

    // ── Attachment events ─────────────────────────────────────────────
    function bindAttachmentEvents() {
        const attachBtn  = document.getElementById('attachBtn');
        const fileInput  = document.getElementById('fileInput');
        const inputArea  = document.querySelector('.chat-input-area');

        // Button click → open file picker
        attachBtn?.addEventListener('click', () => fileInput?.click());

        // File picker change
        fileInput?.addEventListener('change', async () => {
            if (!fileInput.files?.length) return;
            await processFiles(Array.from(fileInput.files));
            fileInput.value = '';   // reset so same file can be re-selected
        });

        // Drag-and-drop onto the input area
        inputArea?.addEventListener('dragover', e => {
            e.preventDefault();
            inputArea.classList.add('drag-over');
        });
        inputArea?.addEventListener('dragleave', e => {
            if (!inputArea.contains(e.relatedTarget))
                inputArea.classList.remove('drag-over');
        });
        inputArea?.addEventListener('drop', async e => {
            e.preventDefault();
            inputArea.classList.remove('drag-over');
            const files = Array.from(e.dataTransfer?.files ?? []);
            if (files.length) await processFiles(files);
        });
    }

    async function processFiles(files) {
        for (const file of files) {
            await AttachmentStore.add(file);
            renderAttachmentChips();   // render after each file so loading state shows immediately
        }
        renderAttachmentChips();
    }

    function renderAttachmentChips() {
        const chipsEl   = document.getElementById('attachmentChips');
        const attachBtn = document.getElementById('attachBtn');
        if (!chipsEl) return;

        const items = AttachmentStore.getAll();
        if (!items.length) {
            chipsEl.style.display = 'none';
            chipsEl.innerHTML     = '';
            attachBtn?.classList.remove('has-files');
            return;
        }

        attachBtn?.classList.add('has-files');
        chipsEl.style.display = 'flex';
        chipsEl.innerHTML = items.map(item => {
            const cls    = item.loading ? 'loading' : item.error ? 'error' : '';
            const icon   = item.loading ? '⏳' : item.error ? '⚠️' : AttachmentStore.extIcon(item.name);
            const label  = item.loading ? 'Extracting…' : item.error ? 'Error' : AttachmentStore.formatSize(item.size);
            return `<span class="attach-chip ${cls}" data-id="${item.id}">
                <span class="attach-chip-icon">${icon}</span>
                <span class="attach-chip-name" title="${escapeHtml(item.name)}">${escapeHtml(item.name)}</span>
                <span class="attach-chip-size">${label}</span>
                <button class="attach-chip-remove" data-id="${item.id}" title="Remove">✕</button>
            </span>`;
        }).join('');

        chipsEl.querySelectorAll('.attach-chip-remove').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                AttachmentStore.remove(parseInt(btn.dataset.id));
                renderAttachmentChips();
            });
        });
    }

    // ── Events ────────────────────────────────────────────────────────
    function bindEvents() {
        sendBtn.addEventListener('click', sendMessage);
        inputEl.addEventListener('keydown', e => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); }
        });
        inputEl.addEventListener('input', () => {
            inputEl.style.height = 'auto';
            inputEl.style.height = Math.min(inputEl.scrollHeight, 120) + 'px';
        });

        document.getElementById('newChatBtn')?.addEventListener('click', () => {
            window.location.href = `/Agent/Chat?sessionId=${crypto.randomUUID().replace(/-/g,'')}`;
        });

        document.getElementById('clearBtn')?.addEventListener('click', async () => {
            if (!confirm('Clear this session?')) return;
            await fetch(`/api/agent/session/${sessionId}`, { method: 'DELETE' });
            FileStore.clear(sessionId);
            messagesEl.innerHTML = '';
            renderFilesPane();
        });
    }

    // ── Pane toggle logic ─────────────────────────────────────────────
    function setupPaneToggles() {
        const convPane   = document.getElementById('convPane');
        const filesPane  = document.getElementById('filesPane');
        const overlay    = document.getElementById('paneOverlay');
        const isMobile   = () => window.innerWidth <= 1100;

        function openConv()  { convPane.classList.add('open');    if (isMobile()) overlay.classList.add('visible'); }
        function closeConv() { convPane.classList.remove('open'); if (!filesPane.classList.contains('open')) overlay.classList.remove('visible'); }
        function openFiles() { filesPane.classList.add('open');   if (isMobile()) overlay.classList.add('visible'); }
        function closeFiles(){ filesPane.classList.remove('open');if (!convPane.classList.contains('open'))  overlay.classList.remove('visible'); }

        document.getElementById('toggleConvBtn')?.addEventListener('click', () => {
            if (isMobile()) { convPane.classList.contains('open') ? closeConv() : openConv(); }
            else            { convPane.classList.toggle('collapsed'); }
        });
        document.getElementById('toggleFilesBtn')?.addEventListener('click', () => {
            if (isMobile()) { filesPane.classList.contains('open') ? closeFiles() : openFiles(); }
            else            { filesPane.classList.toggle('collapsed'); }
        });
        document.getElementById('convCloseBtn')?.addEventListener('click',  closeConv);
        document.getElementById('filesCloseBtn')?.addEventListener('click', closeFiles);
        overlay?.addEventListener('click', () => { closeConv(); closeFiles(); });
    }

    // ── Session list ──────────────────────────────────────────────────
    async function renderSessions() {
        const list = document.getElementById('convList');
        if (!list) return;

        // Sync server sessions into local store (adds any sessions from other browsers)
        try {
            const res = await fetch('/api/agent/sessions');
            if (res.ok) {
                const serverSessions = await res.json();
                serverSessions.forEach(s => {
                    // upsert preserves custom titles set locally; adds missing entries
                    const existing = SessionStore.getAll().find(e => e.id === s.id);
                    if (!existing) {
                        SessionStore.upsert(s.id, {
                            title:     s.firstMessage?.slice(0, 50) || 'New conversation',
                            createdAt: s.createdAt,
                            lastAt:    s.lastAt,
                        });
                    }
                });
            }
        } catch { /* offline or error — fall back to localStorage */ }

        list.innerHTML = '';
        const sessions = SessionStore.getAll();
        if (!sessions.length) {
            list.innerHTML = '<div style="padding:.85rem 1rem;font-size:.78rem;color:#495057;text-align:center">No conversations yet</div>';
            return;
        }
        sessions.forEach(s => {
            const item = document.createElement('div');
            item.className = `conv-item${s.id === sessionId ? ' active' : ''}`;
            item.innerHTML =
                `<span class="conv-item-icon">&#x1F4AC;</span>` +
                `<div class="conv-item-body">` +
                  `<div class="conv-item-title">${escapeHtml(s.title)}</div>` +
                  `<div class="conv-item-time">${relTime(new Date(s.lastAt))}</div>` +
                `</div>` +
                `<button class="conv-item-del" data-id="${s.id}" title="Delete">&#10005;</button>`;

            item.querySelector('.conv-item-del').addEventListener('click', async e => {
                e.stopPropagation();
                SessionStore.remove(s.id);
                await fetch(`/api/agent/session/${s.id}`, { method: 'DELETE' });
                if (s.id === sessionId) window.location.href = `/Agent/Chat?sessionId=${crypto.randomUUID().replace(/-/g,'')}`;
                else renderSessions();
            });

            item.addEventListener('click', () => { if (s.id !== sessionId) window.location.href = `/Agent/Chat?sessionId=${s.id}`; });
            list.appendChild(item);
        });
    }

    // ── Files pane ────────────────────────────────────────────────────
    function renderFilesPane() {
        const files = FileStore.get(sessionId);
        const empty = document.getElementById('filesEmpty');
        const list  = document.getElementById('filesList');
        if (!empty || !list) return;

        if (!files.length) { empty.style.display = 'flex'; list.style.display = 'none'; return; }
        empty.style.display = 'none';
        list.style.display  = 'block';
        list.innerHTML      = '';

        files.forEach(file => {
            const card    = document.createElement('div');
            card.className= 'file-card';

            if (file.isGenerated) {
                // ── Generated file (PDF / Word / Excel) ───────────────
                card.innerHTML =
                    `<div class="file-card-icon">${file.icon}</div>` +
                    `<div class="file-card-info">` +
                      `<div class="file-card-name" title="${escapeHtml(file.name)}">${escapeHtml(file.name)}</div>` +
                      `<div class="file-card-meta">Generated file</div>` +
                    `</div>` +
                    `<div class="file-card-actions">` +
                      `<a class="file-action-btn dl-btn" href="${escapeHtml(file.url)}" title="Download" download>&#8595;</a>` +
                    `</div>`;
            } else {
                // ── Code block ────────────────────────────────────────
                const lines = file.content.split('\n').length;
                const size  = file.content.length > 1024
                    ? (file.content.length / 1024).toFixed(1) + ' KB'
                    : file.content.length + ' B';

                card.innerHTML =
                    `<div class="file-card-icon">${file.icon}</div>` +
                    `<div class="file-card-info">` +
                      `<div class="file-card-name" title="${escapeHtml(file.name)}">${escapeHtml(file.name)}</div>` +
                      `<div class="file-card-meta">${lines} lines &middot; ${size}</div>` +
                    `</div>` +
                    `<div class="file-card-actions">` +
                      `<button class="file-action-btn dl-btn" title="Download">&#8595;</button>` +
                      `<button class="file-action-btn cp-btn" title="Copy">&#x1F4CB;</button>` +
                    `</div>`;

                card.querySelector('.dl-btn').addEventListener('click', () => downloadFile(file));
                card.querySelector('.cp-btn').addEventListener('click', e => copyFile(file, e.currentTarget));
            }
            list.appendChild(card);
        });

        // Count bar
        const bar = document.createElement('div');
        bar.className = 'files-count-bar';
        bar.textContent = `${files.length} file${files.length !== 1 ? 's' : ''} generated`;
        list.appendChild(bar);
    }

    function downloadFile(file) {
        const blob = new Blob([file.content], { type: 'text/plain;charset=utf-8' });
        const url  = URL.createObjectURL(blob);
        const a    = document.createElement('a');
        a.href = url; a.download = file.name; a.click();
        URL.revokeObjectURL(url);
    }

    async function copyFile(file, btn) {
        await navigator.clipboard.writeText(file.content);
        const orig = btn.innerHTML;
        btn.innerHTML = '&#10003;';
        setTimeout(() => { btn.innerHTML = orig; }, 1500);
    }

    // ── SignalR ───────────────────────────────────────────────────────
    async function connectSignalR() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/agentHub')
            .withAutomaticReconnect([0, 1000, 3000, 5000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        connection.on('AgentStatus', status => {
            if (status.startsWith('delegating:')) {
                // Pre-ensure the assistant bubble exists so the card has a home
                if (!currentAssistantBubble) createAssistantBubble();
                setStatus('delegating');
            } else if (status.startsWith('delegating-complete:')) {
                const agentName = status.slice('delegating-complete:'.length);
                const card = subAgentBubbles.get(agentName);
                if (card) {
                    card.querySelector('.sub-agent-spinner')?.remove();
                    card.removeAttribute('open');  // collapse by default; user can re-open
                }
            } else {
                setStatus(status);
            }
        });

        connection.on('SubAgentChunk', ({ agentName, icon, content }) => {
            hideTyping();
            if (!currentAssistantBubble) createAssistantBubble();
            const card        = getOrCreateSubAgentCard(agentName, icon);
            const accumulated = (subAgentContents.get(agentName) || '') + content;
            subAgentContents.set(agentName, accumulated);
            card.querySelector('.sub-agent-content').innerHTML = renderMarkdown(accumulated);
            scrollToBottom();
        });

        connection.on('ReceiveChunk', chunk => {
            hideTyping();
            if (!currentAssistantBubble) createAssistantBubble();
            currentAssistantContent += chunk;
            currentAssistantBubble.innerHTML = renderMarkdown(currentAssistantContent);
            scrollToBottom();
        });

        connection.on('TaskCompleted', ({ awaitingInput }) => {
            finaliseAssistantBubble();
            if (awaitingInput) appendInputNeededBadge();
            setStatus('idle');
            setBusy(false);
            scrollToBottom();
        });

        connection.on('TaskError', message => {
            hideTyping();
            streamingCharts = [];
            finaliseAssistantBubble();
            appendErrorMessage(message);
            setStatus('error');
            setBusy(false);
        });

        // Chart pushed by ChartGeneratorPlugin before the text response arrives
        connection.on('RenderChart', specJson => {
            streamingCharts.push(specJson);
            // Show a loading placeholder in the active bubble (or create one)
            if (!currentAssistantBubble) createAssistantBubble();
            const ph = document.createElement('div');
            ph.className = 'bp-chart-wrapper bp-chart-incoming';
            ph.innerHTML = '<div class="bp-chart-loading">' +
                           '<span class="spinner-border spinner-border-sm me-2" role="status"></span>' +
                           'Rendering chart\u2026</div>';
            currentAssistantBubble.appendChild(ph);
            scrollToBottom();
        });

        connection.onreconnected(async () => { await connection.invoke('JoinSession', sessionId); });

        try {
            await connection.start();
            await connection.invoke('JoinSession', sessionId);
        } catch (err) {
            appendErrorMessage('Could not connect to the agent. Please refresh.');
        }
    }

    // ── History ───────────────────────────────────────────────────────
    async function loadHistory() {
        try {
            const res = await fetch(`/api/agent/history/${sessionId}`);
            if (!res.ok) return;
            const messages = await res.json();
            if (messages.length) {
                document.getElementById('welcomeMsg')?.remove();
                isFirstMessage = false;
                // Restore this session to the sidebar (handles direct URL / bookmark navigation)
                SessionStore.upsert(sessionId);
                renderSessions();
            }
            messages.forEach(m => {
                if (m.role === 'chart') {
                    appendChartMessage(m.content, new Date(m.timestamp));
                } else {
                    appendMessage(m.role, m.content, new Date(m.timestamp), false);
                    if (m.role === 'assistant') {
                        const added = FileStore.extractFromMarkdown(sessionId, m.content);
                        if (added > 0) renderFilesPane();
                    }
                }
            });
            scrollToBottom();
        } catch { /* ignore */ }

        // Restore visual cues if the agent is still active for this session
        await checkAgentStatus();
    }

    async function checkAgentStatus() {
        try {
            const res = await fetch(`/api/agent/status/${sessionId}`);
            if (!res.ok) return;
            const { status, isActive } = await res.json();
            if (isActive) {
                setStatus(status);
                showTyping();
                setBusy(true);
            }
        } catch { /* ignore */ }
    }

    function appendChartMessage(specJson, timestamp) {
        const wrapper = document.createElement('div');
        wrapper.className = 'msg assistant';
        wrapper.innerHTML =
            `<div class="msg-avatar">&#x1F916;</div>` +
            `<div class="msg-body">` +
              `<div class="msg-bubble"></div>` +
              `<div class="msg-time">${formatTime(timestamp)}</div>` +
            `</div>`;
        messagesEl.appendChild(wrapper);
        const bubble = wrapper.querySelector('.msg-bubble');
        appendChartToBubble(bubble, specJson);
        renderPendingCharts(bubble);
    }

    // ── Send ──────────────────────────────────────────────────────────
    async function sendMessage() {
        const text        = inputEl.value.trim();
        const attachments = AttachmentStore.getAll();

        if ((!text && !attachments.length) || isBusy) return;

        if (AttachmentStore.isLoading()) {
            const hint = document.getElementById('attachmentChips');
            hint?.animate([{outline:'2px solid #ffc107'},{outline:'none'}], {duration:600,iterations:2});
            return;
        }

        // Build the full message sent to the agent (original text + file contexts)
        let fullMessage = text;
        if (attachments.length > 0) {
            const fileBlocks = attachments
                .map(a => `--- file: ${a.name} ---\n${a.content}\n--- end of file ---`)
                .join('\n\n');
            fullMessage = `[User has attached ${attachments.length} file(s) as context:]\n\n${fileBlocks}` +
                          (text ? `\n\n[User message:]\n${text}` : '');
        }

        const displayText = text || '(see attached files)';

        inputEl.value = '';
        inputEl.style.height = 'auto';

        if (isFirstMessage) {
            document.getElementById('welcomeMsg')?.remove();
            isFirstMessage = false;
            SessionStore.upsert(sessionId, { title: (text || attachments[0]?.name || 'New chat').slice(0, 50) });
            renderSessions();
        }

        // Render user bubble — with file chips if any
        appendUserMessage(displayText, attachments);

        // Clear attachments
        AttachmentStore.clear();
        renderAttachmentChips();

        showTyping();
        setBusy(true);
        setStatus('thinking');
        scrollToBottom();

        try {
            const res = await fetch('/api/agent/message', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ sessionId, message: fullMessage })
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({ error: 'Request failed' }));
                throw new Error(err.error ?? 'Unknown error');
            }
            SessionStore.upsert(sessionId);
        } catch (err) {
            hideTyping();
            appendErrorMessage(err.message);
            setStatus('error');
            setBusy(false);
        }
    }

    // ── Message rendering ─────────────────────────────────────────────
    function appendUserMessage(text, attachments = []) {
        const wrapper = document.createElement('div');
        wrapper.className = 'msg user';

        let chipsHtml = '';
        if (attachments.length > 0) {
            const chips = attachments.map(a =>
                `<span class="msg-file-chip">${escapeHtml(AttachmentStore.extIcon(a.name))} ${escapeHtml(a.name)}</span>`
            ).join('');
            chipsHtml = `<div class="msg-attachments">${chips}</div>`;
        }

        wrapper.innerHTML =
            `<div class="msg-avatar" aria-hidden="true">` +
            `<svg viewBox="0 0 24 24" fill="currentColor" width="16" height="16">` +
            `<path d="M12 12a5 5 0 1 0 0-10 5 5 0 0 0 0 10zm0 2c-5.33 0-8 2.67-8 4v1h16v-1c0-1.33-2.67-4-8-4z"/>` +
            `</svg></div>` +
            `<div class="msg-body">` +
              `<div class="msg-bubble">${chipsHtml}${escapeHtml(text)}</div>` +
              `<div class="msg-time">${formatTime(new Date())}</div>` +
            `</div>`;
        messagesEl.appendChild(wrapper);
        return wrapper.querySelector('.msg-bubble');
    }

    function appendMessage(role, content, timestamp, _animate = true) {
        const isUser  = role === 'user';
        const wrapper = document.createElement('div');
        wrapper.className = `msg ${isUser ? 'user' : 'assistant'}`;

        const avatarHtml = isUser
            ? `<div class="msg-avatar" aria-hidden="true">` +
              `<svg viewBox="0 0 24 24" fill="currentColor" width="16" height="16">` +
              `<path d="M12 12a5 5 0 1 0 0-10 5 5 0 0 0 0 10zm0 2c-5.33 0-8 2.67-8 4v1h16v-1c0-1.33-2.67-4-8-4z"/>` +
              `</svg></div>`
            : `<div class="msg-avatar ai-avatar" aria-hidden="true">` +
              `<svg viewBox="0 0 24 24" fill="none">` +
              `<path d="M12 2l1.796 5.527L19 9l-5.204 1.473L12 16l-1.796-5.527L5 9l5.204-1.473L12 2z" fill="currentColor" opacity=".9"/>` +
              `<path d="M19 16l.898 2.764L22 19.5l-2.102.736L19 23l-.898-2.764L16 19.5l2.102-.736L19 16z" fill="currentColor" opacity=".6"/>` +
              `</svg></div>`;

        wrapper.innerHTML =
            avatarHtml +
            `<div class="msg-body">` +
              `<div class="msg-bubble">${isUser ? escapeHtml(content) : renderMarkdown(content)}</div>` +
              `<div class="msg-time">${formatTime(timestamp)}</div>` +
            `</div>`;
        messagesEl.appendChild(wrapper);
        const bubble = wrapper.querySelector('.msg-bubble');
        if (!isUser) renderPendingCharts(bubble);
        return bubble;
    }

    function createAssistantBubble() {
        currentAssistantContent    = '';
        subAgentBubbles            = new Map();
        subAgentContents           = new Map();
        currentSubAgentsContainer  = null;
        const wrapper = document.createElement('div');
        wrapper.className = 'msg assistant';
        wrapper.dataset.streaming = '1';
        wrapper.innerHTML =
            `<div class="msg-avatar ai-avatar" aria-hidden="true">` +
            `<svg viewBox="0 0 24 24" fill="none">` +
            `<path d="M12 2l1.796 5.527L19 9l-5.204 1.473L12 16l-1.796-5.527L5 9l5.204-1.473L12 2z" fill="currentColor" opacity=".9"/>` +
            `<path d="M19 16l.898 2.764L22 19.5l-2.102.736L19 23l-.898-2.764L16 19.5l2.102-.736L19 16z" fill="currentColor" opacity=".6"/>` +
            `</svg></div>` +
            `<div class="msg-body">` +
              `<div class="sub-agents-container"></div>` +
              `<div class="msg-bubble"></div>` +
              `<div class="msg-time">${formatTime(new Date())}</div>` +
            `</div>`;
        messagesEl.appendChild(wrapper);
        currentAssistantBubble    = wrapper.querySelector('.msg-bubble');
        currentSubAgentsContainer = wrapper.querySelector('.sub-agents-container');
    }

    function getOrCreateSubAgentCard(agentName, icon) {
        if (subAgentBubbles.has(agentName)) return subAgentBubbles.get(agentName);

        const card = document.createElement('details');
        card.className = 'sub-agent-card';
        card.setAttribute('open', '');
        card.innerHTML =
            `<summary class="sub-agent-summary">` +
              `<span class="sub-agent-icon">${escapeHtml(icon)}</span>` +
              `<span class="sub-agent-name">${escapeHtml(agentName)}</span>` +
              `<span class="sub-agent-spinner">` +
                `<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span>` +
              `</span>` +
            `</summary>` +
            `<div class="sub-agent-content"></div>`;
        currentSubAgentsContainer?.appendChild(card);
        subAgentBubbles.set(agentName, card);
        return card;
    }

    function finaliseAssistantBubble() {
        if (currentAssistantBubble) {
            // Remove loading placeholders injected during streaming
            currentAssistantBubble
                .querySelectorAll('.bp-chart-incoming')
                .forEach(el => el.remove());

            currentAssistantBubble.innerHTML = renderMarkdown(currentAssistantContent);

            // Flush charts pushed via SignalR during this response
            streamingCharts.forEach(specJson => appendChartToBubble(currentAssistantBubble, specJson));
            streamingCharts = [];

            // Also render any ```chart``` blocks that ended up in the markdown text
            renderPendingCharts(currentAssistantBubble);

            const wrapper = currentAssistantBubble.closest('[data-streaming]');
            if (wrapper) delete wrapper.dataset.streaming;

            // Extract code blocks into Files pane (skip chart blocks)
            const added = FileStore.extractFromMarkdown(sessionId, currentAssistantContent);
            if (added > 0) renderFilesPane();
        }
        // Finalize any sub-agent cards that didn't receive a delegating-complete event
        subAgentBubbles.forEach(card => {
            card.querySelector('.sub-agent-spinner')?.remove();
            card.removeAttribute('open');
        });
        currentAssistantBubble    = null;
        currentAssistantContent   = '';
        subAgentBubbles           = new Map();
        subAgentContents          = new Map();
        currentSubAgentsContainer = null;
    }

    function appendChartToBubble(bubble, specJson) {
        const encoded = btoa(unescape(encodeURIComponent(specJson)));
        const wrapper = document.createElement('div');
        wrapper.className = 'bp-chart-wrapper';
        wrapper.dataset.chartSpec = encoded;
        wrapper.innerHTML =
            '<div class="bp-chart-loading">📊 Rendering\u2026</div>' +
            '<canvas class="bp-chart-canvas" style="display:none"></canvas>';
        bubble.appendChild(wrapper);
    }

    function appendInputNeededBadge() {
        const bubble = messagesEl.lastElementChild?.querySelector('.msg-bubble');
        if (!bubble) return;
        const badge = document.createElement('div');
        badge.className = 'input-needed-badge mt-2';
        badge.textContent = 'Awaiting your input...';
        bubble.appendChild(badge);
    }

    function appendErrorMessage(message) {
        const el = document.createElement('div');
        el.className = 'alert alert-danger alert-sm my-2 mx-2 py-2 px-3';
        el.style.fontSize = '.84rem';
        el.textContent = `Error: ${message}`;
        messagesEl.appendChild(el);
        scrollToBottom();
    }

    // ── Helpers ───────────────────────────────────────────────────────
    function showTyping() {
        if (!typingEl) return;
        messagesEl.appendChild(typingEl);   // always at the bottom, after latest message
        typingEl.style.display = 'flex';
        scrollToBottom();
    }
    function hideTyping() { if (typingEl) typingEl.style.display = 'none'; }

    function renderMarkdown(text) {
        if (typeof marked === 'undefined') return escapeHtml(text);
        return marked.parse(text, { breaks: true, gfm: true });
    }
    function escapeHtml(t) {
        return t.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
                .replace(/"/g,'&quot;').replace(/'/g,'&#039;');
    }
    function formatTime(d) { return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
    function scrollToBottom() { messagesEl.scrollTop = messagesEl.scrollHeight; }

    function setBusy(busy) {
        isBusy = sendBtn.disabled = inputEl.disabled = busy;
        sendBtn.innerHTML = busy
            ? '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px" role="status"></span>'
            : '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/></svg>';
    }

    function setStatus(s) {
        if (!statusBadge) return;
        statusBadge.className = `status-badge ${s}`;
        statusBadge.textContent = {
            thinking:   'Thinking…',
            responding: 'Responding…',
            delegating: 'Delegating…',
            idle:       '',
            error:      'Error',
        }[s] ?? s;
    }

    function relTime(d) {
        const diff = Date.now() - d;
        if (diff < 60000)    return 'just now';
        if (diff < 3600000)  return `${Math.floor(diff/60000)}m ago`;
        if (diff < 86400000) return `${Math.floor(diff/3600000)}h ago`;
        return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
    }

    return { init };
})();

document.addEventListener('DOMContentLoaded', () => {
    const data = document.getElementById('sessionData')?.dataset;
    const sid  = data?.sessionId;
    if (sid) Chat.init(sid);
});
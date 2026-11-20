(function () {
    'use strict';

    const VERSION = '1.6.1';
    const base = location.pathname.replace(/\/MediaDownloader\/?$/i, '');
    let token = '';
    let me = null;
    let serverId = '';

    // ---------- helpers ----------

    function $(id) {
        return document.getElementById(id);
    }

    function el(tag, text, style) {
        const e = document.createElement(tag);
        if (text !== undefined && text !== null) e.textContent = text;
        if (style) e.setAttribute('style', style);
        return e;
    }

    function store(key, value) {
        try {
            if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, value);
        } catch (e) {
            // storage blocked
        }
    }

    function load(key) {
        try {
            return localStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    function deviceId() {
        let id = load('mediaDownloaderDevice');
        if (!id) {
            id = 'md-' + Math.random().toString(36).slice(2) + Date.now().toString(36);
            store('mediaDownloaderDevice', id);
        }
        return id;
    }

    function authHeader(withToken) {
        const parts = ['Client="Media Downloader"', 'Device="Web browser"', 'DeviceId="' + deviceId() + '"', 'Version="' + VERSION + '"'];
        if (withToken) parts.push('Token="' + withToken + '"');
        return 'MediaBrowser ' + parts.join(', ');
    }

    class HttpError extends Error {
        constructor(status, message) {
            super(message);
            this.status = status;
        }
    }

    async function request(method, url, body, useToken) {
        const headers = { Authorization: authHeader(useToken === undefined ? token : useToken), Accept: 'application/json' };
        if (body !== undefined) headers['Content-Type'] = 'application/json';
        const res = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store' });
        if (!res.ok) {
            let message = 'Request failed (' + res.status + ')';
            try {
                const data = await res.json();
                if (data && data.error) message = data.error;
            } catch (e) {
                // not JSON
            }
            throw new HttpError(res.status, message);
        }
        if (res.status === 204) return null;
        const text = await res.text();
        return text ? JSON.parse(text) : null;
    }

    function api(path) {
        return base + '/MediaDownloader/' + path;
    }

    function fmtBytes(n) {
        if (!n) return '0 B';
        const u = ['B', 'KB', 'MB', 'GB', 'TB'];
        let i = 0;
        let v = n;
        while (v >= 1024 && i < u.length - 1) {
            v /= 1024;
            i++;
        }
        return v.toFixed(i >= 3 ? 2 : i ? 1 : 0) + ' ' + u[i];
    }

    function fmtDate(iso) {
        if (!iso) return '';
        const d = new Date(iso);
        return isNaN(d) ? '' : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    }

    function fmtDuration(sec) {
        if (!sec) return '';
        const s = Math.round(sec);
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        const r = s % 60;
        return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(r).padStart(2, '0');
    }

    function itemUrl(itemId) {
        return base + '/web/#/details?id=' + encodeURIComponent(itemId) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : '');
    }

    // ---------- signing in ----------

    function webClientTokens() {
        // Tokens the Jellyfin web app on this same address already holds, so a signed-in user does not sign in again.
        const list = [];
        try {
            const creds = JSON.parse(load('jellyfin_credentials') || 'null');
            for (const s of (creds && creds.Servers) || []) {
                if (!s.AccessToken) continue;
                const addresses = [s.ManualAddress, s.LocalAddress, s.RemoteAddress].filter(Boolean);
                const same = addresses.some((a) => {
                    try {
                        return new URL(a).origin === location.origin;
                    } catch (e) {
                        return false;
                    }
                });
                if (same) list.unshift(s.AccessToken); else list.push(s.AccessToken);
            }
        } catch (e) {
            // nothing usable
        }
        return list;
    }

    async function tryToken(candidate) {
        try {
            const info = await request('GET', api('Me'), undefined, candidate);
            token = candidate;
            return info;
        } catch (e) {
            if (e.status === 401) return null;
            throw e;
        }
    }

    async function signIn() {
        const own = load('mediaDownloaderToken');
        if (own) {
            const info = await tryToken(own);
            if (info) return info;
            store('mediaDownloaderToken', null);
        }
        let skipWeb = false;
        try {
            skipWeb = sessionStorage.getItem('mediaDownloaderSignedOut') === '1';
        } catch (e) {
            // storage blocked
        }
        if (!skipWeb) {
            for (const t of webClientTokens()) {
                const info = await tryToken(t);
                if (info) return info;
            }
        }
        return null;
    }

    function show(id) {
        for (const x of ['loading', 'login', 'denied', 'app']) $(x).classList.toggle('hidden', x !== id);
    }

    function showLogin() {
        show('login');
        $('who').textContent = '';
        $('signOut').classList.add('hidden');
        $('loginUser').focus();
    }

    $('login').addEventListener('submit', async (e) => {
        e.preventDefault();
        $('loginError').textContent = '';
        try {
            const r = await request('POST', base + '/Users/AuthenticateByName', { Username: $('loginUser').value.trim(), Pw: $('loginPass').value }, '');
            token = r.AccessToken;
            store('mediaDownloaderToken', token);
            try {
                sessionStorage.removeItem('mediaDownloaderSignedOut');
            } catch (err) {
                // storage blocked
            }
            $('loginPass').value = '';
            await start(await request('GET', api('Me')));
        } catch (err) {
            $('loginError').textContent = err.status === 401 ? 'Wrong user name or password.' : err.message;
        }
    });

    $('signOut').addEventListener('click', async () => {
        if (load('mediaDownloaderToken') === token) {
            try {
                await request('POST', base + '/Sessions/Logout');
            } catch (e) {
                // already signed out
            }
            store('mediaDownloaderToken', null);
        }
        try {
            sessionStorage.setItem('mediaDownloaderSignedOut', '1');
        } catch (e) {
            // storage blocked
        }
        token = '';
        me = null;
        stopPolling();
        showLogin();
    });

    // ---------- tabs ----------

    let filesLoaded = false;

    function showTab(name) {
        for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t.dataset.tab === name);
        for (const p of document.querySelectorAll('.panel')) p.classList.toggle('active', p.dataset.panel === name);
        store('mediaDownloaderUserTab', name);
        if (name === 'files' && !filesLoaded) loadFiles();
        if (name === 'folders' || foldersStale) loadFolders();
    }

    for (const t of document.querySelectorAll('.tab')) t.addEventListener('click', () => showTab(t.dataset.tab));

    // ---------- link check ----------

    const supportedSitesUrl = 'https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md';
    const linkExamples = {
        youtube: ['https://www.youtube.com/watch?v=VIDEO_ID', 'https://youtu.be/VIDEO_ID', 'https://www.youtube.com/playlist?list=PLAYLIST_ID', 'https://www.youtube.com/@CHANNEL (whole channel)', 'https://www.youtube.com/shorts/VIDEO_ID'],
        youtu: ['https://youtu.be/VIDEO_ID'],
        facebook: ['https://www.facebook.com/watch/?v=VIDEO_ID', 'https://www.facebook.com/PAGE/videos/VIDEO_ID/', 'https://www.facebook.com/reel/REEL_ID', 'https://fb.watch/CODE/'],
        fb: ['https://fb.watch/CODE/'],
        instagram: ['https://www.instagram.com/p/POST_ID/', 'https://www.instagram.com/reel/REEL_ID/'],
        tiktok: ['https://www.tiktok.com/@USER/video/VIDEO_ID', 'https://vm.tiktok.com/CODE/', 'https://www.tiktok.com/@USER (all videos)'],
        x: ['https://x.com/USER/status/POST_ID'],
        twitter: ['https://twitter.com/USER/status/POST_ID'],
        vimeo: ['https://vimeo.com/VIDEO_ID', 'https://vimeo.com/channels/NAME/VIDEO_ID', 'https://vimeo.com/showcase/ID'],
        dailymotion: ['https://www.dailymotion.com/video/VIDEO_ID', 'https://www.dailymotion.com/playlist/ID'],
        bbc: ['https://www.bbc.co.uk/iplayer/episode/EPISODE_ID/NAME (UK only)', 'https://www.bbc.co.uk/programmes/PROGRAMME_ID'],
        soundcloud: ['https://soundcloud.com/ARTIST/TRACK', 'https://soundcloud.com/ARTIST/sets/PLAYLIST'],
        twitch: ['https://www.twitch.tv/videos/VIDEO_ID', 'https://clips.twitch.tv/CLIP'],
        reddit: ['https://www.reddit.com/r/SUB/comments/POST_ID/TITLE/'],
        bilibili: ['https://www.bilibili.com/video/BV_ID', 'https://www.bilibili.tv/en/video/ID'],
        rumble: ['https://rumble.com/VIDEO-NAME.html'],
        archive: ['https://archive.org/details/ITEM_ID'],
        vk: ['https://vk.com/video-OWNER_ID_VIDEO_ID'],
        ok: ['https://ok.ru/video/VIDEO_ID'],
        t: ['https://t.me/CHANNEL/POST_ID'],
        pinterest: ['https://www.pinterest.com/pin/PIN_ID/'],
        streamable: ['https://streamable.com/CODE'],
        mixcloud: ['https://www.mixcloud.com/USER/SHOW/']
    };

    function siteName(url) {
        try {
            const labels = new URL(url).hostname.toLowerCase().split('.').filter(Boolean);
            if (labels.length >= 2) labels.pop();
            if (labels.length >= 2 && ['co', 'com', 'org', 'net', 'ac', 'gov', 'edu'].includes(labels[labels.length - 1])) labels.pop();
            return labels.length ? labels[labels.length - 1] : '';
        } catch (e) {
            return '';
        }
    }

    function showCheck(kind, parts) {
        const box = $('check');
        box.replaceChildren();
        if (!kind) {
            box.style.display = 'none';
            return;
        }
        const colors = { ok: 'rgba(76,175,80,.18)', warn: 'rgba(255,193,7,.18)', bad: 'rgba(229,115,115,.18)', busy: 'rgba(255,255,255,.06)' };
        box.style.display = 'block';
        box.style.background = colors[kind];
        for (const p of parts) box.appendChild(p);
    }

    let checkSeq = 0;
    let checkTimer = null;

    async function checkLink() {
        const url = $('url').value.trim();
        const seq = ++checkSeq;
        if (!/^https?:\/\/\S+$/i.test(url)) {
            showCheck(url ? 'bad' : null, [el('div', 'Paste a full link starting with https://')]);
            return;
        }
        showCheck('busy', [el('div', 'Checking the link...')]);
        try {
            const r = await request('POST', api('Check'), { Url: url });
            if (seq !== checkSeq) return;
            const parts = [];
            if (r.Result === 'supported' || r.Result === 'generic') {
                parts.push(el('div', '✔ ' + (r.Result === 'supported' ? 'Supported (' + r.Site + ')' : 'Media found on the page'), 'font-weight:bold'));
                if (r.Kind === 'playlist') {
                    parts.push(el('div', 'Playlist or channel: ' + (r.Title || '') + (r.Items ? ' (' + r.Items + (r.Items >= 200 ? '+' : '') + ' items)' : '')));
                    parts.push(el('div', 'Tick "Whole playlist or channel" to download all of it; otherwise only one video is taken.', 'opacity:.8'));
                } else {
                    let line = r.Title || '(no title)';
                    if (r.Uploader) line += ' – ' + r.Uploader;
                    if (r.Duration) line += ' (' + fmtDuration(r.Duration) + ')';
                    parts.push(el('div', line));
                }
                showCheck(r.Result === 'supported' ? 'ok' : 'warn', parts);
                return;
            }
            parts.push(el('div', r.Result === 'unsupported' ? '✘ This link is not supported' : '✘ This link could not be read', 'font-weight:bold'));
            if (r.Message) parts.push(el('div', r.Message, 'opacity:.85'));
            const examples = linkExamples[siteName(url)];
            if (examples) {
                parts.push(el('div', 'Links of this site that work look like:', 'margin-top:.5em;font-weight:bold'));
                for (const ex of examples) parts.push(el('div', ex, 'font-family:monospace;font-size:.9em;padding-left:1em'));
            }
            if (r.Handlers && r.Handlers.length) {
                parts.push(el('div', 'Paste the page where the video itself plays, not the home page. Handlers for this site: ' + r.Handlers.join(', '), 'margin-top:.4em;font-size:.9em'));
            }
            const a = el('a', 'List of supported sites', 'display:inline-block;margin-top:.5em;font-weight:bold');
            a.href = supportedSitesUrl;
            a.target = '_blank';
            a.rel = 'noopener';
            parts.push(a);
            showCheck('bad', parts);
        } catch (err) {
            if (seq === checkSeq) showCheck('bad', [el('div', 'Could not check: ' + err.message)]);
        }
    }

    $('url').addEventListener('input', () => {
        clearTimeout(checkTimer);
        checkTimer = setTimeout(checkLink, 700);
    });

    $('downloadForm').addEventListener('submit', async (e) => {
        e.preventDefault();
        $('formError').textContent = '';
        const body = {
            Url: $('url').value.trim(),
            Kind: $('kind').value,
            TargetFolder: $('folder').value,
            SubFolder: $('subFolder').value.trim(),
            MaxHeight: parseInt($('height').value, 10) || 0,
            WholePlaylist: $('playlist').checked,
            SkipDownloaded: $('skip').checked
        };
        try {
            await request('POST', api('Jobs'), body);
            $('url').value = '';
            checkSeq++;
            showCheck(null, []);
            await refreshJobs();
            showTab('jobs');
        } catch (err) {
            $('formError').textContent = err.message;
        }
    });

    // ---------- downloads ----------

    const stateText = { Queued: 'Waiting', Running: 'Downloading', Done: 'Finished', Failed: 'Failed', Cancelled: 'Cancelled' };
    const itemIds = {};
    const lastLookup = {};
    let lastActive = 0;

    function lookupItems(job) {
        const now = Date.now();
        if (lastLookup[job.Id] && now - lastLookup[job.Id] < 5000) return;
        lastLookup[job.Id] = now;
        request('GET', api('Jobs/' + encodeURIComponent(job.Id) + '/Items')).then((items) => {
            let found = false;
            for (const i of items) {
                if (i.itemId && !itemIds[i.file]) {
                    itemIds[i.file] = i.itemId;
                    found = true;
                }
            }
            if (found) refreshJobs();
        }).catch(() => { /* next poll */ });
    }

    function folderLabel(path) {
        let best = null;
        for (const f of (me && me.Folders) || []) {
            const p = f.Path.replace(/[\\/]+$/, '');
            if ((path === p || path.startsWith(p + '\\') || path.startsWith(p + '/')) && (!best || p.length > best.p.length)) best = { p, f };
        }
        if (!best) return path;
        const rest = path.slice(best.p.length).replace(/^[\\/]+/, '').replace(/\\/g, '/');
        return best.f.Label + (rest ? ' / ' + rest : '');
    }

    function renderJobs(jobs) {
        const box = $('jobs');
        box.replaceChildren();
        if (!jobs.length) {
            box.appendChild(el('div', 'No downloads yet.', 'opacity:.7'));
            return;
        }
        for (const job of jobs) {
            const card = el('div');
            card.className = 'card';
            card.appendChild(el('div', job.Title || job.Request.Url, 'font-weight:bold;word-break:break-all'));
            let line = stateText[job.State] || job.State;
            if (job.State === 'Running') {
                line += ' ' + (job.Percent || 0).toFixed(1) + '%';
                if (job.Speed) line += ' at ' + job.Speed;
                if (job.Eta) line += ', ' + job.Eta + ' left';
            }
            line += ' | ' + job.Request.Kind + ' | ' + folderLabel(job.Request.TargetFolder);
            card.appendChild(el('div', line, 'opacity:.85;margin-top:.3em'));
            if (job.State === 'Running' || job.State === 'Queued') {
                const bar = el('div');
                bar.className = 'bar';
                bar.appendChild(el('div', null, 'width:' + Math.min(100, job.Percent || 0) + '%'));
                card.appendChild(bar);
                const cancel = el('button', 'Cancel', 'margin-top:.5em');
                cancel.type = 'button';
                cancel.addEventListener('click', () => request('POST', api('Jobs/' + encodeURIComponent(job.Id) + '/Cancel')).then(refreshJobs));
                card.appendChild(cancel);
            }
            let waiting = false;
            for (const f of job.Files || []) {
                card.appendChild(el('div', 'Saved: ' + folderLabel(f), 'font-size:.9em;opacity:.8;word-break:break-all'));
                if (job.State === 'Done') {
                    if (itemIds[f]) {
                        const a = el('a', 'Open in Jellyfin to play');
                        a.className = 'play';
                        a.href = itemUrl(itemIds[f]);
                        card.appendChild(a);
                    } else {
                        card.appendChild(el('div', 'Waiting for Jellyfin to add it to the library...', 'font-size:.9em;opacity:.7'));
                        waiting = true;
                    }
                }
            }
            if (waiting) lookupItems(job);
            if (job.Error) card.appendChild(el('div', job.Error, 'color:var(--' + (job.State === 'Done' ? 'warn' : 'bad') + ');margin-top:.3em;word-break:break-word'));
            box.appendChild(card);
        }
    }

    async function refreshJobs() {
        try {
            const jobs = await request('GET', api('Jobs'));
            renderJobs(jobs);
            const active = jobs.filter((j) => j.State === 'Running' || j.State === 'Queued').length;
            $('jobCount').textContent = active ? '(' + active + ')' : '';
            if (active < lastActive) {
                filesLoaded = false;
                foldersStale = true;
            }
            lastActive = active;
        } catch (err) {
            if (err.status === 401) {
                stopPolling();
                showLogin();
            }
        }
    }

    $('clearFinished').addEventListener('click', () => request('POST', api('Jobs/ClearFinished')).then(refreshJobs));

    let timer = null;

    function startPolling() {
        stopPolling();
        timer = setInterval(refreshJobs, 2000);
    }

    function stopPolling() {
        if (timer) clearInterval(timer);
        timer = null;
    }

    // ---------- my files ----------

    let storage = null;
    const selected = new Set();

    function watchState(f) {
        if (!f.ItemId) return 'unknown';
        if (!f.Watched || !f.Watched.length) return 'unwatched';
        return f.Watched.some((w) => w.Played) ? 'watched' : 'started';
    }

    function watchText(f) {
        if (!f.Media) return 'Not a media file';
        if (!f.ItemId) return 'Not in the library yet';
        const w = f.Watched && f.Watched[0];
        if (!w) return 'Not watched';
        return (w.Played ? 'Watched' : w.Percent + '% watched') + (w.LastPlayed ? ' (' + fmtDate(w.LastPlayed) + ')' : '');
    }

    async function loadFiles() {
        $('filesInfo').textContent = 'Reading your folders...';
        try {
            storage = await request('GET', api('Storage'));
            filesLoaded = true;
            for (const p of Array.from(selected)) {
                if (!storage.Files.some((f) => f.Path === p)) selected.delete(p);
            }
            $('filesInfo').textContent = 'Read at ' + new Date().toLocaleTimeString();
            renderDrives();
            renderFolders();
            renderFiles();
        } catch (err) {
            $('filesInfo').textContent = 'Could not read your folders: ' + err.message;
        }
    }

    function renderDrives() {
        const box = $('drives');
        box.replaceChildren();
        for (const d of storage.Drives) {
            const used = d.Total - d.Free;
            const pct = d.Total ? Math.round(used * 100 / d.Total) : 0;
            const card = el('div');
            card.className = 'card';
            card.appendChild(el('div', 'Space left on the server\'s drive ' + d.Name + ': ' + fmtBytes(d.Free) + ' of ' + fmtBytes(d.Total), 'font-weight:bold'));
            const bar = el('div');
            bar.className = 'bar';
            bar.appendChild(el('div', null, 'width:' + pct + '%;' + (pct > 90 ? 'background:#e53935' : '')));
            card.appendChild(bar);
            box.appendChild(card);
        }
    }

    function renderFolders() {
        const t = $('folders');
        t.replaceChildren();
        const head = el('tr');
        for (const h of ['Your folder', 'Media files', 'Size']) head.appendChild(el('th', h));
        t.appendChild(head);
        for (const f of storage.Folders) {
            const tr = el('tr');
            tr.appendChild(el('td', folderLabel(f.Folder)));
            const n = el('td', String(f.MediaFiles));
            n.className = 'num';
            tr.appendChild(n);
            const s = el('td', fmtBytes(f.Size));
            s.className = 'num';
            tr.appendChild(s);
            t.appendChild(tr);
        }
    }

    function shownFiles() {
        if (!storage) return [];
        const watch = $('filterWatch').value;
        const text = $('filterText').value.trim().toLowerCase();
        return storage.Files.filter((f) => (watch === 'all' || watchState(f) === watch)
            && (!text || f.Relative.toLowerCase().includes(text) || (f.Title || '').toLowerCase().includes(text)));
    }

    function updateDeleteLabel() {
        let bytes = 0;
        for (const f of storage ? storage.Files : []) {
            if (selected.has(f.Path)) bytes += f.Size;
        }
        $('deleteSelected').textContent = selected.size ? 'Delete selected (' + selected.size + ', ' + fmtBytes(bytes) + ')' : 'Delete selected';
    }

    function renderFiles() {
        const t = $('files');
        t.replaceChildren();
        const canDelete = true; // the selection is for moving too; deleting is checked separately
        const files = shownFiles();
        const head = el('tr');
        for (const h of (canDelete ? [''] : []).concat(['File', 'Folder', 'Size', 'Added', 'Watched'])) head.appendChild(el('th', h));
        t.appendChild(head);
        if (!files.length) {
            const tr = el('tr');
            const td = el('td', 'No files here yet.', 'opacity:.7');
            td.colSpan = 6;
            tr.appendChild(td);
            t.appendChild(tr);
        }
        for (const f of files) {
            const tr = el('tr');
            if (canDelete) {
                const td = el('td');
                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.checked = selected.has(f.Path);
                cb.addEventListener('change', () => {
                    if (cb.checked) selected.add(f.Path); else selected.delete(f.Path);
                    updateDeleteLabel();
                });
                td.appendChild(cb);
                tr.appendChild(td);
            }
            const nameTd = el('td', null, 'word-break:break-word');
            if (f.ItemId) {
                const a = el('a', f.Title || f.Relative);
                a.href = itemUrl(f.ItemId);
                nameTd.appendChild(a);
                if (f.Title && f.Title !== f.Relative) nameTd.appendChild(el('div', f.Relative, 'font-size:.85em;opacity:.65'));
            } else {
                nameTd.appendChild(el('div', f.Relative));
            }
            tr.appendChild(nameTd);
            tr.appendChild(el('td', f.Library));
            const size = el('td', fmtBytes(f.Size));
            size.className = 'num';
            tr.appendChild(size);
            tr.appendChild(el('td', fmtDate(f.Added), 'white-space:nowrap'));
            const state = watchState(f);
            tr.appendChild(el('td', watchText(f), 'color:' + { watched: '#81c784', started: '#ffd54f', unwatched: 'inherit', unknown: '#bdbdbd' }[state]));
            t.appendChild(tr);
        }
        updateDeleteLabel();
    }

    $('refreshFiles').addEventListener('click', loadFiles);
    $('filterWatch').addEventListener('change', renderFiles);
    $('filterText').addEventListener('input', renderFiles);
    $('selectShown').addEventListener('click', () => {
        for (const f of shownFiles()) selected.add(f.Path);
        renderFiles();
    });
    $('selectNone').addEventListener('click', () => {
        selected.clear();
        renderFiles();
    });
    $('deleteSelected').addEventListener('click', async () => {
        const files = storage ? storage.Files.filter((f) => selected.has(f.Path)) : [];
        if (!files.length) return;
        const bytes = files.reduce((a, f) => a + f.Size, 0);
        const list = files.slice(0, 15).map((f) => '  ' + f.Relative).join('\n') + (files.length > 15 ? '\n  ... and ' + (files.length - 15) + ' more' : '');
        if (!window.confirm('Delete ' + files.length + ' file(s), ' + fmtBytes(bytes) + ', for good?\n\n' + list + '\n\nTheir posters, descriptions and subtitles go too. This cannot be undone.')) return;
        const result = $('deleteResult');
        result.textContent = 'Deleting...';
        try {
            const rs = await request('POST', api('Storage/Delete'), { Paths: files.map((f) => f.Path) });
            const ok = rs.filter((r) => r.Deleted);
            result.replaceChildren(el('div', 'Deleted ' + ok.length + ' file(s), ' + fmtBytes(ok.reduce((a, r) => a + r.Freed, 0)) + ' freed.', 'color:var(--ok)'));
            for (const r of rs.filter((x) => !x.Deleted)) result.appendChild(el('div', 'Not deleted: ' + r.Path + ' – ' + r.Error, 'color:var(--bad);word-break:break-all'));
            for (const r of ok) selected.delete(r.Path);
            await loadFiles();
            foldersStale = true;
        } catch (err) {
            result.textContent = 'Delete failed: ' + err.message;
        }
    });

    $('moveSelected').addEventListener('click', async () => {
        const files = storage ? storage.Files.filter((f) => selected.has(f.Path)) : [];
        const target = $('moveTarget').value;
        const result = $('deleteResult');
        if (!files.length) {
            result.replaceChildren(el('div', 'Tick the files to move first.', 'color:var(--warn)'));
            return;
        }
        if (!target) return;
        result.textContent = 'Moving...';
        try {
            const rs = await request('POST', api('Storage/Move'), { Paths: files.map((f) => f.Path), Target: target });
            const ok = rs.filter((r) => r.Ok);
            result.replaceChildren(el('div', 'Moved ' + ok.length + ' file(s) to ' + folderLabel(target) + '.', 'color:var(--ok)'));
            for (const r of rs.filter((x) => !x.Ok)) result.appendChild(el('div', 'Not moved: ' + r.Path + ' – ' + r.Error, 'color:var(--bad);word-break:break-all'));
            for (const r of ok) selected.delete(r.Path);
            foldersStale = true;
            await loadFiles();
        } catch (err) {
            result.textContent = 'Move failed: ' + err.message;
        }
    });

    // ---------- my folders ----------

    let folderList = [];
    let selFolder = null;
    let foldersStale = true;

    function folderName(f) {
        return f.Relative ? f.RootLabel + ' / ' + f.Relative : f.RootLabel;
    }

    function fillSelect(select, keep, filter) {
        const previous = keep || select.value;
        select.replaceChildren();
        for (const f of folderList.filter(filter || (() => true))) {
            const o = el('option', '\u00a0\u00a0'.repeat(f.Depth) + folderName(f));
            o.value = f.Path;
            select.appendChild(o);
        }
        if (previous && Array.from(select.options).some((o) => o.value === previous)) select.value = previous;
    }

    function fillFolderSelects() {
        fillSelect($('folder'), load('mediaDownloaderFolder'));
        fillSelect($('moveTarget'));
        const sel = selFolder;
        fillSelect($('folderMoveTarget'), null, (f) => !sel || !(f.Path === sel.Path || f.Path.startsWith(sel.Path + '\\') || f.Path.startsWith(sel.Path + '/')));
    }

    function folderMessage(kind, text) {
        $('folderMsg').replaceChildren(text ? el('div', text, 'color:var(--' + kind + ');word-break:break-word') : el('span'));
    }

    async function loadFolders(keepPath) {
        try {
            folderList = await request('GET', api('Folders'));
            foldersStale = false;
        } catch (err) {
            if (!folderList.length) {
                // fall back to the user's own folders
                folderList = ((me && me.Folders) || []).map((f) => ({ Path: f.Path, Relative: '', Root: f.Path, RootLabel: f.Label, Depth: 0, Files: 0, Size: 0 }));
            }
            folderMessage('bad', 'Could not read your folders: ' + err.message);
        }
        const want = keepPath || (selFolder && selFolder.Path);
        selFolder = folderList.find((f) => f.Path === want) || null;
        renderTree();
        fillFolderSelects();
    }

    function renderTree() {
        const box = $('folderTree');
        box.replaceChildren();
        for (const f of folderList) {
            const row = el('div');
            row.className = 'node' + (selFolder && selFolder.Path === f.Path ? ' sel' : '');
            row.style.paddingLeft = (0.7 + f.Depth * 1.4) + 'em';
            row.tabIndex = 0;
            row.setAttribute('role', 'button');
            row.appendChild(el('span', f.Depth ? '\ud83d\udcc1' : '\ud83d\udcc2'));
            row.appendChild(el('span', f.Depth ? f.Relative.split('/').pop() : folderName(f) + ' (your folder)', f.Depth ? '' : 'font-weight:bold'));
            row.lastChild.className = 'name';
            row.appendChild(el('span', f.Files + ' file' + (f.Files === 1 ? '' : 's') + ', ' + fmtBytes(f.Size)));
            row.lastChild.className = 'meta';
            const pick = () => {
                selFolder = f;
                folderMessage('ok', '');
                renderTree();
                fillFolderSelects();
            };
            row.addEventListener('click', pick);
            row.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    pick();
                }
            });
            box.appendChild(row);
        }
        const inner = selFolder && selFolder.Depth > 0;
        $('folderRename').disabled = !inner;
        $('folderMove').disabled = !inner;
        $('folderDelete').disabled = !inner;
        $('folderDelete').classList.toggle('hidden', !(me && me.CanDelete));
    }

    async function folderAction(path, body, done) {
        try {
            const r = await request('POST', api(path), body);
            if (!r.Ok) {
                folderMessage('bad', r.Error || 'It did not work.');
                return;
            }
            folderMessage('ok', done);
            filesLoaded = false;
            await loadFolders(r.NewPath || null);
        } catch (err) {
            folderMessage('bad', err.message);
        }
    }

    $('folderRefresh').addEventListener('click', () => loadFolders());

    $('folderNew').addEventListener('click', () => {
        const parent = selFolder || folderList[0];
        if (!parent) return;
        const name = window.prompt('Name of the new folder inside "' + folderName(parent) + '":', '');
        if (name === null || !name.trim()) return;
        folderAction('Folders/Create', { Path: parent.Path, Name: name.trim() }, 'Folder "' + name.trim() + '" made.');
    });

    $('folderRename').addEventListener('click', () => {
        if (!selFolder || !selFolder.Depth) return;
        const old = selFolder.Relative.split('/').pop();
        const name = window.prompt('New name for "' + old + '":', old);
        if (name === null || !name.trim() || name.trim() === old) return;
        folderAction('Folders/Rename', { Path: selFolder.Path, Name: name.trim() }, 'Renamed to "' + name.trim() + '".');
    });

    $('folderMove').addEventListener('click', () => {
        const target = $('folderMoveTarget').value;
        if (!selFolder || !selFolder.Depth || !target) return;
        const into = folderList.find((f) => f.Path === target);
        folderAction('Folders/Move', { Path: selFolder.Path, Target: target }, 'Moved into "' + (into ? folderName(into) : target) + '".');
    });

    $('folderDelete').addEventListener('click', () => {
        if (!selFolder || !selFolder.Depth) return;
        const f = selFolder;
        const what = f.Files ? f.Files + ' file(s), ' + fmtBytes(f.Size) : 'it is empty';
        if (!window.confirm('Delete the folder "' + folderName(f) + '" and everything in it (' + what + ') for good?\n\nThis cannot be undone.')) return;
        const parent = folderList.find((x) => x.Path === f.Path.replace(/[\\/][^\\/]+$/, ''));
        selFolder = parent || null;
        folderAction('Folders/Delete', { Path: f.Path }, 'Folder deleted.');
    });

    $('folder').addEventListener('change', () => store('mediaDownloaderFolder', $('folder').value));

    // ---------- start ----------

    async function start(info) {
        me = info;
        $('who').textContent = 'Signed in as ' + info.UserName;
        $('signOut').classList.remove('hidden');
        if (!info.Allowed) {
            show('denied');
            $('denied').textContent = 'Your account (' + info.UserName + ') may not use the downloader yet. Ask the server\'s administrator to allow it: Dashboard › Media Downloader › Settings › People who may download.';
            return;
        }
        await loadFolders();
        $('height').value = String(info.DefaultMaxHeight);
        $('deleteSelected').classList.toggle('hidden', !info.CanDelete);
        show('app');
        showTab(load('mediaDownloaderUserTab') || 'download');
        await refreshJobs();
        startPolling();
    }

    $('openJellyfin').href = base + '/web/';

    (async () => {
        try {
            const pub = await request('GET', base + '/System/Info/Public', undefined, '');
            serverId = pub.Id || '';
        } catch (e) {
            // links work without it
        }
        try {
            const info = await signIn();
            if (info) await start(info); else showLogin();
        } catch (err) {
            show('denied');
            $('denied').textContent = 'The downloader does not answer: ' + err.message;
        }
    })();
})();

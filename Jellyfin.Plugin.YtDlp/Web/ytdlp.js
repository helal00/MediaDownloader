const pluginId = '16118734-a017-4a44-8210-7baef431cea4';

function api(path) {
    return ApiClient.getUrl('YtDlp/' + path);
}

// The subtitle picker is shared with the user page; it is loaded once from the plugin.
let subsPicker = null;
let subsLoading = null;

function loadSubsScript() {
    if (window.MediaDownloaderSubs) return Promise.resolve();
    if (!subsLoading) {
        subsLoading = new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = ApiClient.getUrl('MediaDownloader/subs.js');
            s.onload = () => resolve();
            s.onerror = () => {
                subsLoading = null;
                reject(new Error('The subtitle picker did not load.'));
            };
            document.head.appendChild(s);
        });
    }
    return subsLoading;
}

function subName(code) {
    return window.MediaDownloaderSubs ? window.MediaDownloaderSubs.label(code) : code;
}

async function errorText(err) {
    try {
        if (err && typeof err.json === 'function') {
            const body = await err.json();
            if (body && body.error) {
                return body.error;
            }
        }
    } catch (e) {
        // not JSON
    }
    if (err && err.status) {
        return 'Request failed (' + err.status + ')';
    }
    return String(err && err.message ? err.message : err);
}

function post(path, body) {
    return ApiClient.ajax({
        type: 'POST',
        url: api(path),
        data: body === undefined ? undefined : JSON.stringify(body),
        contentType: 'application/json'
    });
}

function el(tag, text, style) {
    const e = document.createElement(tag);
    if (text !== undefined && text !== null) {
        e.textContent = text;
    }
    if (style) {
        e.setAttribute('style', style);
    }
    return e;
}

const itemIds = {};
const lastItemLookup = {};

function lookupItems(view, job) {
    const now = Date.now();
    if (lastItemLookup[job.Id] && now - lastItemLookup[job.Id] < 5000) {
        return;
    }
    lastItemLookup[job.Id] = now;
    ApiClient.getJSON(api('Jobs/' + encodeURIComponent(job.Id) + '/Items')).then((items) => {
        let found = false;
        for (const i of items) {
            if (i.itemId && !itemIds[i.file]) {
                itemIds[i.file] = i.itemId;
                found = true;
            }
        }
        if (found) {
            refreshJobs(view);
        }
    }).catch(() => { /* next poll */ });
}

function itemLink(itemId) {
    const a = el('a', 'Open in Jellyfin to play', 'display:inline-block;margin:.4em 0;padding:.4em .9em;border-radius:4px;background:#00a4dc;color:#fff;text-decoration:none;font-weight:bold');
    a.href = '#/details?id=' + encodeURIComponent(itemId) + '&serverId=' + encodeURIComponent(ApiClient.serverId());
    return a;
}

const stateText = {
    Queued: 'Waiting',
    Running: 'Downloading',
    Done: 'Finished',
    Failed: 'Failed',
    Cancelled: 'Cancelled'
};

function renderJobs(view, jobs) {
    const box = view.querySelector('#ytdlpJobs');
    box.replaceChildren();
    if (!jobs.length) {
        box.appendChild(el('div', 'No downloads yet.', 'opacity:.7'));
        return;
    }
    for (const job of jobs) {
        const card = el('div', null, 'padding:.8em;margin-bottom:.8em;border-radius:6px;background:rgba(255,255,255,.06)');
        card.appendChild(el('div', job.Title || job.Request.Url, 'font-weight:bold;word-break:break-all'));
        let line = stateText[job.State] || job.State;
        if (job.State === 'Running') {
            line += ' ' + (job.Percent || 0).toFixed(1) + '%';
            if (job.Speed) line += ' at ' + job.Speed;
            if (job.Eta) line += ', ' + job.Eta + ' left';
        }
        line += ' | ' + job.Request.Kind + ' | ' + job.Request.TargetFolder;
        if (job.OwnerName) line += ' | by ' + job.OwnerName;
        card.appendChild(el('div', line, 'opacity:.85;margin-top:.3em'));
        if (job.State === 'Running' || job.State === 'Queued') {
            const bar = el('div', null, 'height:6px;background:rgba(255,255,255,.15);border-radius:3px;margin-top:.4em');
            bar.appendChild(el('div', null, 'height:6px;border-radius:3px;background:#00a4dc;width:' + Math.min(100, job.Percent || 0) + '%'));
            card.appendChild(bar);
            const cancel = el('button', 'Cancel', 'margin-top:.5em');
            cancel.setAttribute('is', 'emby-button');
            cancel.className = 'raised';
            cancel.addEventListener('click', () => post('Jobs/' + encodeURIComponent(job.Id) + '/Cancel').then(() => refreshJobs(view)));
            card.appendChild(cancel);
        }
        let waiting = false;
        for (const f of job.Files || []) {
            card.appendChild(el('div', 'Saved: ' + f, 'font-size:.9em;opacity:.8;word-break:break-all'));
            if (job.State === 'Done') {
                if (itemIds[f]) {
                    card.appendChild(itemLink(itemIds[f]));
                } else {
                    card.appendChild(el('div', 'Waiting for Jellyfin to add it to the library...', 'font-size:.9em;opacity:.7'));
                    waiting = true;
                }
            }
        }
        if (waiting) {
            lookupItems(view, job);
        }
        if (job.Error) {
            card.appendChild(el('div', job.Error, (job.State === 'Done' ? 'color:#ffd54f' : 'color:#e57373') + ';margin-top:.3em;word-break:break-word'));
        }
        if (job.SubtitlesSaved && job.SubtitlesSaved.length) {
            card.appendChild(el('div', 'Subtitles saved: ' + job.SubtitlesSaved.map(subName).join(', '), 'font-size:.9em;opacity:.8;margin-top:.3em'));
        }
        if (job.Warning) {
            card.appendChild(el('div', job.Warning, 'color:#ffd54f;margin-top:.3em;word-break:break-word'));
        }
        if (job.State === 'Failed' && job.Log && job.Log.length) {
            const details = el('details', null, 'margin-top:.3em');
            details.appendChild(el('summary', 'yt-dlp output'));
            details.appendChild(el('pre', job.Log.join('\n'), 'white-space:pre-wrap;font-size:.8em'));
            card.appendChild(details);
        }
        box.appendChild(card);
    }
}

let lastActive = 0;

// ---------- tabs ----------

function showTab(view, name) {
    for (const t of view.querySelectorAll('.md-tab')) {
        t.classList.toggle('active', t.dataset.tab === name);
    }
    for (const p of view.querySelectorAll('.md-panel')) {
        p.classList.toggle('active', p.dataset.panel === name);
    }
    try {
        localStorage.setItem('mediaDownloaderTab', name);
    } catch (e) {
        // storage blocked
    }
    if (name === 'storage' && !storageLoaded) {
        loadStorage(view);
    }
}

// ---------- storage ----------

let storage = null;
let storageLoaded = false;
const selected = new Set();

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

function watchState(f) {
    if (!f.ItemId) return 'unknown';
    if (!f.Watched || !f.Watched.length) return 'unwatched';
    return f.Watched.some((w) => w.Played) ? 'watched' : 'started';
}

function watchText(f) {
    if (!f.Media) return 'Not a media file';
    if (!f.ItemId) return 'Not in Jellyfin\'s library';
    if (!f.Watched || !f.Watched.length) return 'Not watched';
    return f.Watched.map((w) => w.User + ': ' + (w.Played ? 'watched' : w.Percent + '%') + (w.LastPlayed ? ' (' + fmtDate(w.LastPlayed) + ')' : '')).join('; ');
}

function loadStorage(view) {
    const info = view.querySelector('#mdStorageInfo');
    info.textContent = 'Reading the library folders...';
    return ApiClient.getJSON(api('Storage')).then((r) => {
        storage = r;
        storageLoaded = true;
        for (const p of Array.from(selected)) {
            if (!r.Files.some((f) => f.Path === p)) selected.delete(p);
        }
        info.textContent = 'Read at ' + new Date().toLocaleTimeString();
        renderDrives(view);
        renderFolders(view);
        const lib = view.querySelector('#mdFilterLib');
        const current = lib.value;
        lib.replaceChildren();
        const all = el('option', 'All libraries');
        all.value = '';
        lib.appendChild(all);
        for (const name of [...new Set(r.Folders.map((f) => f.Library))]) {
            const o = el('option', name);
            o.value = name;
            lib.appendChild(o);
        }
        lib.value = current || '';
        renderFiles(view);
    }).catch(async (err) => {
        info.textContent = 'Could not read the library folders: ' + await errorText(err);
    });
}

function renderDrives(view) {
    const box = view.querySelector('#mdDrives');
    box.replaceChildren();
    for (const d of storage.Drives) {
        const used = d.Total - d.Free;
        const pct = d.Total ? Math.round(used * 100 / d.Total) : 0;
        const card = el('div', null);
        card.className = 'md-card';
        card.appendChild(el('div', 'Drive ' + d.Name + ': ' + fmtBytes(d.Free) + ' free of ' + fmtBytes(d.Total) + ' (' + pct + '% used)', 'font-weight:bold'));
        const bar = el('div', null);
        bar.className = 'md-bar';
        bar.appendChild(el('div', null, 'width:' + pct + '%;' + (pct > 90 ? 'background:#e53935' : '')));
        card.appendChild(bar);
        box.appendChild(card);
    }
}

function renderFolders(view) {
    const t = view.querySelector('#mdFolders');
    t.replaceChildren();
    const head = el('tr');
    for (const h of ['Library', 'Folder', 'Media files', 'Size (all files)']) head.appendChild(el('th', h));
    t.appendChild(head);
    for (const f of storage.Folders) {
        const tr = el('tr');
        tr.appendChild(el('td', f.Library));
        tr.appendChild(el('td', f.Folder, 'word-break:break-all'));
        const n = el('td', String(f.MediaFiles));
        n.className = 'num';
        tr.appendChild(n);
        const s = el('td', fmtBytes(f.Size));
        s.className = 'num';
        tr.appendChild(s);
        t.appendChild(tr);
    }
}

function shownFiles(view) {
    if (!storage) return [];
    const lib = view.querySelector('#mdFilterLib').value;
    const watch = view.querySelector('#mdFilterWatch').value;
    const text = view.querySelector('#mdFilterText').value.trim().toLowerCase();
    return storage.Files.filter((f) => (!lib || f.Library === lib)
        && (watch === 'all' || watchState(f) === watch)
        && (!text || f.Relative.toLowerCase().includes(text) || (f.Title || '').toLowerCase().includes(text)));
}

function updateDeleteLabel(view) {
    let bytes = 0;
    for (const f of storage ? storage.Files : []) {
        if (selected.has(f.Path)) bytes += f.Size;
    }
    view.querySelector('#mdDeleteLabel').textContent = selected.size
        ? 'Delete selected (' + selected.size + ', ' + fmtBytes(bytes) + ')'
        : 'Delete selected';
}

function renderFiles(view) {
    const t = view.querySelector('#mdFiles');
    t.replaceChildren();
    const files = shownFiles(view);
    const head = el('tr');
    for (const h of ['', 'File', 'Library', 'Size', 'Added', 'Watched']) head.appendChild(el('th', h));
    t.appendChild(head);
    if (!files.length) {
        const tr = el('tr');
        const td = el('td', storage ? 'No files match.' : '', 'opacity:.7');
        td.colSpan = 6;
        tr.appendChild(td);
        t.appendChild(tr);
    }
    for (const f of files) {
        const tr = el('tr');
        const cbTd = el('td');
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = selected.has(f.Path);
        cb.addEventListener('change', () => {
            if (cb.checked) selected.add(f.Path); else selected.delete(f.Path);
            updateDeleteLabel(view);
        });
        cbTd.appendChild(cb);
        tr.appendChild(cbTd);

        const nameTd = el('td', null, 'word-break:break-word');
        if (f.ItemId) {
            const a = el('a', f.Title || f.Relative);
            a.className = 'md-link';
            a.href = '#/details?id=' + encodeURIComponent(f.ItemId) + '&serverId=' + encodeURIComponent(ApiClient.serverId());
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
        const color = { watched: '#81c784', started: '#ffd54f', unwatched: 'inherit', unknown: '#bdbdbd' }[state];
        tr.appendChild(el('td', watchText(f), 'color:' + color));
        t.appendChild(tr);
    }
    updateDeleteLabel(view);
}

function deleteSelected(view) {
    const files = storage ? storage.Files.filter((f) => selected.has(f.Path)) : [];
    if (!files.length) return;
    const bytes = files.reduce((a, f) => a + f.Size, 0);
    const list = files.slice(0, 15).map((f) => '  ' + f.Relative).join('\n') + (files.length > 15 ? '\n  ... and ' + (files.length - 15) + ' more' : '');
    if (!window.confirm('Delete ' + files.length + ' file(s), ' + fmtBytes(bytes) + ', for good?\n\n' + list + '\n\nTheir posters, .nfo and subtitle files go too. This cannot be undone.')) {
        return;
    }
    const result = view.querySelector('#mdDeleteResult');
    result.textContent = 'Deleting...';
    ApiClient.ajax({ type: 'POST', url: api('Storage/Delete'), data: JSON.stringify({ Paths: files.map((f) => f.Path) }), contentType: 'application/json', dataType: 'json' }).then((rs) => {
        const ok = rs.filter((r) => r.Deleted);
        const bad = rs.filter((r) => !r.Deleted);
        result.replaceChildren();
        result.appendChild(el('div', 'Deleted ' + ok.length + ' file(s), ' + fmtBytes(ok.reduce((a, r) => a + r.Freed, 0)) + ' freed.', 'color:#81c784'));
        for (const r of bad) result.appendChild(el('div', 'Not deleted: ' + r.Path + ' \u2013 ' + r.Error, 'color:#e57373;word-break:break-all'));
        for (const r of ok) selected.delete(r.Path);
        loadStorage(view);
    }).catch(async (err) => {
        result.textContent = 'Delete failed: ' + await errorText(err);
    });
}

function refreshJobs(view) {
    return ApiClient.getJSON(api('Jobs')).then((jobs) => {
        renderJobs(view, jobs);
        const active = jobs.filter((j) => j.State === 'Running' || j.State === 'Queued').length;
        view.querySelector('#mdJobCount').textContent = active ? '(' + active + ')' : '';
        if (active < lastActive) {
            refreshStatus(view);
        }
        lastActive = active;
    }).catch(() => { /* next poll */ });
}

const supportedSitesUrl = 'https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md';

// Example link shapes for common sites (shown when a link of that site is not accepted).
const linkExamples = {
    youtube: ['https://www.youtube.com/watch?v=VIDEO_ID', 'https://youtu.be/VIDEO_ID', 'https://www.youtube.com/playlist?list=PLAYLIST_ID', 'https://www.youtube.com/@CHANNEL (whole channel)', 'https://www.youtube.com/shorts/VIDEO_ID'],
    youtu: ['https://youtu.be/VIDEO_ID'],
    facebook: ['https://www.facebook.com/watch/?v=VIDEO_ID', 'https://www.facebook.com/PAGE/videos/VIDEO_ID/', 'https://www.facebook.com/reel/REEL_ID', 'https://fb.watch/CODE/'],
    fb: ['https://fb.watch/CODE/'],
    instagram: ['https://www.instagram.com/p/POST_ID/', 'https://www.instagram.com/reel/REEL_ID/', 'https://www.instagram.com/stories/USER/STORY_ID/ (needs login cookies)'],
    tiktok: ['https://www.tiktok.com/@USER/video/VIDEO_ID', 'https://vm.tiktok.com/CODE/', 'https://www.tiktok.com/@USER (all videos)'],
    x: ['https://x.com/USER/status/POST_ID'],
    twitter: ['https://twitter.com/USER/status/POST_ID'],
    vimeo: ['https://vimeo.com/VIDEO_ID', 'https://vimeo.com/channels/NAME/VIDEO_ID', 'https://vimeo.com/showcase/ID'],
    dailymotion: ['https://www.dailymotion.com/video/VIDEO_ID', 'https://www.dailymotion.com/playlist/ID'],
    bbc: ['https://www.bbc.co.uk/iplayer/episode/EPISODE_ID/NAME (UK only)', 'https://www.bbc.co.uk/programmes/PROGRAMME_ID', 'https://www.bbc.com/news/av/ARTICLE'],
    soundcloud: ['https://soundcloud.com/ARTIST/TRACK', 'https://soundcloud.com/ARTIST/sets/PLAYLIST'],
    twitch: ['https://www.twitch.tv/videos/VIDEO_ID', 'https://clips.twitch.tv/CLIP', 'https://www.twitch.tv/CHANNEL (live now)'],
    reddit: ['https://www.reddit.com/r/SUB/comments/POST_ID/TITLE/'],
    bilibili: ['https://www.bilibili.com/video/BV_ID', 'https://www.bilibili.tv/en/video/ID'],
    rumble: ['https://rumble.com/VIDEO-NAME.html'],
    odysee: ['https://odysee.com/@CHANNEL/VIDEO'],
    archive: ['https://archive.org/details/ITEM_ID'],
    imdb: ['https://www.imdb.com/video/VIDEO_ID/'],
    vk: ['https://vk.com/video-OWNER_ID_VIDEO_ID'],
    ok: ['https://ok.ru/video/VIDEO_ID'],
    telegram: ['https://t.me/CHANNEL/POST_ID'],
    t: ['https://t.me/CHANNEL/POST_ID'],
    pinterest: ['https://www.pinterest.com/pin/PIN_ID/'],
    linkedin: ['https://www.linkedin.com/posts/POST (public posts)'],
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
let checkSeq = 0;
let checkTimer = null;

function fmtDuration(sec) {
    if (!sec) return '';
    const s = Math.round(sec);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const r = s % 60;
    return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(r).padStart(2, '0');
}

function showCheck(view, kind, parts) {
    const box = view.querySelector('#ytdlpCheck');
    box.replaceChildren();
    if (!kind) {
        box.style.display = 'none';
        return;
    }
    const colors = { ok: 'rgba(76,175,80,.18)', warn: 'rgba(255,193,7,.18)', bad: 'rgba(229,115,115,.18)', busy: 'rgba(255,255,255,.06)' };
    box.style.display = 'block';
    box.style.background = colors[kind];
    for (const p of parts) {
        box.appendChild(p);
    }
}

function checkLink(view) {
    const url = view.querySelector('#ytdlpUrl').value.trim();
    const seq = ++checkSeq;
    if (subsPicker) subsPicker.clearAvailable();
    if (!/^https?:\/\/\S+$/i.test(url)) {
        showCheck(view, url ? 'bad' : null, [el('div', 'Paste a full link starting with https://')]);
        return;
    }
    showCheck(view, 'busy', [el('div', 'Checking the link with yt-dlp...')]);
    ApiClient.ajax({ type: 'POST', url: api('Check'), data: JSON.stringify({ Url: url }), contentType: 'application/json', dataType: 'json' }).then((r) => {
        if (seq !== checkSeq) return;
        const parts = [];
        if (r.Result === 'supported' || r.Result === 'generic') {
            let head = r.Result === 'supported'
                ? 'Supported (site handler: ' + r.Site + ')'
                : 'No dedicated handler for this site, but media was found on the page';
            parts.push(el('div', '\u2714 ' + head, 'font-weight:bold'));
            if (r.Kind === 'playlist') {
                parts.push(el('div', 'Playlist or channel: ' + (r.Title || '') + (r.Items ? ' (' + r.Items + (r.Items >= 200 ? '+' : '') + ' items)' : '')));
                parts.push(el('div', 'Tick "Whole playlist or channel" to download all of it; otherwise only one video is taken.', 'opacity:.8'));
            } else {
                let line = r.Title || '(no title)';
                if (r.Uploader) line += ' \u2013 ' + r.Uploader;
                if (r.Duration) line += ' (' + fmtDuration(r.Duration) + ')';
                parts.push(el('div', line));
                if (subsPicker) subsPicker.setAvailable(r.Subtitles || []);
            }
            showCheck(view, r.Result === 'supported' ? 'ok' : 'warn', parts);
            return;
        }
        parts.push(el('div', r.Result === 'unsupported' ? '\u2718 This link is not supported' : '\u2718 yt-dlp could not read this link', 'font-weight:bold'));
        if (r.Message) parts.push(el('div', r.Message, 'opacity:.85'));
        const examples = linkExamples[siteName(url)];
        if (examples && examples.length) {
            parts.push(el('div', 'Links of this site that work look like:', 'margin-top:.5em;font-weight:bold'));
            for (const ex of examples) {
                parts.push(el('div', ex, 'font-family:monospace;font-size:.9em;padding-left:1em'));
            }
        }
        if (r.Handlers && r.Handlers.length) {
            parts.push(el('div', 'yt-dlp has these handlers for this site, so it takes these kinds of link (paste the page where the video itself plays, not the home page):', 'margin-top:.4em'));
            parts.push(el('div', r.Handlers.join(', '), 'font-family:monospace;font-size:.9em'));
        } else if (r.Result === 'unsupported') {
            parts.push(el('div', 'yt-dlp has no handler for this site, and found no video on the page.', 'margin-top:.4em'));
        }
        const a = el('a', 'List of supported sites', 'display:inline-block;margin-top:.5em;color:#8fd3ff;font-weight:bold');
        a.href = supportedSitesUrl;
        a.target = '_blank';
        a.rel = 'noopener';
        parts.push(a);
        showCheck(view, 'bad', parts);
    }).catch(async (err) => {
        if (seq !== checkSeq) return;
        showCheck(view, 'bad', [el('div', 'Could not check: ' + await errorText(err))]);
    });
}

function refreshStatus(view) {
    return ApiClient.getJSON(api('Status')).then((s) => {
        view.querySelector('#ytdlpStatus').textContent =
            (s.sites ? 'Sites supported: ' + s.sites + ' | ' : '') + 'yt-dlp: ' + (s.version || 'not downloaded yet (fetched on the first download)') +
            ' | deno: ' + (s.deno || 'not downloaded yet') +
            ' | Program: ' + s.path + ' | Runs as: ' + s.account;
    });
}

let libraryFolders = [];
let siteUsers = [];
let savedAccess = [];

function renderUsers(view) {
    const box = view.querySelector('#mdUsers');
    box.replaceChildren();
    const others = siteUsers.filter((u) => !u.isAdmin);
    if (!others.length) {
        box.appendChild(el('div', 'Every user is an administrator, so everyone can use the downloader already.', 'opacity:.7;margin-bottom:1em'));
        return;
    }
    for (const u of others) {
        const entry = savedAccess.find((a) => (a.UserId || '').replace(/-/g, '').toLowerCase() === u.id.toLowerCase());
        const card = el('div', null);
        card.className = 'md-card mdUserRow';
        card.dataset.userId = u.id;
        card.dataset.userName = u.name;

        const allow = document.createElement('input');
        allow.type = 'checkbox';
        allow.className = 'mdUserAllow';
        allow.checked = !!entry;
        const allowLabel = el('label', null, 'display:flex;gap:.6em;align-items:center;font-weight:bold;cursor:pointer');
        allowLabel.appendChild(allow);
        allowLabel.appendChild(el('span', u.name + ' may use the downloader'));
        card.appendChild(allowLabel);

        const details = el('div', null, 'margin:.6em 0 0 1.8em');
        const del = document.createElement('input');
        del.type = 'checkbox';
        del.className = 'mdUserDelete';
        del.checked = entry ? entry.CanDelete !== false : true;
        const delLabel = el('label', null, 'display:flex;gap:.6em;align-items:center;margin-bottom:.6em;cursor:pointer');
        delLabel.appendChild(del);
        delLabel.appendChild(el('span', 'May delete files in their folders'));
        details.appendChild(delLabel);

        details.appendChild(el('div', 'Their folders, one full path per line:', 'opacity:.8;margin-bottom:.3em'));
        const folders = document.createElement('textarea');
        folders.className = 'mdUserFolders';
        folders.rows = 3;
        folders.setAttribute('style', 'width:100%;font-family:monospace;background:rgba(0,0,0,.25);color:inherit;border:1px solid rgba(255,255,255,.2);border-radius:4px;padding:.4em');
        folders.value = entry ? (entry.Folders || []).join('\n') : '';
        details.appendChild(folders);

        const add = el('div', null, 'display:flex;flex-wrap:wrap;gap:.5em;align-items:center;margin-top:.4em');
        const pick = document.createElement('select');
        pick.setAttribute('style', 'background:rgba(0,0,0,.25);color:inherit;border:1px solid rgba(255,255,255,.2);border-radius:4px;padding:.3em');
        for (const f of libraryFolders) {
            const o = el('option', f.library + ': ' + f.folder);
            o.value = f.folder;
            pick.appendChild(o);
        }
        const sub = document.createElement('input');
        sub.type = 'text';
        sub.placeholder = 'sub folder, e.g. ' + u.name;
        sub.value = u.name;
        sub.setAttribute('style', 'background:rgba(0,0,0,.25);color:inherit;border:1px solid rgba(255,255,255,.2);border-radius:4px;padding:.3em;min-width:10em');
        const addBtn = el('button', 'Add folder');
        addBtn.type = 'button';
        addBtn.setAttribute('is', 'emby-button');
        addBtn.className = 'raised';
        addBtn.addEventListener('click', () => {
            if (!pick.value) return;
            const sep = pick.value.includes('\\') ? '\\' : '/';
            const name = sub.value.trim().replace(/^[\\/]+|[\\/]+$/g, '');
            const path = pick.value.replace(/[\\/]+$/, '') + (name ? sep + name : '');
            const lines = folders.value.split('\n').map((l) => l.trim()).filter(Boolean);
            if (!lines.includes(path)) lines.push(path);
            folders.value = lines.join('\n');
            allow.checked = true;
            details.style.display = '';
        });
        add.appendChild(pick);
        add.appendChild(el('span', '\\', 'opacity:.6'));
        add.appendChild(sub);
        add.appendChild(addBtn);
        details.appendChild(add);
        card.appendChild(details);

        details.style.display = allow.checked ? '' : 'none';
        allow.addEventListener('change', () => {
            details.style.display = allow.checked ? '' : 'none';
        });
        box.appendChild(card);
    }
}

function collectUsers(view) {
    const list = [];
    for (const row of view.querySelectorAll('.mdUserRow')) {
        if (!row.querySelector('.mdUserAllow').checked) continue;
        list.push({
            UserId: row.dataset.userId,
            UserName: row.dataset.userName,
            CanDelete: row.querySelector('.mdUserDelete').checked,
            Folders: row.querySelector('.mdUserFolders').value.split('\n').map((l) => l.trim()).filter(Boolean)
        });
    }
    return list;
}

function loadUsers(view) {
    const link = view.querySelector('#mdUserPageLink');
    const url = ApiClient.serverAddress().replace(/\/+$/, '') + '/MediaDownloader';
    link.textContent = url;
    link.href = url;
    return ApiClient.getJSON(api('Users')).then((users) => {
        siteUsers = users;
        renderUsers(view);
    });
}

function loadFolders(view) {
    return ApiClient.getJSON(api('Folders')).then((folders) => {
        libraryFolders = folders;
        const select = view.querySelector('#ytdlpFolder');
        select.replaceChildren();
        for (const f of folders) {
            const o = el('option', f.library + ': ' + f.folder);
            o.value = f.folder;
            o.dataset.type = f.type || '';
            select.appendChild(o);
        }
        pickFolderForKind(view);
    });
}

function pickFolderForKind(view) {
    const kind = view.querySelector('#ytdlpKind').value;
    const wanted = { Movie: 'movies', Episode: 'tvshows', Music: 'music', Video: 'homevideos' }[kind];
    const select = view.querySelector('#ytdlpFolder');
    for (const o of select.options) {
        if ((o.dataset.type || '').toLowerCase() === wanted) {
            select.value = o.value;
            return;
        }
    }
}

function loadSettings(view) {
    return ApiClient.getPluginConfiguration(pluginId).then((c) => {
        view.querySelector('#cfgAutoUpdate').checked = c.AutoUpdate;
        view.querySelector('#cfgTv').checked = c.PreferTvCompatible;
        view.querySelector('#cfgHeight').value = String(c.DefaultMaxHeight);
        view.querySelector('#ytdlpHeight').value = String(c.DefaultMaxHeight);
        view.querySelector('#cfgNfo').checked = c.WriteNfo;
        view.querySelector('#cfgScan').checked = c.ScanAfterDownload;
        view.querySelector('#cfgCustomPath').value = c.CustomYtDlpPath || '';
        view.querySelector('#cfgExtra').value = c.ExtraArguments || '';
        view.querySelector('#cfgDeno').checked = c.UseDeno;
        view.querySelector('#cfgClients').value = c.YouTubePlayerClient || '';
        view.querySelector('#cfgMenuLink').checked = c.ShowMenuLink !== false;
        view.querySelector('#cfgMenuLinkName').value = c.MenuLinkName || 'Media Downloader';
        savedAccess = c.UserAccess || [];
    });
}

export default function (view) {
    let timer = null;

    for (const t of view.querySelectorAll('.md-tab')) {
        t.addEventListener('click', () => showTab(view, t.dataset.tab));
    }
    let firstTab = 'download';
    try {
        firstTab = localStorage.getItem('mediaDownloaderTab') || 'download';
    } catch (e) {
        // storage blocked
    }
    showTab(view, firstTab);

    view.querySelector('#mdStorageRefresh').addEventListener('click', () => loadStorage(view));
    view.querySelector('#mdFilterLib').addEventListener('change', () => renderFiles(view));
    view.querySelector('#mdFilterWatch').addEventListener('change', () => renderFiles(view));
    view.querySelector('#mdFilterText').addEventListener('input', () => renderFiles(view));
    view.querySelector('#mdSelectShown').addEventListener('click', () => {
        for (const f of shownFiles(view)) selected.add(f.Path);
        renderFiles(view);
    });
    view.querySelector('#mdSelectNone').addEventListener('click', () => {
        selected.clear();
        renderFiles(view);
    });
    view.querySelector('#mdDelete').addEventListener('click', () => deleteSelected(view));

    const showSubs = () => {
        if (subsPicker) subsPicker.setVisible(view.querySelector('#ytdlpKind').value !== 'Music');
    };
    view.querySelector('#ytdlpKind').addEventListener('change', () => {
        pickFolderForKind(view);
        showSubs();
    });
    loadSubsScript().then(() => {
        const box = view.querySelector('#ytdlpSubsPicker');
        box.textContent = '';
        subsPicker = window.MediaDownloaderSubs.create(box, { storageKey: 'mediaDownloaderSubtitles' });
        showSubs();
    }).catch((err) => {
        view.querySelector('#ytdlpSubsPicker').textContent = err.message;
    });

    const urlInput = view.querySelector('#ytdlpUrl');
    urlInput.addEventListener('input', () => {
        clearTimeout(checkTimer);
        checkTimer = setTimeout(() => checkLink(view), 700);
    });

    view.querySelector('#ytdlpDownloadForm').addEventListener('submit', (e) => {
        e.preventDefault();
        const errorBox = view.querySelector('#ytdlpFormError');
        errorBox.textContent = '';
        const request = {
            Url: view.querySelector('#ytdlpUrl').value.trim(),
            Kind: view.querySelector('#ytdlpKind').value,
            TargetFolder: view.querySelector('#ytdlpFolder').value,
            SubFolder: view.querySelector('#ytdlpSubFolder').value.trim(),
            MaxHeight: parseInt(view.querySelector('#ytdlpHeight').value, 10) || 0,
            WholePlaylist: view.querySelector('#ytdlpPlaylist').checked,
            SkipDownloaded: view.querySelector('#ytdlpSkip').checked,
            Subtitles: view.querySelector('#ytdlpKind').value === 'Music' || !subsPicker ? [] : subsPicker.get()
        };
        post('Jobs', request).then(() => {
            view.querySelector('#ytdlpUrl').value = '';
            checkSeq++;
            showCheck(view, null, []);
            if (subsPicker) subsPicker.clearAvailable();
            refreshJobs(view);
            showTab(view, 'jobs');
        }).catch(async (err) => {
            errorBox.textContent = await errorText(err);
        });
        return false;
    });

    view.querySelector('#ytdlpClear').addEventListener('click', () => post('Jobs/ClearFinished').then(() => refreshJobs(view)));

    view.querySelector('#ytdlpUpdate').addEventListener('click', () => {
        const result = view.querySelector('#ytdlpUpdateResult');
        result.textContent = 'Updating...';
        ApiClient.ajax({ type: 'POST', url: api('Update'), dataType: 'json' }).then((r) => {
            result.textContent = 'yt-dlp ' + r.version + ', deno ' + r.deno;
            refreshStatus(view);
        }).catch(async (err) => {
            result.textContent = await errorText(err);
        });
    });

    view.querySelector('#ytdlpSettingsForm').addEventListener('submit', (e) => {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((c) => {
            c.AutoUpdate = view.querySelector('#cfgAutoUpdate').checked;
            c.PreferTvCompatible = view.querySelector('#cfgTv').checked;
            c.DefaultMaxHeight = parseInt(view.querySelector('#cfgHeight').value, 10) || 0;
            c.WriteNfo = view.querySelector('#cfgNfo').checked;
            c.ScanAfterDownload = view.querySelector('#cfgScan').checked;
            c.CustomYtDlpPath = view.querySelector('#cfgCustomPath').value.trim();
            c.ExtraArguments = view.querySelector('#cfgExtra').value;
            c.UseDeno = view.querySelector('#cfgDeno').checked;
            c.YouTubePlayerClient = view.querySelector('#cfgClients').value.trim();
            c.ShowMenuLink = view.querySelector('#cfgMenuLink').checked;
            c.MenuLinkName = view.querySelector('#cfgMenuLinkName').value.trim() || 'Media Downloader';
            c.UserAccess = collectUsers(view);
            savedAccess = c.UserAccess;
            return ApiClient.updatePluginConfiguration(pluginId, c);
        }).then((r) => {
            Dashboard.processPluginConfigurationUpdateResult(r);
            refreshStatus(view);
        }).catch(() => Dashboard.hideLoadingMsg());
        return false;
    });

    view.addEventListener('viewshow', () => {
        Dashboard.showLoadingMsg();
        Promise.all([loadSettings(view), loadFolders(view), refreshStatus(view), refreshJobs(view)])
            .then(() => loadUsers(view))
            .catch(() => { /* shown per part */ })
            .finally(() => Dashboard.hideLoadingMsg());
        timer = setInterval(() => refreshJobs(view), 2000);
    });

    view.addEventListener('viewhide', () => {
        if (timer) {
            clearInterval(timer);
            timer = null;
        }
    });
}

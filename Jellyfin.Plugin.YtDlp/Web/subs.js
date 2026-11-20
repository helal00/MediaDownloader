// Media Downloader: subtitle language picker, shared by the user page and the dashboard page.
// window.MediaDownloaderSubs.create(container, options) builds it inside container and returns
// { get(), setAvailable(list), clearAvailable(), setVisible(bool) }.
(function () {
    'use strict';

    if (window.MediaDownloaderSubs) return;

    // ISO 639-1 codes, plus the regional and script variants sites use most.
    const CODES = ('aa ab ae af ak am an ar as av ay az ba be bg bh bi bm bn bo br bs ca ce ch co cr cs cu cv cy da de dv dz ee el en eo es et eu fa ff fi fj fo fr fy ga gd gl gn gu gv ha he hi ho hr ht hu hy hz ia id ie ig ii ik io is it iu ja jv ka kg ki kj kk kl km kn ko kr ks ku kv kw ky la lb lg li ln lo lt lu lv mg mh mi mk ml mn mr ms mt my na nb nd ne ng nl nn no nr nv ny oc oj om or os pa pi pl ps pt qu rm rn ro ru rw sa sc sd se sg si sk sl sm sn so sq sr ss st su sv sw ta te tg th ti tk tl tn to tr ts tt tw ty ug uk ur uz ve vi vo wa wo xh yi yo za zh zu ' +
        'en-US en-GB en-IN es-419 es-ES fr-CA pt-BR pt-PT zh-Hans zh-Hant zh-CN zh-TW zh-HK fil').split(' ');
    const CODE_RE = /^[A-Za-z0-9][A-Za-z0-9_-]{0,34}$/;
    const MAX = 20;

    let names = null;
    try {
        names = new Intl.DisplayNames(['en'], { type: 'language' });
    } catch (e) {
        names = null;
    }

    function langName(code) {
        if (names) {
            try {
                const n = names.of(code);
                if (n && n.toLowerCase() !== code.toLowerCase()) return n;
            } catch (e) {
                // not a code Intl knows
            }
        }
        return code;
    }

    function withCode(label, code) {
        return label && label.toLowerCase() !== code.toLowerCase() ? label + ' (' + code + ')' : code;
    }

    function el(tag, text, style) {
        const e = document.createElement(tag);
        if (text !== undefined && text !== null) e.textContent = text;
        if (style) e.setAttribute('style', style);
        return e;
    }

    function readStore(key) {
        try {
            const raw = localStorage.getItem(key);
            if (raw === null) return null;
            const v = JSON.parse(raw);
            return Array.isArray(v) ? v.filter((c) => typeof c === 'string' && CODE_RE.test(c)).slice(0, MAX) : null;
        } catch (e) {
            return null;
        }
    }

    function writeStore(key, list) {
        try {
            localStorage.setItem(key, JSON.stringify(list));
        } catch (e) {
            // storage blocked: the choice lasts until the page is closed
        }
    }

    function same(a, b) {
        return a.toLowerCase() === b.toLowerCase();
    }

    function create(container, options) {
        const opts = options || {};
        const key = opts.storageKey || 'mediaDownloaderSubtitles';
        let selected = readStore(key);
        if (selected === null) selected = (opts.defaults || ['en']).slice();
        let available = null; // null: no single video checked; otherwise [{Code, Name, Auto}]
        let open = false;

        const root = el('div', null, 'margin-bottom:1em');
        root.appendChild(el('div', 'Subtitles', 'font-size:.9em;opacity:.65;margin-bottom:.3em'));
        const summary = el('div', null, 'display:flex;flex-wrap:wrap;gap:.4em;align-items:center');
        root.appendChild(summary);
        const note = el('div', null, 'font-size:.85em;opacity:.7;margin-top:.3em');
        root.appendChild(note);
        const panel = el('div', null, 'display:none;margin-top:.5em;border:1px solid rgba(255,255,255,.15);border-radius:6px;padding:.6em');
        const filter = el('input', null, 'width:100%;box-sizing:border-box;padding:.5em .6em;font:inherit;color:inherit;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.15);border-radius:4px;margin-bottom:.5em');
        filter.type = 'text';
        filter.placeholder = 'Find a language or type a code (for example bn, pt-BR)';
        filter.setAttribute('aria-label', 'Find a subtitle language');
        panel.appendChild(filter);
        const list = el('div', null, 'max-height:16em;overflow-y:auto');
        panel.appendChild(list);
        root.appendChild(panel);
        container.appendChild(root);

        function button(text, onClick, extra) {
            const b = el('button', text, 'font:inherit;font-size:.9em;cursor:pointer;border-radius:4px;border:1px solid rgba(255,255,255,.2);background:rgba(255,255,255,.08);color:inherit;padding:.3em .7em;' + (extra || ''));
            b.type = 'button';
            b.addEventListener('click', onClick);
            return b;
        }

        function offered(code) {
            return !available || available.some((a) => same(a.Code, code));
        }

        function displayName(code) {
            const a = available && available.find((x) => same(x.Code, code));
            return (a && a.Name) || langName(code);
        }

        function save() {
            writeStore(key, selected);
            renderSummary();
            renderList();
        }

        function toggle(code, on) {
            const i = selected.findIndex((c) => same(c, code));
            if (on && i < 0) {
                if (selected.length >= MAX) return;
                selected.push(code);
            } else if (!on && i >= 0) {
                selected.splice(i, 1);
            }
            save();
        }

        function renderSummary() {
            summary.textContent = '';
            if (!selected.length) summary.appendChild(el('span', 'None', 'opacity:.8'));
            for (const code of selected) {
                const missing = !offered(code);
                const chip = el('span', null, 'display:inline-flex;align-items:center;gap:.3em;padding:.2em .2em .2em .6em;border-radius:1em;background:' + (missing ? 'rgba(255,255,255,.06)' : 'rgba(0,164,220,.25)') + ';font-size:.9em');
                chip.appendChild(el('span', withCode(displayName(code), code) + (missing ? ' – not on this video' : ''), missing ? 'opacity:.6' : ''));
                const x = button('×', () => toggle(code, false), 'border:none;background:none;padding:0 .4em;font-size:1.1em;line-height:1');
                x.setAttribute('aria-label', 'Remove ' + displayName(code));
                chip.appendChild(x);
                summary.appendChild(chip);
            }
            summary.appendChild(button(open ? 'Done' : 'Choose...', () => {
                open = !open;
                panel.style.display = open ? 'block' : 'none';
                renderSummary();
                if (open) {
                    renderList();
                    filter.focus();
                }
            }));
            if (selected.length) summary.appendChild(button('None', () => {
                selected = [];
                save();
            }));

            if (available === null) {
                note.textContent = 'Saved for each video when the site has them. A language the site refuses or does not have is skipped, and the download says so.';
            } else if (!available.length) {
                note.textContent = 'This video has no subtitles on the site.';
            } else {
                const own = available.filter((a) => !a.Auto).length;
                note.textContent = 'This video has ' + (own ? own + ' subtitle language' + (own === 1 ? '' : 's') + ' from the uploader' : 'no subtitles from the uploader') +
                    (available.length > own ? ' and ' + (available.length - own) + ' automatic (machine-made)' : '') + '.';
            }
        }

        function row(code, label, sub) {
            const on = selected.some((c) => same(c, code));
            const line = el('label', null, 'display:flex;gap:.6em;align-items:center;padding:.3em .2em;cursor:pointer');
            const box = el('input');
            box.type = 'checkbox';
            box.checked = on;
            box.disabled = !on && selected.length >= MAX;
            box.style.width = '1.1em';
            box.style.height = '1.1em';
            box.style.flex = '0 0 auto';
            box.addEventListener('change', () => toggle(code, box.checked));
            line.appendChild(box);
            line.appendChild(el('span', withCode(label, code), 'flex:1 1 auto'));
            if (sub) line.appendChild(el('span', sub, 'font-size:.8em;opacity:.6;white-space:nowrap'));
            return line;
        }

        function heading(text) {
            return el('div', text, 'font-size:.8em;text-transform:uppercase;letter-spacing:.05em;opacity:.6;margin:.6em 0 .2em 0');
        }

        function renderList() {
            if (!open) return;
            list.textContent = '';
            const q = filter.value.trim().toLowerCase();
            const match = (code, label) => !q || code.toLowerCase().includes(q) || label.toLowerCase().includes(q);
            const shown = new Set();
            let count = 0;

            if (available && available.length) {
                for (const [title, auto] of [['On this video, from the uploader', false], ['On this video, automatic', true]]) {
                    const items = available.filter((a) => a.Auto === auto && match(a.Code, a.Name || langName(a.Code)));
                    if (!items.length) continue;
                    list.appendChild(heading(title));
                    for (const a of items) {
                        list.appendChild(row(a.Code, a.Name || langName(a.Code), auto ? 'automatic' : ''));
                        shown.add(a.Code.toLowerCase());
                        count++;
                    }
                }
            }

            const others = [];
            for (const code of selected.concat(CODES)) {
                if (shown.has(code.toLowerCase())) continue;
                shown.add(code.toLowerCase());
                const label = langName(code);
                if (match(code, label)) others.push([code, label]);
            }
            others.sort((a, b) => a[1].localeCompare(b[1]));
            if (others.length) {
                list.appendChild(heading(available && available.length ? 'Other languages (not on this video)' : 'Languages'));
                for (const [code, label] of others) {
                    list.appendChild(row(code, label, ''));
                    count++;
                }
            }

            const typed = filter.value.trim();
            if (typed && CODE_RE.test(typed) && !shown.has(typed.toLowerCase())) {
                list.appendChild(heading('Other code'));
                list.appendChild(row(typed, langName(typed), 'code as typed'));
                count++;
            }
            if (!count) list.appendChild(el('div', 'No language matches. Type a language code to use it as it is.', 'opacity:.7;padding:.3em'));
            if (selected.length >= MAX) list.insertBefore(el('div', 'At most ' + MAX + ' languages.', 'opacity:.7;padding:.3em'), list.firstChild);
        }

        filter.addEventListener('input', renderList);
        filter.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') e.preventDefault();
        });
        renderSummary();

        return {
            get: () => selected.slice(),
            setAvailable(items) {
                available = Array.isArray(items) ? items.filter((a) => a && typeof a.Code === 'string' && a.Code) : [];
                renderSummary();
                renderList();
            },
            clearAvailable() {
                available = null;
                renderSummary();
                renderList();
            },
            setVisible(on) {
                root.style.display = on ? '' : 'none';
            }
        };
    }

    window.MediaDownloaderSubs = { create, langName, label: (code) => withCode(langName(code), code) };
})();

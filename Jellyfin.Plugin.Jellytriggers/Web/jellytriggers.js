// Jellytriggers — pane bundle for the Jellyfin web client.
// Loaded by File Transformation injecting <script src="/Plugins/Jellytriggers/script.js">
// into index.html.
//
// What this file does:
//   1. Watches for the user opening a movie's detail page.
//   2. Calls /Plugins/Jellytriggers/triggers/{itemId} via Jellyfin's ApiClient
//      so it inherits standard auth / base-URL handling.
//   3. Renders a small pane: trigger questions filtered to that viewer's DTDD
//      favorites, with vote totals, comment links, paywall badges, and a
//      manual refresh control.
(function () {
    'use strict';

    var STYLE_HREF = '/Plugins/Jellytriggers/style.css';
    var TRIGGERS_PATH = 'Plugins/Jellytriggers/triggers/';
    var DTDD_BASE = 'https://www.doesthedogdie.com/media/';

    /** Current item being shown, so we don't re-fetch every tick. */
    var lastItemId = null;
    /** Set to true while we're waiting for an in-flight fetch. */
    var inFlight = false;

    // ---- bootstrapping ------------------------------------------------------

    injectStylesheet();
    window.addEventListener('hashchange', maybeUpdate);
    window.addEventListener('popstate', maybeUpdate);
    document.addEventListener('viewshow', maybeUpdate);
    // Final safety net: Jellyfin's SPA can swap views in ways that don't fire
    // the events above. Cheap poll keeps us correct.
    setInterval(maybeUpdate, 1500);
    if (document.readyState !== 'loading') {
        maybeUpdate();
    } else {
        document.addEventListener('DOMContentLoaded', maybeUpdate);
    }

    // ---- core loop ----------------------------------------------------------

    function maybeUpdate() {
        var itemId = readItemIdFromUrl();
        if (!itemId) {
            lastItemId = null;
            return;
        }

        // Same item, pane already drawn? Nothing to do.
        if (itemId === lastItemId && document.querySelector('.jt-slot .jt-pane')) {
            return;
        }

        var slot = ensureSlot();
        if (!slot) {
            // The detail page hasn't fully rendered yet; we'll catch the next tick.
            return;
        }

        lastItemId = itemId;
        loadAndRender(slot, itemId, /*forceRefresh*/ false);
    }

    function loadAndRender(slot, itemId, forceRefresh) {
        if (inFlight) {
            return;
        }

        renderLoading(slot);
        inFlight = true;
        fetchTriggers(itemId, forceRefresh)
            .then(function (payload) {
                inFlight = false;
                renderPayload(slot, itemId, payload);
            })
            .catch(function (err) {
                inFlight = false;
                renderError(slot, err);
            });
    }

    // ---- rendering ----------------------------------------------------------

    function renderPayload(slot, itemId, payload) {
        clear(slot);

        var pane = el('section', 'jt-pane');
        pane.appendChild(renderHeader(slot, itemId));
        pane.appendChild(renderBody(payload));
        slot.appendChild(pane);
    }

    function renderHeader(slot, itemId) {
        var header = el('div', 'jt-pane-header');
        header.appendChild(text(el('h2', 'jt-pane-title'), 'Your triggers'));

        var refresh = el('button', 'jt-refresh');
        refresh.setAttribute('type', 'button');
        refresh.setAttribute('title', 'Re-check doesthedogdie.com');
        refresh.setAttribute('aria-label', 'Refresh triggers');
        refresh.innerHTML = '↻'; // ↻
        refresh.addEventListener('click', function () {
            loadAndRender(slot, itemId, /*forceRefresh*/ true);
        });
        header.appendChild(refresh);
        return header;
    }

    function renderBody(payload) {
        var body = el('div', 'jt-pane-body');
        var state = payload && payload.State;

        if (state === 'KeyMissing') {
            body.appendChild(renderKeyMissing());
        } else if (state === 'NotOnDoesTheDogDie') {
            body.appendChild(renderInfo(
                'This title isn’t on doesthedogdie.com yet. Add it there and refresh.'));
        } else if (state === 'UserHasNoFavorites') {
            body.appendChild(renderInfo(
                'No favorites you’ve marked on doesthedogdie.com apply to this movie.'));
        } else {
            body.appendChild(renderItems(payload.Items || [], payload.DtddMediaId));
        }

        return body;
    }

    function renderItems(items, mediaId) {
        var list = el('ul', 'jt-list');
        if (!items.length) {
            return text(el('div', 'jt-empty'), 'No matches.');
        }

        items.forEach(function (item) {
            var li = el('li', 'jt-item');

            var question = text(el('span', 'jt-question'), item.DoesName || '');
            li.appendChild(question);

            li.appendChild(renderVerdict(item));

            if (item.NumComments && item.Slug && mediaId) {
                var link = el('a', 'jt-link');
                link.href = DTDD_BASE + mediaId + '#' + item.Slug;
                link.target = '_blank';
                link.rel = 'noopener';
                link.textContent = item.NumComments + (item.NumComments === 1 ? ' comment' : ' comments');
                li.appendChild(link);
            }

            list.appendChild(li);
        });
        return list;
    }

    function renderVerdict(item) {
        if (item.Paywalled) {
            var locked = el('span', 'jt-verdict jt-paywalled');
            locked.setAttribute('title', 'Paywalled on doesthedogdie.com');
            locked.textContent = '🔒'; // 🔒
            return locked;
        }

        var yes = item.YesSum || 0;
        var no = item.NoSum || 0;
        var verdict = el('span', 'jt-verdict ' + (yes > no ? 'jt-yes' : 'jt-no'));
        verdict.textContent = yes > no ? 'Yes' : 'No';

        var counts = el('span', 'jt-counts');
        counts.textContent = yes + ' – ' + no; // y – n
        var wrap = el('span', 'jt-verdict-wrap');
        wrap.appendChild(verdict);
        wrap.appendChild(counts);
        return wrap;
    }

    function renderKeyMissing() {
        var box = el('div', 'jt-state jt-state-key');
        box.appendChild(text(el('p'),
            'You haven’t connected a Does The Dog Die API key yet.'));
        var p = el('p');
        p.appendChild(document.createTextNode('Get one from '));
        var link = el('a');
        link.href = 'https://www.doesthedogdie.com/api';
        link.target = '_blank';
        link.rel = 'noopener';
        link.textContent = 'doesthedogdie.com/api';
        p.appendChild(link);
        p.appendChild(document.createTextNode(', then add it on your user settings.'));
        box.appendChild(p);
        return box;
    }

    function renderInfo(message) {
        return text(el('div', 'jt-state jt-state-info'), message);
    }

    function renderLoading(slot) {
        clear(slot);
        var pane = el('section', 'jt-pane jt-loading');
        pane.appendChild(text(el('div', 'jt-pane-title'), 'Your triggers'));
        pane.appendChild(text(el('div', 'jt-loading-dot'), '…')); // …
        slot.appendChild(pane);
    }

    function renderError(slot, err) {
        clear(slot);
        var pane = el('section', 'jt-pane jt-error');
        pane.appendChild(text(el('div', 'jt-pane-title'), 'Your triggers'));
        pane.appendChild(text(el('div'), 'Couldn’t load: ' + (err && err.message ? err.message : err)));
        slot.appendChild(pane);
    }

    // ---- DOM placement ------------------------------------------------------

    function ensureSlot() {
        // Locate the visible item-detail page. Jellyfin marks inactive pages with
        // .hide so we filter those out.
        var pages = document.querySelectorAll('.itemDetailPage');
        var visible = null;
        for (var i = 0; i < pages.length; i++) {
            if (!pages[i].classList.contains('hide')) {
                visible = pages[i];
                break;
            }
        }

        if (!visible) {
            return null;
        }

        // Detail pages aren't only for movies — make sure we're looking at one.
        // The cheap signal is the URL itself; the body of the SPA hash will have
        // serverId and id, but the page DOM is shared across content types.
        // We rely on the controller returning NotOnDoesTheDogDie for non-movies,
        // so the worst case for, say, a TV episode page is a tiny "not on DTDD"
        // pane. That's fine for v1.

        var existingSlot = visible.querySelector('.jt-slot');
        if (existingSlot) {
            return existingSlot;
        }

        var slot = el('div', 'jt-slot');
        var primary = visible.querySelector('.detailPagePrimaryContent')
                   || visible.querySelector('.detailPageContent')
                   || visible;
        primary.appendChild(slot);
        return slot;
    }

    // ---- network ------------------------------------------------------------

    function fetchTriggers(itemId, forceRefresh) {
        return getApiClient().then(function (apiClient) {
            var path = TRIGGERS_PATH + itemId + (forceRefresh ? '/refresh' : '');
            var url = apiClient.getUrl(path);
            return apiClient.ajax({
                url: url,
                type: forceRefresh ? 'POST' : 'GET',
                dataType: 'json',
            });
        });
    }

    function getApiClient() {
        return new Promise(function (resolve, reject) {
            var tries = 0;
            (function check() {
                tries++;
                if (window.ApiClient
                    && typeof window.ApiClient.getUrl === 'function'
                    && typeof window.ApiClient.ajax === 'function') {
                    resolve(window.ApiClient);
                    return;
                }

                if (tries > 50) {
                    reject(new Error('ApiClient never appeared on window.'));
                    return;
                }

                setTimeout(check, 100);
            })();
        });
    }

    // ---- helpers ------------------------------------------------------------

    function injectStylesheet() {
        if (document.querySelector('link[data-jellytriggers="1"]')) {
            return;
        }

        var link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = STYLE_HREF;
        link.setAttribute('data-jellytriggers', '1');
        document.head.appendChild(link);
    }

    function readItemIdFromUrl() {
        var hash = window.location.hash || '';
        // Jellyfin's SPA URLs look like #/details?id=<guid>&serverId=...
        var match = hash.match(/[?&]id=([0-9a-fA-F-]+)/);
        if (!match) {
            return null;
        }

        return normalizeGuid(match[1]);
    }

    function normalizeGuid(raw) {
        var stripped = raw.replace(/-/g, '');
        if (stripped.length !== 32) {
            return raw;
        }

        return stripped.substr(0, 8) + '-'
             + stripped.substr(8, 4) + '-'
             + stripped.substr(12, 4) + '-'
             + stripped.substr(16, 4) + '-'
             + stripped.substr(20, 12);
    }

    function el(tag, cls) {
        var node = document.createElement(tag);
        if (cls) {
            node.className = cls;
        }
        return node;
    }

    function text(node, content) {
        node.textContent = content;
        return node;
    }

    function clear(node) {
        while (node.firstChild) {
            node.removeChild(node.firstChild);
        }
    }
})();

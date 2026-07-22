// ============================================================
// Funding page - deposit card interactions
// ============================================================
//
// Cache lives in localStorage (per Phil's load-balancing concern):
//   - methods:   7 days
//   - addresses: 1 hour
// Keys are scoped per UEI + asset (+ method) so multi-account users don't
// see each other's data. Stale entries are skipped, expired entries removed.

(function () {
    var coinsRoot = document.querySelector('.tp-deposit-coins');
    if (!coinsRoot) return;
    var uei = coinsRoot.getAttribute('data-uei');

    var detail        = document.getElementById('depositDetail');
    var coinNameEl    = document.getElementById('depositCoinName');
    var methodRow     = document.getElementById('depositMethodRow');
    var methodSelect  = document.getElementById('depositMethodSelect');
    var statusEl      = document.getElementById('depositStatus');
    var addrWrap      = document.getElementById('depositAddressWrap');
    var addrCode      = document.getElementById('depositAddressCode');
    var copyBtn       = document.getElementById('depositCopyBtn');
    var generateLink  = document.getElementById('depositGenerateNew');

    var currentAsset  = null;
    var currentMethod = null;

    // ---------- localStorage cache layer ----------
    var METHODS_TTL_MS   = 7 * 24 * 60 * 60 * 1000;   // 7 days
    var ADDRESSES_TTL_MS =     60 * 60 * 1000;        // 1 hour
    var CACHE_PREFIX     = 'tp:funding:v1:';          // bump v1 to invalidate everyone

    function cacheKey(kind, parts) {
        return CACHE_PREFIX + uei + ':' + kind + ':' + parts.join(':');
    }

    function cacheGet(key) {
        try {
            var raw = localStorage.getItem(key);
            if (!raw) return null;
            var parsed = JSON.parse(raw);
            if (!parsed || typeof parsed.exp !== 'number') return null;
            if (Date.now() > parsed.exp) {
                localStorage.removeItem(key);
                return null;
            }
            return parsed.val;
        } catch (_) { return null; }
    }

    function cacheSet(key, val, ttlMs) {
        try {
            localStorage.setItem(key, JSON.stringify({ exp: Date.now() + ttlMs, val: val }));
        } catch (_) { /* quota exceeded or privacy mode - silent fail, just no cache */ }
    }

    function cacheInvalidate(key) {
        try { localStorage.removeItem(key); } catch (_) {}
    }

    // ---------- UI helpers ----------
    function setStatus(msg, kind) {
        statusEl.textContent = msg || '';
        statusEl.className = 'tp-deposit-status' + (kind ? ' ' + kind : '');
        statusEl.style.display = msg ? '' : 'none';
    }

    function showAddress(addresses) {
        if (!addresses || addresses.length === 0) {
            setStatus('No address available yet. Try "Generate a new address" below.', 'warn');
            addrWrap.style.display = '';
            addrCode.textContent = '';
            return;
        }
        addrCode.textContent = addresses[0].address;
        addrWrap.style.display = '';
        setStatus('', '');
    }

    // ---------- Network calls (with cache) ----------
    function loadAddressesFor(asset, method, generate) {
        currentMethod = method;
        addrWrap.style.display = 'none';

        var key = cacheKey('addr', [asset.toUpperCase(), method]);
        if (generate) cacheInvalidate(key);

        if (!generate) {
            var cached = cacheGet(key);
            if (cached) {
                showAddress(cached);
                return;
            }
        }

        setStatus('Loading address' + (generate ? ' (generating new)' : '') + '...');

        var url = '/Funding/Index?handler=DepositAddresses'
                + '&uei=' + encodeURIComponent(uei)
                + '&asset=' + encodeURIComponent(asset)
                + '&method=' + encodeURIComponent(method);
        if (generate) url += '&generate=true';

        fetch(url, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.error) {
                    setStatus('Could not load address: ' + data.error, 'error');
                    return;
                }
                if (data.addresses) cacheSet(key, data.addresses, ADDRESSES_TTL_MS);
                showAddress(data.addresses);
            })
            .catch(function () {
                setStatus('Network error fetching address.', 'error');
            });
    }

    function loadMethodsFor(asset, displayName) {
        currentAsset = asset;
        currentMethod = null;
        coinNameEl.textContent = displayName;
        detail.style.display = '';
        methodRow.style.display = 'none';
        addrWrap.style.display = 'none';

        var key = cacheKey('methods', [asset.toUpperCase()]);
        var cached = cacheGet(key);
        if (cached) {
            handleMethods(cached, asset);
            return;
        }

        setStatus('Loading networks...');

        var url = '/Funding/Index?handler=DepositMethods'
                + '&uei=' + encodeURIComponent(uei)
                + '&asset=' + encodeURIComponent(asset);

        fetch(url, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.error) {
                    setStatus('Could not load networks: ' + data.error, 'error');
                    return;
                }
                if (data.methods) cacheSet(key, data.methods, METHODS_TTL_MS);
                handleMethods(data.methods || [], asset);
            })
            .catch(function () {
                setStatus('Network error fetching networks.', 'error');
            });
    }

    function handleMethods(methods, asset) {
        if (methods.length === 0) {
            setStatus('Kraken returned no deposit methods for this coin.', 'warn');
            return;
        }

        methodSelect.innerHTML = '';
        methods.forEach(function (m) {
            var opt = document.createElement('option');
            opt.value = m.method;
            opt.textContent = m.method;
            methodSelect.appendChild(opt);
        });

        if (methods.length > 1) {
            methodRow.style.display = '';
        }

        loadAddressesFor(asset, methods[0].method, false);
    }

    // ---------- Events ----------
    coinsRoot.addEventListener('click', function (e) {
        var chip = e.target.closest('.tp-coin-chip');
        if (!chip) return;
        coinsRoot.querySelectorAll('.tp-coin-chip.active').forEach(function (c) {
            c.classList.remove('active');
        });
        chip.classList.add('active');
        loadMethodsFor(chip.getAttribute('data-asset'), chip.getAttribute('data-name'));
    });

    methodSelect.addEventListener('change', function () {
        if (!currentAsset) return;
        loadAddressesFor(currentAsset, methodSelect.value, false);
    });

    generateLink.addEventListener('click', function (e) {
        e.preventDefault();
        if (!currentAsset || !currentMethod) return;
        if (!confirm('Generate a new deposit address? Your existing address will continue to work.')) return;
        loadAddressesFor(currentAsset, currentMethod, true);
    });

    document.addEventListener('click', function (e) {
        if (e.target.closest('[data-deposit-close="true"]')) {
            detail.style.display = 'none';
            coinsRoot.querySelectorAll('.tp-coin-chip.active').forEach(function (c) {
                c.classList.remove('active');
            });
        }
    });

    // ---------- Copy ----------
    copyBtn.addEventListener('click', function () {
        var text = addrCode.textContent || '';
        if (!text) return;

        function flashCopied() {
            var orig = copyBtn.textContent;
            copyBtn.textContent = 'Copied!';
            setTimeout(function () { copyBtn.textContent = orig; }, 1500);
        }

        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(flashCopied, function () {
                legacyCopy(text, flashCopied);
            });
        } else {
            legacyCopy(text, flashCopied);
        }
    });

    function legacyCopy(text, onDone) {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); onDone(); }
        catch (_) { /* swallow */ }
        document.body.removeChild(ta);
    }
})();

// =================================================================
// TraderPhil Strategy page — bootstrap-slider wiring
// =================================================================
//
// Two sliders live on this page:
//   1) The "Add a coin" form (one slider, reconfigured when coin changes)
//   2) Each existing strategy's edit form (one slider per row, configured
//      from data-* attrs on the form)
//
// Slider value semantics:
//   - Stored in base units (matches the DcaLotSize column)
//   - min = OrderMin * 2, max = OrderMin * 100, step = OrderMin * 0.1
//   - Min/max labels show USD equivalents using current LastPrice
//   - Below the slider: "Selected: 42 ADA ≈ $9.58 today" with live update
// =================================================================

(function () {
    'use strict';

    // Bail gracefully if bootstrap-slider didn't load (CDN down, ad blocker, etc.)
    if (typeof window.Slider === 'undefined') {
        console.warn('[TraderPhil] bootstrap-slider not loaded. Sliders will be inert.');
        return;
    }

    // -------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------

    function fmtUsd(n) {
        if (!isFinite(n)) return '';
        return '$' + n.toFixed(2);
    }

    function fmtCoin(n, decimals) {
        if (!isFinite(n)) return '—';
        var d = (typeof decimals === 'number' && decimals >= 0 && decimals <= 12) ? decimals : 8;
        // Strip insignificant trailing zeros for readability
        var s = n.toFixed(d);
        if (s.indexOf('.') !== -1) {
            s = s.replace(/0+$/, '').replace(/\.$/, '');
        }
        return s;
    }

    // -------------------------------------------------------------
    // Edit-mode toggling (mirrors what site.js already handles, but
    // we also need to (re)initialize the slider when an edit row opens)
    // -------------------------------------------------------------

    document.addEventListener('click', function (e) {
        var t = e.target.closest('[data-toggle-edit="true"]');
        if (t) {
            var row = t.closest('.tp-strategy-row');
            if (row) {
                row.classList.add('editing');
                initEditSliderForRow(row);
            }
        }
    });

    // -------------------------------------------------------------
    // Edit-row slider initialization
    // -------------------------------------------------------------

    var editSliders = new WeakMap();  // form element -> Slider instance

    function initEditSliderForRow(row) {
        var form = row.querySelector('.tp-edit-form');
        if (!form) return;
        if (editSliders.has(form)) {
            // Already initialized — just resync from the hidden input.
            var existing = editSliders.get(form);
            var hidden = form.querySelector('.tp-edit-lot-value');
            if (hidden) existing.setValue(parseFloat(hidden.value) || existing.getValue());
            return;
        }

        var sliderEl   = form.querySelector('.tp-edit-lot-slider');
        var hiddenEl   = form.querySelector('.tp-edit-lot-value');
        var amountEl   = form.querySelector('.tp-edit-lot-amount');
        var usdEl      = form.querySelector('.tp-edit-lot-usd');
        var minLabelEl = form.querySelector('.tp-edit-slider-min');
        var maxLabelEl = form.querySelector('.tp-edit-slider-max');
        if (!sliderEl || !hiddenEl) return;

        var minV  = parseFloat(form.dataset.sliderMin || '0');
        var maxV  = parseFloat(form.dataset.sliderMax || '0');
        var step  = parseFloat(form.dataset.sliderStep || '0.00000001');
        var initV = parseFloat(form.dataset.initialValue || '0');
        var lastPrice = parseFloat(form.dataset.lastPrice || '0');

        // Clamp the initial value into the slider's valid range so the slider
        // doesn't refuse to render. If the saved DcaLotSize is outside the
        // current OrderMin-derived range (which can happen if Kraken changed
        // their minimums), we snap to the nearest endpoint.
        if (!isFinite(initV) || initV < minV) initV = minV;
        if (initV > maxV) initV = maxV;

        var slider = new Slider(sliderEl, {
            min: minV,
            max: maxV,
            step: step,
            value: initV,
            tooltip: 'hide',
            precision: 8
        });

        function update(value) {
            if (hiddenEl) hiddenEl.value = String(value);
            if (amountEl) amountEl.textContent = fmtCoin(value);
            if (usdEl && lastPrice > 0) {
                usdEl.textContent = ' ≈ ' + fmtUsd(value * lastPrice) + ' today';
            }
        }

        // Min/max USD labels (computed once per init)
        if (minLabelEl && lastPrice > 0) minLabelEl.textContent = fmtUsd(minV * lastPrice);
        if (maxLabelEl && lastPrice > 0) maxLabelEl.textContent = fmtUsd(maxV * lastPrice);

        slider.on('slide',   update);
        slider.on('change',  function (ev) { update(ev.newValue); });

        update(initV);
        editSliders.set(form, slider);
    }

    // Initialize any row that's already in editing state on page load
    // (validation-error redirect path)
    document.querySelectorAll('.tp-strategy-row.editing').forEach(function (row) {
        initEditSliderForRow(row);
    });

    // -------------------------------------------------------------
    // "Add a coin" form slider
    // -------------------------------------------------------------

    (function setupAddSlider() {
        var sel       = document.getElementById('addBaseAsset');
        var sliderEl  = document.getElementById('addLotSlider');
        var hiddenEl  = document.getElementById('addLotValue');
        var amountEl  = document.getElementById('addLotSelectedAmount');
        var unitEl    = document.getElementById('addLotSelectedUnit');
        var usdEl     = document.getElementById('addLotSelectedUsd');
        var minLabel  = document.getElementById('addSliderMinLabel');
        var maxLabel  = document.getElementById('addSliderMaxLabel');
        var staleEl   = document.getElementById('addStaleWarning');
        var shortHid  = document.getElementById('addShortSymbolID');
        if (!sel || !sliderEl || !hiddenEl) return;

        var addables = [];
        try { addables = JSON.parse(sel.dataset.addables || '[]'); }
        catch (e) { addables = []; }

        var byLongId = {};
        addables.forEach(function (a) { byLongId[a.longId] = a; });

        var addSlider = null;

        function applyCoin(a) {
            if (!a) return;

            var orderMin = parseFloat(a.orderMin);
            var minV  = orderMin * 2;
            var maxV  = orderMin * 100;
            var step  = orderMin * 0.1;
            var lp    = a.lastPrice ? parseFloat(a.lastPrice) : 0;

            // Update short symbol pairing
            if (shortHid) shortHid.value = a.shortId;
            if (unitEl)   unitEl.textContent = a.baseAsset;

            // USD labels
            if (minLabel) minLabel.textContent = lp > 0 ? fmtUsd(minV * lp) : '—';
            if (maxLabel) maxLabel.textContent = lp > 0 ? fmtUsd(maxV * lp) : '—';

            // Stale warning
            if (staleEl) staleEl.style.display = a.stale ? '' : 'none';

            // (Re)create the slider. bootstrap-slider doesn't reconfigure
            // cleanly via setAttribute - destroy and rebuild is simpler.
            if (addSlider) {
                addSlider.destroy();
                addSlider = null;
            }

            addSlider = new Slider(sliderEl, {
                min: minV,
                max: maxV,
                step: step,
                value: minV,
                tooltip: 'hide',
                precision: 8
            });

            function update(value) {
                hiddenEl.value = String(value);
                if (amountEl) amountEl.textContent = fmtCoin(value, a.lotDecimals);
                if (usdEl)    usdEl.textContent = lp > 0 ? ' ≈ ' + fmtUsd(value * lp) + ' today' : '';
            }

            addSlider.on('slide',  update);
            addSlider.on('change', function (ev) { update(ev.newValue); });
            update(minV);
        }

        sel.addEventListener('change', function () {
            applyCoin(byLongId[sel.value]);
        });

        // Initial application uses whatever option is selected (first by default)
        applyCoin(byLongId[sel.value]);
    })();

})();

// =================================================================
// ProfitTargets: log-scale Starting Profit slider + reset toggle
// =================================================================

(function setupProfitTargetsCard() {
    if (typeof window.Slider === 'undefined') return;

    // The log-scale slider: 1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000
    // Implementation: the slider works in stop-indices (0..N-1), and a value array
    // gives the human-meaningful dollar amount at each stop. The hidden input
    // submitted to the server is the dollar value, not the index.
    var stops = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    var sliderEl  = document.getElementById('ptStartProfitSlider');
    var hiddenEl  = document.getElementById('ptStartProfitValue');
    var displayEl = document.getElementById('ptStartProfitDisplay');
    if (!sliderEl || !hiddenEl || !displayEl) return;

    var slider = new Slider(sliderEl, {
        min: 0,
        max: stops.length - 1,
        step: 1,
        value: 0,                  // start at $1
        tooltip: 'hide',
        ticks: stops.map(function (_, i) { return i; }),
        ticks_snap_bounds: 0
    });

    function applyStop(idx) {
        var dollars = stops[Math.max(0, Math.min(stops.length - 1, idx | 0))];
        hiddenEl.value = String(dollars);
        displayEl.textContent = '$' + dollars.toLocaleString('en-US');
    }

    slider.on('slide',  applyStop);
    slider.on('change', function (ev) { applyStop(ev.newValue); });

    applyStop(0);
})();

// Toggle the scary reset form.
document.addEventListener('click', function (e) {
    var open = e.target.closest('[data-toggle-pt-reset="true"]');
    if (open) {
        var f = document.getElementById('ptResetForm');
        if (f) f.classList.add('open');
        return;
    }
    var close = e.target.closest('[data-cancel-pt-reset="true"]');
    if (close) {
        var f = document.getElementById('ptResetForm');
        if (f) f.classList.remove('open');
        // Clear the confirmation text on cancel so a stray "Destroy Profits"
        // can't get accidentally resubmitted later.
        var t = document.getElementById('ptConfirmText');
        if (t) t.value = '';
        return;
    }
});

// =================================================================
// Initialize Profit Targets button — busy state on submit
// =================================================================
//
// When the form posts, the page does a full reload. We just need to
// indicate "we heard you" between the click and the server response.
// No teardown needed — the next render gives us a fresh button.
(function () {
    var btn = document.getElementById('ptInitButton');
    if (!btn) return;
    var form = btn.closest('form');
    if (!form) return;

    form.addEventListener('submit', function () {
        // HTML5 validation runs before submit fires, so if we got here the
        // form is at least client-side valid. Server might still reject.
        var label = btn.querySelector('.tp-pt-init-btn-label');
        var busyText = btn.getAttribute('data-busy-text') || 'Working...';
        if (label) label.textContent = busyText;
        btn.classList.add('busy');
        btn.disabled = true;
    });
})();

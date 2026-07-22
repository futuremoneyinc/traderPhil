// -------- Inline day-row expansion --------
// Each .tp-day-row has a sibling .tp-day-detail immediately after it.
// Clicking the row toggles the detail open/closed.
document.addEventListener('click', function (e) {
    var row = e.target.closest('.tp-day-row');
    if (!row) return;
    // Ignore clicks that originate on a sortable header inside the detail panel.
    if (e.target.closest('.tp-detail-table thead')) return;

    var detail = row.nextElementSibling;
    if (!detail || !detail.classList.contains('tp-day-detail')) return;

    var open = detail.classList.toggle('open');
    row.setAttribute('aria-expanded', open ? 'true' : 'false');
});

// -------- Column sorting inside expanded detail tables --------
// Three-state per column: default -> asc -> desc -> default.
// Default ordering is whatever the server rendered.
document.addEventListener('click', function (e) {
    var th = e.target.closest('.tp-detail-table thead th[data-sort-key]');
    if (!th) return;

    var table = th.closest('.tp-detail-table');
    var key   = th.getAttribute('data-sort-key');
    var type  = th.getAttribute('data-sort-type') || 'string';
    var curr  = th.getAttribute('data-sort-dir'); // null | 'asc' | 'desc'

    // Clear sort dir on all headers
    table.querySelectorAll('thead th[data-sort-key]').forEach(function (h) {
        h.removeAttribute('data-sort-dir');
    });

    var nextDir;
    if (curr === null)        nextDir = 'asc';
    else if (curr === 'asc')  nextDir = 'desc';
    else                      nextDir = null; // back to default

    if (nextDir) th.setAttribute('data-sort-dir', nextDir);

    var tbody = table.querySelector('tbody');
    var rows  = Array.from(tbody.querySelectorAll('tr'));

    if (nextDir === null) {
        // Restore original order from data-original-index
        rows.sort(function (a, b) {
            return parseInt(a.dataset.originalIndex, 10) - parseInt(b.dataset.originalIndex, 10);
        });
    } else {
        rows.sort(function (a, b) {
            var av = a.querySelector('[data-cell="' + key + '"]').getAttribute('data-value');
            var bv = b.querySelector('[data-cell="' + key + '"]').getAttribute('data-value');

            var cmp;
            if (type === 'number') {
                cmp = parseFloat(av) - parseFloat(bv);
            } else if (type === 'date') {
                cmp = (new Date(av)) - (new Date(bv));
            } else {
                cmp = (av || '').localeCompare(bv || '');
            }
            return nextDir === 'asc' ? cmp : -cmp;
        });
    }

    rows.forEach(function (r) { tbody.appendChild(r); });
});


// =================================================================
// Strategy page: shared edit-toggle / add-form / treasury handlers
// (Slider-specific logic lives in strategy.js)
// =================================================================

document.addEventListener('click', function (e) {
    // Cancel an edit
    var c = e.target.closest('[data-cancel-edit="true"]');
    if (c) {
        var row = c.closest('.tp-strategy-row');
        if (row) row.classList.remove('editing');
        return;
    }
    // Toggle treasury edit on
    var ta = e.target.closest('[data-toggle-edit-treasury="true"]');
    if (ta) {
        var row = ta.closest('.tp-strategy-row');
        if (row) row.classList.add('editing');
        return;
    }
    // Cancel treasury edit
    var tc = e.target.closest('[data-cancel-edit-treasury="true"]');
    if (tc) {
        var row = tc.closest('.tp-strategy-row');
        if (row) row.classList.remove('editing');
        return;
    }
    // Toggle "+ Add a coin" form
    var addToggle = e.target.closest('[data-toggle-add="true"]');
    if (addToggle) {
        var f = document.getElementById('addForm');
        if (f) {
            var wasOpen = f.classList.toggle('open');
            addToggle.textContent = wasOpen ? 'Cancel' : '+ Add a coin';
        }
        return;
    }
    // Cancel "+ Add a coin" form
    var addCancel = e.target.closest('[data-cancel-add="true"]');
    if (addCancel) {
        var f = document.getElementById('addForm');
        if (f) f.classList.remove('open');
        var btn = document.querySelector('[data-toggle-add="true"]');
        if (btn) btn.textContent = '+ Add a coin';
        return;
    }
});

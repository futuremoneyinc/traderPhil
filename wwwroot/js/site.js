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

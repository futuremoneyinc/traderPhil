// =============================================================================
// Account page interactions
// =============================================================================
//
// Just the Replace-key form toggle. Everything else (privacy switch, progress
// frequency radios) submits via the form's onchange handler set in Razor.

document.addEventListener('click', function (e) {
    var open = e.target.closest('[data-toggle-replace="true"]');
    if (open) {
        var f = document.getElementById('apiReplaceForm');
        if (f) f.classList.add('open');
        return;
    }
    var close = e.target.closest('[data-cancel-replace="true"]');
    if (close) {
        var f = document.getElementById('apiReplaceForm');
        if (f) f.classList.remove('open');
        return;
    }
});

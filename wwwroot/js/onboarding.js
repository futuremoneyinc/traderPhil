// ============================================================================
// Onboarding wizard client behaviour. Progressive enhancement only — every step
// still works with JS disabled (the server renders a valid preview and the
// forms post normally). This just makes it feel alive.
// ============================================================================
(function () {
    "use strict";

    var usd0 = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });
    var usd2 = new Intl.NumberFormat("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    // ---- Profit Ladder live preview (Goals step) -------------------------
    // Mirrors Models/OnboardingModels.cs → ProfitLadderPreview.Compute and
    // OnboardingGoals.StartProfitFromFunding so the estimate matches the server.
    function startProfitFromFunding(funding) {
        var raw = Math.round(funding * 0.0025 * 100) / 100;
        if (raw < 1) return 1;
        if (raw > 10000) return 10000;
        return raw;
    }

    function computeLadder(startProfit, growthPct, target, cap, numSeq) {
        var SAFETY = 20000;
        if (startProfit < 1) startProfit = 1;
        if (target <= startProfit) {
            return { rungs: 0, first: startProfit, largest: startProfit, total: 0 };
        }
        var perSeries = target / numSeq;
        var mult = 1 + growthPct;
        var rungs = 0, sum = 0, current = startProfit, largest = startProfit;
        while (sum <= perSeries) {
            var clamped = current > cap ? cap : current;
            sum += clamped;
            if (clamped > largest) largest = clamped;
            rungs++;
            current = clamped * mult;
            if (rungs >= SAFETY) break;
        }
        return { rungs: rungs * numSeq, first: startProfit, largest: largest, total: sum * numSeq };
    }

    function initGoals() {
        var form = document.getElementById("tpGoalsForm");
        if (!form) return;

        var cap = parseFloat(form.getAttribute("data-cap")) || 10000;
        var numSeq = parseInt(form.getAttribute("data-numseq"), 10) || 10;
        var fundingInput = document.getElementById("tpFunding");
        var targetInput = document.getElementById("tpTarget");
        var preview = document.getElementById("tpLadderPreview");
        if (!fundingInput || !targetInput || !preview) return;

        function selectedGrowth() {
            var checked = form.querySelector('input[name="InvestmentGoal"]:checked');
            return checked ? (parseFloat(checked.getAttribute("data-growth")) || 0.0125) : 0.0125;
        }

        function set(name, value) {
            var el = preview.querySelector('[data-preview="' + name + '"]');
            if (el) el.textContent = value;
        }

        function refresh() {
            var funding = parseFloat(fundingInput.value) || 0;
            var target = parseFloat(targetInput.value) || 0;
            var start = startProfitFromFunding(funding);
            var r = computeLadder(start, selectedGrowth(), target, cap, numSeq);
            set("first", "$" + usd2.format(r.first));
            set("rungs", usd0.format(r.rungs));
            set("largest", "$" + usd2.format(r.largest));
            set("total", "$" + usd0.format(r.total));
        }

        form.addEventListener("change", refresh);
        fundingInput.addEventListener("input", refresh);
        targetInput.addEventListener("input", refresh);

        // Target preset chips fill the number input.
        var chips = form.querySelectorAll(".tp-ob-target-chip");
        Array.prototype.forEach.call(chips, function (chip) {
            chip.addEventListener("click", function () {
                targetInput.value = chip.getAttribute("data-target");
                Array.prototype.forEach.call(chips, function (c) { c.classList.remove("active"); });
                chip.classList.add("active");
                refresh();
            });
        });

        refresh();
    }

    // ---- Secret show/hide (Connect step) ---------------------------------
    function initSecretToggles() {
        var toggles = document.querySelectorAll("[data-secret-toggle]");
        Array.prototype.forEach.call(toggles, function (btn) {
            btn.addEventListener("click", function () {
                var target = document.getElementById(btn.getAttribute("data-secret-toggle"));
                if (!target) return;
                if (target.type === "password") {
                    target.type = "text";
                    btn.textContent = "Hide";
                } else {
                    target.type = "password";
                    btn.textContent = "Show";
                }
            });
        });
    }

    // ---- Walkthrough video placeholder (Connect step) --------------------
    function initVideo() {
        var wraps = document.querySelectorAll(".tp-ob-video");
        Array.prototype.forEach.call(wraps, function (wrap) {
            var btn = wrap.querySelector(".tp-ob-video-play");
            if (!btn) return;
            btn.addEventListener("click", function () {
                var src = wrap.getAttribute("data-video-src");
                var frame = wrap.querySelector(".tp-ob-video-frame");
                if (src && frame) {
                    var iframe = document.createElement("iframe");
                    iframe.src = src;
                    iframe.width = "100%";
                    iframe.height = "100%";
                    iframe.allow = "accelerometer; autoplay; encrypted-media; picture-in-picture";
                    iframe.setAttribute("allowfullscreen", "");
                    iframe.style.border = "0";
                    frame.innerHTML = "";
                    frame.appendChild(iframe);
                } else if (frame) {
                    var cap = frame.querySelector(".tp-ob-video-caption");
                    if (cap) cap.textContent = "Walkthrough video coming soon — the 5 steps on the right have you covered.";
                }
            });
        });
    }

    // ---- Busy state on submit (Verify step) ------------------------------
    function initBusyForms() {
        var forms = document.querySelectorAll("[data-busy-form]");
        Array.prototype.forEach.call(forms, function (form) {
            form.addEventListener("submit", function () {
                var btn = form.querySelector(".tp-ob-btn-busy");
                if (btn) {
                    btn.classList.add("is-busy");
                    btn.disabled = true;
                    var label = btn.querySelector(".tp-ob-btn-label");
                    if (label) label.textContent = "Checking…";
                }
            });
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        initGoals();
        initSecretToggles();
        initVideo();
        initBusyForms();
    });
})();

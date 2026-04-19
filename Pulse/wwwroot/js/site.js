// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// ── Theme toggle ─────────────────────────────────────────────────────────────
(function () {
    const STORAGE_KEY = 'bp-theme';

    function applyTheme(dark) {
        if (dark) {
            document.documentElement.setAttribute('data-theme', 'dark');
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
        localStorage.setItem(STORAGE_KEY, dark ? 'dark' : 'light');
    }

    function syncSettingsUI() {
        const toggle = document.getElementById('darkModeToggle');
        const desc   = document.getElementById('themeDesc');
        if (!toggle) return;

        const isDark = localStorage.getItem(STORAGE_KEY) === 'dark';
        toggle.checked = isDark;
        if (desc) desc.textContent = isDark ? 'Dark mode is active.' : 'Light mode is active.';

        toggle.addEventListener('change', function () {
            applyTheme(this.checked);
            if (desc) desc.textContent = this.checked ? 'Dark mode is active.' : 'Light mode is active.';
        });
    }

    // Run after DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', syncSettingsUI);
    } else {
        syncSettingsUI();
    }
})();

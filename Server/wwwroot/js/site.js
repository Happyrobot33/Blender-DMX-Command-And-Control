// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Forms with a data-confirm attribute ask before submitting (used by job Delete buttons).
document.addEventListener('submit', (event) => {
    const message = event.target.dataset?.confirm;
    if (message && !window.confirm(message)) event.preventDefault();
});

// Timestamps are rendered in UTC (<time datetime="...Z" data-local-time>, see LocalTimeTagHelper);
// show them in the viewer's own timezone, with the UTC time on hover.
(() => {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const formats = {
        minutes: new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }),
        seconds: new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' }),
    };
    document.querySelectorAll('time[data-local-time]').forEach((el) => {
        const date = new Date(el.getAttribute('datetime'));
        if (Number.isNaN(date.getTime())) return;
        el.title = `${el.textContent.trim()} (shown in ${zone})`;
        el.textContent = (formats[el.dataset.localTime] ?? formats.minutes).format(date);
    });
})();

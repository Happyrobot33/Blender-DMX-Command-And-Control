import { filesize } from '../lib/filesize/filesize.js';

(() => {
    const form = document.getElementById('upload-form');
    if (!form) return;

    const nameInput = document.getElementById('job-name');
    const taskCountInput = document.getElementById('task-count');
    const folderInput = document.getElementById('project-folder');
    const configInput = document.getElementById('hnode-config');
    const summary = document.getElementById('selection-summary');
    const progressWrap = document.getElementById('progress-wrap');
    const progressBar = document.getElementById('progress-bar');
    const errorBox = document.getElementById('upload-error');
    const button = document.getElementById('upload-button');
    const maxBytes = Number(form.dataset.maxBytes);

    // Same units as the server's Humanizer output: 1024-based, "KB"/"MB"/"GB".
    const formatBytes = (bytes) => filesize(bytes, { base: 2, standard: 'jedec', round: 2 });

    const showError = (message) => {
        errorBox.textContent = message;
        errorBox.classList.remove('d-none');
    };

    const totalSize = (files) => Array.from(files).reduce((sum, f) => sum + f.size, 0);

    // webkitRelativePath is "<picked folder>/<sub/path>"; the picked folder name is sent once
    // and stripped so its contents land directly in the job's Source directory.
    const rootFolderOf = (files) => files[0].webkitRelativePath.split('/')[0];
    const relativePathOf = (file, rootFolder) => {
        const rel = file.webkitRelativePath || file.name;
        const prefix = `${rootFolder}/`;
        return rel.startsWith(prefix) ? rel.slice(prefix.length) : rel;
    };
    // Matches the server rule: ".blend" only, so Blender's .blend1/.blend2 backups are excluded.
    const isBlendFile = (path) => path.toLowerCase().endsWith('.blend');

    const blendWrap = document.getElementById('blend-wrap');
    const blendSelect = document.getElementById('main-blend-file');
    const blendHelp = document.getElementById('blend-help');

    const populateBlendFiles = (blendPaths) => {
        blendSelect.replaceChildren();
        blendWrap.classList.toggle('d-none', blendPaths.length === 0);
        if (blendPaths.length === 0) return;

        if (blendPaths.length > 1) {
            blendSelect.append(new Option('Choose the .blend file to use…', '', true, true));
            blendSelect.options[0].disabled = true;
            blendHelp.textContent = `${blendPaths.length} .blend files found - choose the one this job should use.`;
        } else {
            blendHelp.textContent = 'Only one .blend file found - selected automatically.';
        }
        for (const path of blendPaths) blendSelect.append(new Option(path, path));
        if (blendPaths.length === 1) blendSelect.value = blendPaths[0];
    };

    folderInput.addEventListener('change', () => {
        errorBox.classList.add('d-none');
        const files = Array.from(folderInput.files);
        if (!files.length) { summary.textContent = ''; populateBlendFiles([]); return; }

        const root = rootFolderOf(files);
        summary.textContent = `${root}: ${files.length} files, ${formatBytes(totalSize(files))}`;

        const blendPaths = files.map((f) => relativePathOf(f, root)).filter(isBlendFile).sort();
        populateBlendFiles(blendPaths);
        if (blendPaths.length === 0) showError('This folder has no .blend file. Pick the Blender project folder.');
    });

    form.addEventListener('submit', (event) => {
        event.preventDefault();
        errorBox.classList.add('d-none');

        const files = Array.from(folderInput.files);
        if (!files.length) { showError('Choose a project folder first.'); return; }
        if (totalSize(files) > maxBytes) { showError(`The folder is larger than the ${formatBytes(maxBytes)} limit.`); return; }
        if (blendSelect.options.length === 0) { showError('This folder has no .blend file. Pick the Blender project folder.'); return; }
        if (!blendSelect.value) { showError('Choose which .blend file this job should use.'); blendSelect.focus(); return; }
        const taskCount = Number(taskCountInput.value);
        if (!Number.isInteger(taskCount) || taskCount < Number(taskCountInput.min) || taskCount > Number(taskCountInput.max)) {
            showError(`Sub tasks must be a whole number from ${taskCountInput.min} to ${taskCountInput.max}.`); taskCountInput.focus(); return;
        }
        const config = configInput.files[0];
        if (!config) { showError('Attach the HNode config (.shwcfg) file.'); configInput.focus(); return; }
        if (!/.\.shwcfg$/i.test(config.name)) { showError('The HNode config must be a .shwcfg file.'); configInput.focus(); return; }

        const rootFolder = rootFolderOf(files);

        // Field order matters: the server needs these before the first file arrives.
        const data = new FormData();
        data.append('name', nameInput.value.trim());
        data.append('taskCount', String(taskCount));
        data.append('sourceFolderName', rootFolder);
        data.append('mainBlendFile', blendSelect.value);
        data.append('config', config, config.name); // must precede the project files
        for (const file of files) {
            data.append('path', relativePathOf(file, rootFolder));
            data.append('file', file, file.name);
        }

        const token = form.querySelector('input[name="__RequestVerificationToken"]').value;
        const xhr = new XMLHttpRequest();
        xhr.open('POST', form.action);
        xhr.setRequestHeader('RequestVerificationToken', token);
        xhr.responseType = 'json';

        xhr.upload.addEventListener('progress', (e) => {
            if (!e.lengthComputable) return;
            const pct = Math.floor((e.loaded / e.total) * 100);
            progressBar.style.width = `${pct}%`;
            progressBar.textContent = pct === 100 ? 'Finishing upload…' : `${pct}%`;
        });

        xhr.addEventListener('load', () => {
            if (xhr.status >= 200 && xhr.status < 300 && xhr.response?.detailsUrl) {
                window.location.href = xhr.response.detailsUrl;
                return;
            }
            showError(xhr.response?.error ?? `Upload failed (HTTP ${xhr.status}).`);
            reset();
        });
        xhr.addEventListener('error', () => { showError('Upload failed: network error.'); reset(); });

        const reset = () => {
            button.disabled = false;
            folderInput.disabled = false;
        configInput.disabled = false;
        taskCountInput.disabled = false;
            blendSelect.disabled = false;
            progressWrap.classList.add('d-none');
        };

        button.disabled = true;
        folderInput.disabled = true;
        configInput.disabled = true;
        taskCountInput.disabled = true;
        blendSelect.disabled = true;
        progressBar.style.width = '0%';
        progressBar.textContent = '0%';
        progressWrap.classList.remove('d-none');
        xhr.send(data);
    });
})();
